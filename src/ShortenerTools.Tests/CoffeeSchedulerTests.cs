using System.Net;
using System.Reflection;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Newtonsoft.Json;
using ShortenerTools.Core.Domain;
using ShortenerTools.Core.Domain.Coffee;
using ShortenerTools.Core.Domain.Socials;
using ShortenerTools.Functions.Functions;
using ShortenerTools.Functions.Socials;

namespace ShortenerTools.Tests;

[TestClass]
public sealed class CoffeeSchedulerTests
{
    private static readonly DateTime Now = new(2026, 10, 12, 17, 0, 0, DateTimeKind.Utc);
    private readonly Mock<ICoffeeGuestFeed> _feed = new(MockBehavior.Strict);
    private readonly Mock<ISocialMediaPublisher> _publisher = new(MockBehavior.Strict);

    private CoffeeOpenSource CreateService(string environment = "Production", bool postSocials = true, bool disabled = false) =>
        new(NullLogger<CoffeeOpenSource>.Instance,
            new ShortenerSettings { EnvironmentName = environment, PostSocials = postSocials, DisableExternalPosting = disabled },
            _feed.Object, _publisher.Object, new FixedClock());

    private void SetGuests(params Guest[] guests) =>
        _feed.Setup(feed => feed.GetGuestsAsync()).ReturnsAsync(guests);

    private static Guest CreateGuest(string key = "jane-doe", DateTime? date = null, bool published = false) =>
        new() { PartitionKey = key, GuestName = "Jane Doe", DateTimeUTC = date ?? Now.AddHours(1), IsPublished = published };

