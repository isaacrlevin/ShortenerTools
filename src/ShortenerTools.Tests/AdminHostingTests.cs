using System.Net;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Protocols;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShortenerTools.Admin;

namespace ShortenerTools.Tests;

[TestClass]
public class AdminHostingTests
{
    [TestMethod]
    [DataRow("/urlmanager")]
    [DataRow("/")]
    [DataRow("/socialposter")]
    [DataRow("/socialposter/links")]
    [DataRow("/socialposter/settings")]
    [DataRow("/socialposter/contacts")]
    [DataRow("/socialposter/history")]
    [DataRow("/socialposter/statistics?link=my-event")]
    [DataRow("/statistics")]
    [DataRow("/auth/login")]
    public async Task ProtectedPagesChallengeEntraWithoutCallingManagementApi(string path)
    {
        await using var application = new AdminFactory();
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost")
        });
        using var response = await client.GetAsync(path);
        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        StringAssert.StartsWith(response.Headers.Location!.AbsoluteUri, "https://identity.example/authorize");
        StringAssert.Contains(response.Headers.Location.Query, "redirect_uri=https%3A%2F%2Flocalhost%2Fsignin-oidc");
    }

    [TestMethod]
    public async Task AnonymousLoginPageDoesNotLeakServerSecrets()
    {
        await using var application = new AdminFactory();
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        using var response = await client.GetAsync("/login");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.IsFalse(body.Contains("integration-test-api-key", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("integration-test-client-secret", StringComparison.Ordinal));
        StringAssert.Contains(body, "blazor.web.js");
    }

    [TestMethod]
    public async Task FailedMutationIsNotAutomaticallyRetried()
    {
        await using var application = new AdminFactory();
        using var client = application.Services.GetRequiredService<IHttpClientFactory>().CreateClient("management");
        using var response = await client.PostAsync("/api/UrlCreate", new StringContent("{}"));
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual(1, application.ManagementHandler.RequestCount);
    }

    private sealed class AdminFactory : WebApplicationFactory<AdminApplication>
    {
        public UnavailableHandler ManagementHandler { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Entra:TenantId"] = "11111111-1111-1111-1111-111111111111",
                    ["Entra:ClientId"] = "22222222-2222-2222-2222-222222222222",
                    ["Entra:ClientSecret"] = "integration-test-client-secret",
                    ["Entra:AdministratorObjectId"] = "33333333-3333-3333-3333-333333333333",
                    ["AdminApiKey"] = "integration-test-api-key",
                    ["services:management-api:https:0"] = "https://management.example"
                }));
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("management").ConfigurePrimaryHttpMessageHandler(() => ManagementHandler);
                services.AddHttpClient("description").ConfigurePrimaryHttpMessageHandler(() => ManagementHandler);
                services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
                {
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                        new OpenIdConnectConfiguration
                        {
                            Issuer = "https://identity.example",
                            AuthorizationEndpoint = "https://identity.example/authorize",
                            TokenEndpoint = "https://identity.example/token"
                        });
                });
            });
        }
    }

    [TestMethod]
    public async Task ManagementClientOutlastsAManagementApiColdStart()
    {
        await using var application = new AdminFactory();
        var options = application.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<
                Microsoft.Extensions.Http.Resilience.HttpStandardResilienceOptions>>()
            .Get("management-standard");
        Assert.AreEqual(TimeSpan.FromSeconds(60), options.AttemptTimeout.Timeout);
        Assert.AreEqual(TimeSpan.FromSeconds(90), options.TotalRequestTimeout.Timeout);
    }

    [TestMethod]
    public async Task DescriptionClientAllowsSlowResponsesWithoutRetryingPosts()
    {
        await using var application = new AdminFactory();
        using var client = application.Services.GetRequiredService<IHttpClientFactory>().CreateClient("description");
        Assert.AreEqual(Timeout.InfiniteTimeSpan, client.Timeout);
        var options = application.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<
                Microsoft.Extensions.Http.Resilience.HttpStandardResilienceOptions>>()
            .Get("description-standard");
        Assert.AreEqual(TimeSpan.FromSeconds(120), options.AttemptTimeout.Timeout);
        Assert.AreEqual(TimeSpan.FromSeconds(120), options.TotalRequestTimeout.Timeout);
        application.ManagementHandler.Delay = TimeSpan.FromSeconds(11);
        using var response = await client.PostAsync("/api/CreateDescription", new StringContent("{}"));
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual(1, application.ManagementHandler.RequestCount);
    }

    private sealed class UnavailableHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public TimeSpan Delay { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            await Task.Delay(Delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
    }
}