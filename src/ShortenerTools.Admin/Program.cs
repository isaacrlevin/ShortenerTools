using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Http.Resilience;
using MudBlazor.Services;
using SocialPoster.Components;
using SocialPoster.Core;
using SocialPoster.Core.Scheduling;
using ShortenerTools.Admin;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults(options => options.Retry.DisableForUnsafeHttpMethods());
builder.Services.AddMudServices();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();

var socialPosterDataDirectory = builder.Configuration["SocialPoster:DataDirectory"]
                                ?? Path.Combine(builder.Environment.ContentRootPath, "socialposter-data");
builder.Services.AddSocialPosterCore(options =>
{
    builder.Configuration.GetSection("SocialPoster").Bind(options);
    options.DataDirectory = socialPosterDataDirectory;
});
builder.Services.AddSocialPosterScheduling(options =>
    builder.Configuration.GetSection("SocialPoster:Scheduling").Bind(options));
builder.Services.AddSocialPosterSchedulingWorker();
builder.Services.AddSingleton<SocialPosterStorage>();
builder.Services.AddHostedService(services => services.GetRequiredService<SocialPosterStorage>());
builder.Services.AddSocialPosterComponents(options => options.BasePath = "socialposter");
builder.Services.AddHostedService<SocialAccountsConfigurationPublisher>();
builder.Services.AddScoped<ComposerLinkWorkflow>();
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(Path.GetFullPath(socialPosterDataDirectory), "keys")))
    .SetApplicationName("ShortenerTools.Admin");

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie(options =>
    {
        options.Cookie.Name = "__Host-ShortenerAdmin";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.AccessDeniedPath = "/unauthorized";
    })
    .AddOpenIdConnect(options =>
    {
        var tenantId = RequiredGuidSetting("Entra:TenantId");
        var administratorObjectId = RequiredGuidSetting("Entra:AdministratorObjectId");
        options.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
        options.ClientId = RequiredGuidSetting("Entra:ClientId");
        options.ClientSecret = RequiredSetting("Entra:ClientSecret");
        options.ResponseType = "code";
        options.UsePkce = true;
        options.MapInboundClaims = false;
        options.TokenValidationParameters.NameClaimType = "name";
        options.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;
        options.Events.OnTokenValidated = context =>
        {
            if (!AdministratorAccess.IsAllowed(context.Principal, tenantId, administratorObjectId))
            {
                context.Fail("This account is not the configured administrator.");
            }
            else if (context.Principal?.Identity is ClaimsIdentity identity)
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, "admin"));
            }

            return Task.CompletedTask;
        };
        options.Events.OnRemoteFailure = context =>
        {
            context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("EntraAuthentication").LogWarning(context.Failure, "Administrator sign-in failed.");
            context.HandleResponse();
            context.Response.Redirect("/unauthorized");
            return Task.CompletedTask;
        };
    });
builder.Services.AddOptions<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme)
    .Validate(_ => !string.IsNullOrWhiteSpace(builder.Configuration["AdminApiKey"]),
        "AdminApiKey must be configured.")
    .ValidateOnStart();
builder.Services.AddAuthorization();
#pragma warning disable EXTEXP0001 // Replace inherited resilience for requests that outlast its 10s attempt timeout.
// A management API cold start (image pull plus Functions host start) takes about 20s.
builder.Services.AddHttpClient("management", client =>
    {
        client.BaseAddress = new Uri("https+http://management-api");
        client.DefaultRequestHeaders.Add("X-Admin-Api-Key", RequiredSetting("AdminApiKey"));
    })
    .RemoveAllResilienceHandlers()
    .AddStandardResilienceHandler(options =>
    {
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(60);
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(90);
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(120);
        options.Retry.DisableForUnsafeHttpMethods();
    });
builder.Services.AddHttpClient("description", client =>
    {
        client.BaseAddress = new Uri("https+http://management-api");
        client.DefaultRequestHeaders.Add("X-Admin-Api-Key", RequiredSetting("AdminApiKey"));
        client.Timeout = TimeSpan.FromSeconds(130);
    })
    .RemoveAllResilienceHandlers()
    .AddStandardResilienceHandler(options =>
    {
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(120);
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(120);
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(240);
        options.Retry.DisableForUnsafeHttpMethods();
    });
#pragma warning restore EXTEXP0001
builder.Services.AddScoped(services =>
    services.GetRequiredService<IHttpClientFactory>().CreateClient("management"));
builder.Services.AddScoped<ShortLinkClient>();

var app = builder.Build();
Directory.CreateDirectory(Path.Combine(Path.GetFullPath(socialPosterDataDirectory), "keys"));
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
}

// SocialPosterStorage opens the database after the host is listening; hold requests briefly until it is ready.
var socialPosterStorage = app.Services.GetRequiredService<SocialPosterStorage>();
app.Use(async (context, next) =>
{
    if (socialPosterStorage.IsReady ||
        await socialPosterStorage.WaitAsync(TimeSpan.FromSeconds(15), context.RequestAborted))
    {
        await next();
        return;
    }

    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
    context.Response.Headers.RetryAfter = "5";
    await context.Response.WriteAsync("SocialPoster is starting. Refresh in a few seconds.");
});

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapGet("/auth/login", () => Results.Challenge(
    new AuthenticationProperties { RedirectUri = "/" },
    [OpenIdConnectDefaults.AuthenticationScheme]));
app.MapGet("/error", () => Results.Problem("The administrator application could not complete this request."));
app.MapPost("/auth/logout", async (HttpContext context,
    Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery) =>
{
    await antiforgery.ValidateRequestAsync(context);
    return Results.SignOut(new AuthenticationProperties { RedirectUri = "/" },
        [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]);
}).RequireAuthorization();
app.MapRazorComponents<ShortenerTools.Admin.App>().AddInteractiveServerRenderMode();
app.MapDefaultEndpoints();
app.Run();

string RequiredSetting(string name) =>
    !string.IsNullOrWhiteSpace(builder.Configuration[name])
        ? builder.Configuration[name]!
        : throw new InvalidOperationException($"The secret or setting '{name}' must be configured.");

string RequiredGuidSetting(string name) =>
    Guid.TryParse(RequiredSetting(name), out var value) && value != Guid.Empty
        ? value.ToString()
        : throw new InvalidOperationException($"The setting '{name}' must be a non-empty GUID.");