    [TestMethod]
    [DataRow("Staging", true, false)]
    [DataRow("staging", true, false)]
    [DataRow("Production", false, false)]
    [DataRow("Production", true, true)]
    public async Task AllTriggers_DisallowedPosting_DoNotAccessFeedOrPublisher(
        string environment, bool postSocials, bool disabled)
    {
        var service = CreateService(environment, postSocials, disabled);
        await service.COSSTeaserTimer(null!);
        await service.COSSAnnouncementTimer(null!);
        await service.COSSArchiveTimer(null!);
        await service.COSSTeaserHttp(null!);
        await service.COSSAnnouncementHttp(null!);
        await service.COSSArchiveHttp(null!);
        using var request = new FunctionRequest("\"jane-doe\"");
        var response = await service.COSSPublishHttp(request.Request);
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        _feed.VerifyNoOtherCalls();
        _publisher.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task Teaser_UnsortedUpcomingGuests_SelectsNearestWithinFiveDays()
    {
        SetGuests(CreateGuest("later", Now.AddDays(4)), CreateGuest("past", Now.AddMinutes(-1)),
            CreateGuest("nearest"), CreateGuest("too-late", Now.AddDays(5)));
        SocialPost? posted = null;
        _publisher.Setup(p => p.PublishAsync(It.IsAny<SocialPost>(), true))
            .Callback<SocialPost, bool>((post, _) => posted = post).Returns(Task.CompletedTask);

        await CreateService().COSSTeaserTimer(null!);

        Assert.IsNotNull(posted);
        Assert.AreEqual("nearest", posted.Key);
        Assert.AreEqual("https://www.coffeeandopensource.com/schedule.html", posted.Url);
        StringAssert.Contains(posted.Text, "Coming up this week");
        _publisher.Verify(p => p.PublishAsync(It.IsAny<SocialPost>(), true), Times.Once);
    }

    [TestMethod]
    public async Task Announcement_OutsideThreeHourWindow_DoesNotPublish()
    {
        SetGuests(CreateGuest("boundary", Now.AddHours(3)), CreateGuest("now", Now));
        await CreateService().COSSAnnouncementTimer(null!);
        _publisher.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task Announcement_UpcomingGuest_PublishesTwitchLink()
    {
        SetGuests(CreateGuest());
        _publisher.Setup(p => p.PublishAsync(It.Is<SocialPost>(post =>
            post.Url == "https://www.twitch.tv/isaacrlevin" && post.Text.Contains("Streaming live today")), true))
            .Returns(Task.CompletedTask);
        await CreateService().COSSAnnouncementTimer(null!);
        _publisher.VerifyAll();
    }

    [TestMethod]
    public async Task Archive_EmptyPublishedList_DoesNotPublish()
    {
        SetGuests(CreateGuest());
        await CreateService().COSSArchiveTimer(null!);
        _publisher.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task Archive_OnePublishedGuest_PublishesOnlyPublishedGuest()
    {
        SetGuests(CreateGuest("unpublished"), CreateGuest(published: true));
        _publisher.Setup(p => p.PublishAsync(It.Is<SocialPost>(post =>
            post.Key == "jane-doe" && post.Url.EndsWith("/guest/jane-doe.html")), true)).Returns(Task.CompletedTask);
        await CreateService().COSSArchiveTimer(null!);
        _publisher.VerifyAll();
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("not-json")]
    [DataRow("\"\"")]
    [DataRow("null")]
    public async Task Publish_InvalidGuestKey_ReturnsBadRequestWithoutFeedAccess(string body)
    {
        using var request = new FunctionRequest(body);
        var response = await CreateService().COSSPublishHttp(request.Request);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        _feed.VerifyNoOtherCalls();
        _publisher.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task Publish_UnpublishedGuest_ReturnsNotFound()
    {
        SetGuests(CreateGuest());
        using var request = new FunctionRequest("\"jane-doe\"");
        var response = await CreateService().COSSPublishHttp(request.Request);
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        _publisher.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task Publish_PublishedGuest_UsesSharedPublisherAndReturnsComplete()
    {
        SetGuests(CreateGuest(published: true));
        _publisher.Setup(p => p.PublishAsync(It.Is<SocialPost>(post =>
            post.Key == "jane-doe" && post.Text.Contains("Video is live on YouTube")), true)).Returns(Task.CompletedTask);
        using var request = new FunctionRequest("\"jane-doe\"");
        var response = await CreateService().COSSPublishHttp(request.Request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("\"Complete\"", await request.ReadBody());
        _publisher.VerifyAll();
    }

    [TestMethod]
    public void Feed_LegacyGuestDocument_PreservesSocialHandles()
    {
        var guests = CoffeeGuestFeed.Parse("""
            {"jane-doe":{"PartitionKey":"jane-doe","GuestName":"Jane Doe","DateTimeUTC":"2026-10-12T18:00:00Z",
            "IsPublished":true,"Socials":[{"X":"https://x.com/jane"},{"Bluesky":"https://bsky.app/profile/jane.example"}]}}
            """);
        Assert.AreEqual(1, guests.Count);
        Assert.AreEqual("https://x.com/jane", guests[0].Socials["X"]);
        Assert.AreEqual(DateTimeKind.Utc, guests[0].DateTimeUTC.Kind);
        Assert.IsTrue(guests[0].IsPublished);
    }

    [TestMethod]
    public void Feed_MissingGuestKey_ThrowsInsteadOfReturningInvalidGuest() =>
        Assert.ThrowsExactly<JsonSerializationException>(() => CoffeeGuestFeed.Parse("""{"jane":{}}"""));

    [TestMethod]
    public void Post_PlatformHandles_UsesGuestHandlesWithoutTreatingThemAsCredentials()
    {
        var guest = CreateGuest();
        guest.Socials = new()
        {
            ["X"] = "https://x.com/jane/",
            ["Bluesky"] = "https://bsky.app/profile/jane.example",
            ["Mastodon"] = "@jane@mastodon.example",
            ["Threads"] = "https://www.threads.net/@jane/"
        };
        var post = CoffeePostFactory.Teaser(guest);
        StringAssert.Contains(post.TwitterText, "with @jane ");
        StringAssert.Contains(post.BlueskyText, "with @jane.example ");
        StringAssert.Contains(post.MastodonText, "with @jane@mastodon.example ");
        StringAssert.Contains(post.ThreadsText, "with @jane ");
        StringAssert.Contains(post.LinkedInText, "with Jane Doe ");
        Assert.IsFalse(post.Text.Contains("[HANDLE]"));
    }

    [TestMethod]
    [DataRow("2026-07-01T18:00:00Z", "11:00 AM PDT")]
    [DataRow("2026-12-01T18:00:00Z", "10:00 AM PST")]
    public void Post_EventDate_UsesPacificTimeAndEventDaylightSaving(string date, string expected)
    {
        var post = CoffeePostFactory.Announcement(CreateGuest(date: DateTime.Parse(date).ToUniversalTime()));
        StringAssert.Contains(post.Text, expected);
    }

    [TestMethod]
    [DataRow(1, "st")]
    [DataRow(2, "nd")]
    [DataRow(3, "rd")]
    [DataRow(11, "th")]
    [DataRow(12, "th")]
    [DataRow(13, "th")]
    [DataRow(21, "st")]
    [DataRow(31, "st")]
    public void Teaser_DaySuffix_PreservesOrdinals(int day, string expected) =>
        Assert.AreEqual(expected, CoffeePostFactory.DaySuffix(day));

    [TestMethod]
    [DataRow(nameof(CoffeeOpenSource.COSSTeaserTimer), "0 0 17 * * MON")]
    [DataRow(nameof(CoffeeOpenSource.COSSAnnouncementTimer), "0 0 17 * * *")]
    [DataRow(nameof(CoffeeOpenSource.COSSArchiveTimer), "0 0 16 * * MON")]
    public void Timers_MigratedFunctions_PreserveNamesAndUtcSchedules(string methodName, string schedule)
    {
        var method = typeof(CoffeeOpenSource).GetMethod(methodName)!;
        Assert.AreEqual(methodName, method.GetCustomAttribute<FunctionAttribute>()!.Name);
        Assert.AreEqual(schedule, method.GetParameters()[0].GetCustomAttribute<TimerTriggerAttribute>()!.Schedule);
    }

    [TestMethod]
    public void ShortenerTimer_UsesFunctionNameExpectedByDeployment()
    {
        var method = typeof(SchedulePost).GetMethod(nameof(SchedulePost.ShortPostTimer))!;
        var functionName = method.GetCustomAttribute<FunctionAttribute>()!.Name;

        Assert.AreEqual("ShortPostTimer", functionName);
    }

    [TestMethod]
    public void DeploymentTimerNames_MatchDeclaredFunctions()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "shortenerTools.sln")))
        {
            root = root.Parent;
        }
        Assert.IsNotNull(root, "Repository root must be available for deployment configuration checks.");

        var appHost = File.ReadAllText(Path.Combine(root.FullName, "src", "ShortenerTools.AppHost", "Program.cs"));
        var coffeeTimerList = System.Text.RegularExpressions.Regex.Match(appHost,
            @"string\[\] coffeeTimers = \[(.*?)\];").Groups[1].Value;
        var configuredNames = System.Text.RegularExpressions.Regex.Matches(coffeeTimerList, "\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value).ToArray();
        var declaredNames = typeof(CoffeeOpenSource).GetMethods()
            .Where(method => method.GetParameters().Any(parameter =>
                parameter.GetCustomAttribute<TimerTriggerAttribute>() is not null))
            .Select(method => method.GetCustomAttribute<FunctionAttribute>()!.Name).ToArray();
        CollectionAssert.AreEquivalent(declaredNames, configuredNames);

        var workflow = File.ReadAllText(Path.Combine(root.FullName, ".github", "workflows", "azure-container-apps.yml"));
        var verificationTimers = System.Text.RegularExpressions.Regex.Match(workflow,
            @"\$timers = @\((.*?)\)", System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value;
        var verifiedNames = System.Text.RegularExpressions.Regex.Matches(verificationTimers, "'([^']+)'")
            .Select(match => match.Groups[1].Value).ToArray();
        CollectionAssert.AreEquivalent(declaredNames.Append("ShortPostTimer").ToArray(), verifiedNames);
    }

    [TestMethod]
    public void ShortenerPost_SharedEnvelope_PreservesTextAndShortUrl()
    {
        var scheduler = new SchedulePost(new ShortenerSettings { CustomDomain = "https://short.example/" }, _publisher.Object);
        var item = new ShortUrlEntity("https://destination.example", "link", "Title", "Message", true);
        var post = scheduler.CreatePost(item);
        Assert.AreEqual("Title\nMessage\n\nhttps://short.example/link", post.Text);
        Assert.AreEqual(post.Text, post.TwitterText);
        Assert.AreEqual("https://short.example/link", post.Url);
    }

    [TestMethod]
    public async Task SharedPublisher_Staging_DoesNotInitializeClientsOrCallProviders()
    {
        var settings = new ShortenerSettings { EnvironmentName = "Staging" };
        var publisher = new SocialPosterPublisher(NullLogger<SocialPosterPublisher>.Instance, settings, null!,
            new EmailService(NullLoggerFactory.Instance, settings));
        await publisher.PublishAsync(new SocialPost("test", "Test", "text", "https://example.com"));
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(Now);
    }
}
