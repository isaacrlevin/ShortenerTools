using ShortenerTools.Core.Domain;
using ShortenerTools.Functions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System.Net;

namespace ShortenerTools.Tests;

[TestClass]
public class UrlRedirectTests
{
    [TestMethod]
    [DataRow(true, false, "https://fallback.example")]
    [DataRow(false, true, "https://fallback.example")]
    [DataRow(true, false, null)]
    public async Task ArchivedAndMissingLinksUseFallbackWithoutTracking(bool archived, bool missing, string? fallback)
    {
        var storage = new Mock<StorageTableHelper>();
        var entity = new ShortUrlEntity("https://destination.example", "link") { IsArchived = archived };
        storage.Setup(x => x.GetShortUrlEntity(It.IsAny<ShortUrlEntity>()))
            .ReturnsAsync(missing ? null! : entity);
        using var request = new FunctionRequest();
        var function = new UrlRedirect(NullLoggerFactory.Instance,
            new ShortenerSettings { DefaultRedirectUrl = fallback! }, storage.Object);

        var response = await function.Run(request.Request, "link", null!);

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.AreEqual(new Uri(fallback ?? "https://isaaclevin.com"),
            new Uri(response.Headers.GetValues("Location").Single()));
        storage.Verify(x => x.SaveClickStatsEntity(It.IsAny<ClickStatsEntity>()), Times.Never);
    }

    [TestMethod]
    public async Task ActiveLinkStillTracksRichAnalyticsAndPreservesSocialFields()
    {
        var storage = new Mock<StorageTableHelper>();
        var entity = new ShortUrlEntity("https://destination.example/path?campaign=test", "link",
            "Title", "Social message", true);
        storage.Setup(x => x.GetShortUrlEntity(It.IsAny<ShortUrlEntity>())).ReturnsAsync(entity);
        using var request = new FunctionRequest(userAgent:
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36",
            referrer: "https://referrer.example/article");
        var function = new UrlRedirect(NullLoggerFactory.Instance,
            new ShortenerSettings { DefaultRedirectUrl = "https://fallback.example" }, storage.Object);

        var response = await function.Run(request.Request, "link", null!);

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.AreEqual(entity.Url, response.Headers.GetValues("Location").Single());
        storage.Verify(x => x.SaveClickStatsEntity(It.Is<ClickStatsEntity>(click =>
            click.PartitionKey == "link" && click.ShortUrl == "link" &&
            click.ReferrerHost == "referrer.example" && click.ReferrerUrl == "https://referrer.example/article" &&
            click.Browser == "Chrome" && click.Platform == "Windows" && click.IsDesktop &&
            click.Page.Contains("destination.example/path") && click.Host == "destination.example")), Times.Once);
        Assert.AreEqual("Social message", entity.Message);
        Assert.IsFalse(entity.Posted);
        Assert.IsFalse(entity.IsArchived);
    }

    [TestMethod]
    public async Task ActiveLinkRecordsCloudflareVisitorLocation()
    {
        var storage = new Mock<StorageTableHelper>();
        storage.Setup(x => x.GetShortUrlEntity(It.IsAny<ShortUrlEntity>()))
            .ReturnsAsync(new ShortUrlEntity("https://destination.example", "link"));
        using var request = new FunctionRequest(userAgent: "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120.0",
            headers: new Dictionary<string, string>
            {
                ["CF-IPCountry"] = "us", ["cf-region"] = "Washington", ["cf-ipcity"] = " Seattle ",
                ["cf-iplatitude"] = "47.60621", ["cf-iplongitude"] = "-122.33207"
            });
        var function = new UrlRedirect(NullLoggerFactory.Instance, new ShortenerSettings(), storage.Object);

        await function.Run(request.Request, "link", null!);

        storage.Verify(x => x.SaveClickStatsEntity(It.Is<ClickStatsEntity>(click =>
            click.Country == "US" && click.Region == "Washington" && click.City == "Seattle" &&
            click.Latitude == 47.6 && click.Longitude == -122.3)), Times.Once);
    }

    [TestMethod]
    [DataRow("47.6", null)]
    [DataRow("91", "10")]
    [DataRow("10", "-180.5")]
    [DataRow("NaN", "10")]
    [DataRow("not-a-number", "10")]
    public void CoordinatesAreStoredOnlyAsAValidPair(string? latitude, string? longitude)
    {
        var headers = new Microsoft.Azure.Functions.Worker.Http.HttpHeadersCollection();
        if (latitude is not null)
            headers.Add("cf-iplatitude", latitude);
        if (longitude is not null)
            headers.Add("cf-iplongitude", longitude);

        var location = UrlRedirect.ReadVisitorLocation(headers);

        Assert.IsNull(location.Latitude);
        Assert.IsNull(location.Longitude);
    }

    [TestMethod]
    public void CoordinatesAreRoundedToOneDecimalPlace()
    {
        var headers = new Microsoft.Azure.Functions.Worker.Http.HttpHeadersCollection();
        headers.Add("cf-iplatitude", "-33.86785");
        headers.Add("cf-iplongitude", "151.20732");

        var location = UrlRedirect.ReadVisitorLocation(headers);

        Assert.AreEqual(-33.9, location.Latitude);
        Assert.AreEqual(151.2, location.Longitude);
    }

