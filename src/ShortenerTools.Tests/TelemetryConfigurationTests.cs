using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.ApplicationInsights;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenTelemetry.Exporter;
using ShortenerTools.Functions;

namespace ShortenerTools.Tests;

[TestClass]
public sealed class TelemetryConfigurationTests
{
    private const string ConnectionString =
        "InstrumentationKey=00000000-0000-0000-0000-000000000001;IngestionEndpoint=https://ingestion.example/";

    // Inspect registration/options only: no host start, exports or network calls.
    private static HostApplicationBuilder CreateBuilder() => Host.CreateEmptyApplicationBuilder(
        new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void NoConnectionStringDoesNotRegisterAzureMonitor(string? connectionString)
    {
        var builder = CreateBuilder();
        builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"] = connectionString;
        builder.ConfigureOpenTelemetry();

        Assert.IsFalse(builder.Services.Any(s =>
            s.ServiceType == typeof(IConfigureOptions<AzureMonitorExporterOptions>)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AzureMonitorUsesConfiguredConnectionAndCanCoexistWithOtlp(bool useOtlp)
    {
        var builder = CreateBuilder();
        builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"] = ConnectionString;
        if (useOtlp)
            builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://localhost:4317";
        builder.ConfigureOpenTelemetry();
        using var services = builder.Services.BuildServiceProvider();

        var options = services.GetRequiredService<IOptions<AzureMonitorExporterOptions>>().Value;
        Assert.AreEqual(ConnectionString, options.ConnectionString);
        Assert.IsFalse(options.EnableLiveMetrics);
        Assert.AreEqual(useOtlp, builder.Services.Any(s =>
            s.ServiceType == typeof(IOptionsFactory<OtlpExporterOptions>)));
    }

    [TestMethod]
    public void LocalDashboardUsesOtlpWithoutInsights()
    {
        var builder = CreateBuilder();
        builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://localhost:4317";
        builder.ConfigureOpenTelemetry();
        Assert.IsTrue(builder.Services.Any(s =>
            s.ServiceType == typeof(IOptionsFactory<OtlpExporterOptions>)));
        Assert.IsFalse(builder.Services.Any(s =>
            s.ServiceType == typeof(IConfigureOptions<AzureMonitorExporterOptions>)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FunctionsOnlyRegisterLegacySdkOutsideAspire(bool inAspire)
    {
        var builder = CreateBuilder();
        builder.Configuration["IN_ASPIRE"] = inAspire.ToString();
        builder.ConfigureFunctionsTelemetry();
        Assert.AreEqual(!inAspire, builder.Services.Any(s => s.ServiceType == typeof(TelemetryClient)));
        using var services = builder.Services.BuildServiceProvider();
        var capabilities = services.GetRequiredService<IOptions<WorkerOptions>>().Value.Capabilities;
        Assert.AreEqual(inAspire, capabilities.TryGetValue("WorkerOpenTelemetryEnabled", out var enabled));
        if (inAspire)
            Assert.AreEqual(bool.TrueString, enabled);
    }

    [TestMethod]
    public void HttpClientHeaderValuesAreAlwaysRedacted()
    {
        var builder = CreateBuilder();
        builder.AddServiceDefaults();
        using var services = builder.Services.BuildServiceProvider();
        var options = services.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>().Get("management");

        foreach (var header in new[] { "Authorization", "Cookie", "X-Admin-Api-Key", "api-key", "X-Custom-Secret" })
            Assert.IsTrue(options.ShouldRedactHeaderValue(header), header);
    }
}
