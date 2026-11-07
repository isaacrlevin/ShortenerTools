using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using MudBlazor.Services;
using ShortenerTools.Admin.Shared;
using ShortenerTools.Core.Domain;

namespace ShortenerTools.Tests;

[TestClass]
public class ClickMapTests
{
    private static ClickStatsEntity Click(string? country = null, string? city = null, string? region = null,
        double? latitude = null, double? longitude = null) =>
        new() { Country = country, City = city, Region = region, Latitude = latitude, Longitude = longitude };

    [TestMethod]
    public void ClicksAtTheSamePositionBecomeOneCircleLabelledWithTheMostCommonPlace()
    {
        var points = ClickMapPoints.Build(new[]
        {
            Click("US", "Seattle", "Washington", 47.6, -122.3),
            Click("US", "Seattle", "Washington", 47.6, -122.3),
            Click("US", null, "Washington", 47.6, -122.3),
            Click("GB", "London", "England", 51.5, -0.1)
        });

        Assert.HasCount(2, points);
        Assert.AreEqual(new ClickMapPoint(47.6, -122.3, "Seattle, Washington, US", 3, false), points[0]);
        Assert.AreEqual(new ClickMapPoint(51.5, -0.1, "London, England, GB", 1, false), points[1]);
    }

    [TestMethod]
    public void CountryOnlyClicksAreDrawnAtTheCountryCentre()
    {
        var points = ClickMapPoints.Build(new[] { Click("DE"), Click("de"), Click("ZZ"), Click() });

        var point = points.Single();
        Assert.IsTrue(point.CountryOnly);
        Assert.AreEqual(2, point.Clicks);
        Assert.AreEqual("Germany (country only)", point.Label);
        Assert.AreEqual(51.17, point.Latitude);
        Assert.AreEqual(10.45, point.Longitude);
    }

    [TestMethod]
    public void PointsAreOrderedLargestFirstSoSmallCirclesDrawOnTop()
    {
        var points = ClickMapPoints.Build(new[]
        {
            Click("FR", "Paris", latitude: 48.9, longitude: 2.4),
            Click("US"), Click("US"), Click("US")
        });

        CollectionAssert.AreEqual(new[] { 3, 1 }, points.Select(p => p.Clicks).ToArray());
    }

    [TestMethod]
    public void MappableRequiresCoordinatesOrAKnownCountry()
    {
        Assert.IsTrue(ClickMapPoints.IsMappable(Click(latitude: 1, longitude: 2)));
        Assert.IsTrue(ClickMapPoints.IsMappable(Click("JP")));
        Assert.IsFalse(ClickMapPoints.IsMappable(Click("XX")));
        Assert.IsFalse(ClickMapPoints.IsMappable(Click(latitude: 1)));
    }

    [TestMethod]
    public async Task MapRendersTitleAndAccessibleSummary()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<IJSRuntime>());
        services.AddMudServices();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        IReadOnlyList<ClickStatsEntity> clicks = new[] { Click("US", "Seattle", "Washington", 47.6, -122.3), Click("DE") };

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<ClickMap>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(ClickMap.Clicks)] = clicks }))).ToHtmlString());

        StringAssert.Contains(html, "Click Locations");
        StringAssert.Contains(html, "class=\"click-map");
        StringAssert.Contains(html, "2 clicks across 2 places");
    }
}