    [TestMethod]
    public async Task ActiveLinkWithoutCloudflareHeadersHasNoLocation()
    {
        var storage = new Mock<StorageTableHelper>();
        storage.Setup(x => x.GetShortUrlEntity(It.IsAny<ShortUrlEntity>()))
            .ReturnsAsync(new ShortUrlEntity("https://destination.example", "link"));
        using var request = new FunctionRequest(userAgent: "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120.0");
        var function = new UrlRedirect(NullLoggerFactory.Instance, new ShortenerSettings(), storage.Object);

        await function.Run(request.Request, "link", null!);

        storage.Verify(x => x.SaveClickStatsEntity(It.Is<ClickStatsEntity>(click =>
            click.Country == null && click.Region == null && click.City == null &&
            click.Latitude == null && click.Longitude == null)), Times.Once);
    }

    [TestMethod]
    [DataRow("XX")]
    [DataRow("T1")]
    [DataRow("  ")]
    public void UnknownTorAndBlankCountriesAreNotRecorded(string country)
    {
        var headers = new Microsoft.Azure.Functions.Worker.Http.HttpHeadersCollection();
        headers.Add("CF-IPCountry", country);
        headers.Add("cf-ipcity", new string('a', 500));

        var location = UrlRedirect.ReadVisitorLocation(headers);

        Assert.IsNull(location.Country);
        Assert.IsNull(location.Region);
        Assert.AreEqual(100, location.City!.Length);
    }

    [TestMethod]
    public async Task ActiveLinkStillSuppressesBotsWithoutTracking()
    {
        var storage = new Mock<StorageTableHelper>();
        storage.Setup(x => x.GetShortUrlEntity(It.IsAny<ShortUrlEntity>()))
            .ReturnsAsync(new ShortUrlEntity("https://destination.example", "link"));
        using var request = new FunctionRequest(userAgent: "ExampleBot");
        var function = new UrlRedirect(NullLoggerFactory.Instance, new ShortenerSettings(), storage.Object);
        var response = await function.Run(request.Request, "link", null!);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(await request.ReadBody(), "Disallow: /");
        storage.Verify(x => x.SaveClickStatsEntity(It.IsAny<ClickStatsEntity>()), Times.Never);
    }

    [TestMethod]
    [DataRow("Twitterbot/1.0")]
    [DataRow("LinkedInBot/1.0 (compatible; Mozilla/5.0; Apache-HttpClient +http://www.linkedin.com)")]
    [DataRow("facebookexternalhit/1.1 (+http://www.facebook.com/externalhit_uatext.php)")]
    [DataRow("Bluesky Cardyb/1.0")]
    [DataRow("Mozilla/5.0 (compatible; SocialPoster/1.0; +https://github.com)")]
    public async Task LinkPreviewFetchersFollowTheRedirectAndAreFlaggedAsCrawlers(string userAgent)
    {
        var storage = new Mock<StorageTableHelper>();
        storage.Setup(x => x.GetShortUrlEntity(It.IsAny<ShortUrlEntity>()))
            .ReturnsAsync(new ShortUrlEntity("https://destination.example", "link"));
        using var request = new FunctionRequest(userAgent: userAgent);
        var function = new UrlRedirect(NullLoggerFactory.Instance, new ShortenerSettings(), storage.Object);
        var response = await function.Run(request.Request, "link", null!);
        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        // HttpHeaders parses Location as a URI, so a bare host may be returned with a root "/".
        Assert.AreEqual(new Uri("https://destination.example"), new Uri(response.Headers.GetValues("Location").Single()));
        storage.Verify(x => x.SaveClickStatsEntity(It.Is<ClickStatsEntity>(click => click.IsCrawler)), Times.Once);
    }

    [TestMethod]
    [DataRow("meta-externalagent/1.1 (+https://developers.facebook.com/docs/sharing/webmasters/crawler)")]
    [DataRow("Googlebot/2.1")]
    public void OnlyLinkPreviewFetchersBypassTheBotFilter(string userAgent)
    {
        Assert.IsFalse(UrlRedirect.IsLinkPreviewAgent(userAgent));
        Assert.IsFalse(UrlRedirect.IsLinkPreviewAgent(null));
        Assert.IsTrue(UrlRedirect.IsLinkPreviewAgent("TWITTERBOT/1.0"));
    }

    [TestMethod]
    public void RobotsAllowsEachLinkPreviewFetcherAndDisallowsEveryoneElse()
    {
        foreach (var token in new[] { "Twitterbot", "LinkedInBot", "facebookexternalhit", "Cardyb" })
        {
            StringAssert.Contains(UrlRedirect.RobotsBody, $"User-agent: {token}\n");
        }
        StringAssert.EndsWith(UrlRedirect.RobotsBody, "User-agent: *\nDisallow: /");
    }

    [TestMethod]
    [DataRow("robots.txt")]
    [DataRow("favicon.ico")]
    public async Task RobotsAndDottedPathsStillBypassStorage(string path)
    {
        var storage = new Mock<StorageTableHelper>(MockBehavior.Strict);
        using var request = new FunctionRequest();
        var function = new UrlRedirect(NullLoggerFactory.Instance, new ShortenerSettings(), storage.Object);
        var response = await function.Run(request.Request, path, null!);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(await request.ReadBody(), "User-agent: Twitterbot");
        storage.VerifyNoOtherCalls();
    }

    [TestMethod]
    public void ConstructorRejectsNullDependencies()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new UrlRedirect(null!, new ShortenerSettings(), new StorageTableHelper()));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new UrlRedirect(NullLoggerFactory.Instance, null!, new StorageTableHelper()));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new UrlRedirect(NullLoggerFactory.Instance, new ShortenerSettings(), null!));
    }
}
