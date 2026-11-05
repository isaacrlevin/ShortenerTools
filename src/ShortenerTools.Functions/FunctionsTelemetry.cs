using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ShortenerTools.Functions;

internal static class FunctionsTelemetry
{
    internal static TBuilder ConfigureFunctionsTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        if (builder.Configuration.GetValue<bool>("IN_ASPIRE"))
        {
            builder.AddServiceDefaults();
            // Correlate worker logs/spans with the Functions host and prevent
            // worker logs being forwarded through a second host telemetry path.
            builder.Services.AddOpenTelemetry().UseFunctionsWorkerDefaults();
        }
        else
        {
            // Preserve the standalone/legacy Functions deployment path. Never
            // register this SDK alongside the Aspire OpenTelemetry pipeline.
            builder.Services.AddApplicationInsightsTelemetryWorkerService()
                .ConfigureFunctionsApplicationInsights();
        }

        return builder;
    }
}
