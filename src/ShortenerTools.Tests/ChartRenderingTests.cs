using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using ShortenerTools.Admin.Shared;
using ShortenerTools.Core.Domain;

namespace ShortenerTools.Tests;

[TestClass]
public class ChartRenderingTests
{
    [TestMethod]
    public async Task PieChartRendersCategoriesAndOtherGroup()
    {
        await using var services = CreateServices();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var clicks = Enumerable.Range(1, 12)
            .Select(i => new ClickStatsEntity { ShortUrl = $"link-{i:00}" }).ToList();

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = await renderer.RenderComponentAsync<StatisticsPie>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    [nameof(StatisticsPie.Title)] = "ShortUrl Statistics",
                    [nameof(StatisticsPie.Clicks)] = clicks,
                    [nameof(StatisticsPie.Category)] = (Func<ClickStatsEntity, string>)(click => click.ShortUrl)
                }));
            return result.ToHtmlString();
        });

        StringAssert.Contains(html, "<path");
        StringAssert.Contains(html, "link-01");
        StringAssert.Contains(html, "link-10");
        StringAssert.Contains(html, "Other");
        Assert.IsFalse(html.Contains("link-11", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task TimeSeriesChartRendersTimestampedData()
    {
        await using var services = CreateServices();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var series = new List<ChartSeries<double>>
        {
            new()
            {
                Name = "Click(s) by Day",
                Data = new[] { (new DateTime(2026, 10, 1), 2d), (new DateTime(2026, 10, 2), 3d) }
            }
        };

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = await renderer.RenderComponentAsync<MudChart<double>>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    ["ChartType"] = ChartType.Timeseries,
                    ["ChartSeries"] = series,
                    ["ChartOptions"] = new TimeSeriesChartOptions
                    {
                        TimeLabelFormat = "MM/dd/yyyy",
                        TimeLabelSpacing = TimeSpan.FromDays(1),
                        TooltipTimeLabelFormat = "MM/dd/yyyy",
                        ShowDataMarkers = true
                    }
                }));
            return result.ToHtmlString();
        });

        StringAssert.Contains(html, "<path");
        StringAssert.Contains(html, "10/01/2026");
        StringAssert.Contains(html, "10/02/2026");
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<IJSRuntime>());
        services.AddMudServices();
        return services.BuildServiceProvider();
    }
}
