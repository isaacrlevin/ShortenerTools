using ShortenerTools.Core.Domain;
using ShortenerTools.Functions;
using ShortenerTools.Core.Domain.Coffee;
using ShortenerTools.Core.Domain.Socials;
using ShortenerTools.Functions.Socials;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SocialPoster.Posting.Services;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();
var socialAccountsConnectionString = builder.Configuration.GetConnectionString("social-accounts");
if (!string.IsNullOrWhiteSpace(socialAccountsConnectionString))
{
    builder.Configuration.AddAzureAppConfiguration(options => options
        .Connect(socialAccountsConnectionString)
        .Select("Shortener:*")
        .TrimKeyPrefix("Shortener:")
        .ConfigureRefresh(refresh => refresh
            .Register("SocialPoster:Accounts:Sentinel", refreshAll: true)
            .SetRefreshInterval(TimeSpan.FromSeconds(5))));
    builder.Services.AddAzureAppConfiguration();
    builder.UseMiddleware<AzureAppConfigurationRefreshMiddleware>();
}

builder.UseMiddleware<AdminApiKeyMiddleware>();


ShortenerSettings shortenerSettings = new ShortenerSettings();

builder.ConfigureFunctionsTelemetry();

builder.Configuration.Bind(shortenerSettings);
// Use the host environment, not a potentially overridden settings value.
shortenerSettings.EnvironmentName = builder.Environment.EnvironmentName;
var tableConnectionString = builder.Configuration.GetConnectionString("tables");
var tableEndpoint = builder.Configuration["tables:tableServiceUri"];
if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DataStorage")))
{
    shortenerSettings.DataStorage = Environment.GetEnvironmentVariable("DataStorage")!;
}
else if (!string.IsNullOrWhiteSpace(tableConnectionString))
{
    shortenerSettings.DataStorage = tableConnectionString;
}
else if (!string.IsNullOrWhiteSpace(tableEndpoint))
{
    shortenerSettings.DataStorage = tableEndpoint;
}

builder.Services.AddHttpClient();
builder.Services.AddHttpClient<ICoffeeGuestFeed, CoffeeGuestFeed>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ISocialMediaPublisher, SocialPosterPublisher>();
// Credentials must never follow a redirect to another host (matches SocialPoster's own registrations).
builder.Services.AddHttpClient(nameof(MastodonClient))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient(nameof(ThreadsClient))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<EmailService, EmailService>();
builder.Services.AddOptions<KestrelServerOptions>()
    .Configure<IConfiguration>((settings, configuration) =>
    {
        settings.AllowSynchronousIO = true;
        configuration.Bind(settings);
    });

builder.Logging.Services.Configure<LoggerFilterOptions>(options =>
{
    LoggerFilterRule defaultRule = options.Rules.FirstOrDefault(rule =>
        rule.ProviderName == "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider");

    if (defaultRule is not null)
    {
        options.Rules.Remove(defaultRule);
    }
});

builder.Services.AddSingleton(options => { return shortenerSettings; });
builder.Services.AddSingleton(_ => new StorageTableHelper(shortenerSettings.DataStorage));

if (!builder.Environment.IsStaging() &&
    builder.Configuration.GetValue("EnableDescriptionGeneration", true))
{
    builder.AddAIServices();
}

var app = builder.Build();
app.Run();