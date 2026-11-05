using Moq;
using ShortenerTools.Core.Domain;
using ShortenerTools.Core.Domain.Socials;
using ShortenerTools.Functions.Functions;

namespace ShortenerTools.Tests;

[TestClass]
public class SchedulePostVersionTests
{
    private static readonly ShortenerSettings Settings = new() { CustomDomain = "https://short.example/" };

    private static (SchedulePost Scheduler, Mock<ISocialMediaPublisher> Publisher, Mock<StorageTableHelper> Storage, ShortUrlEntity Item)
        Arrange(params PostVersionEntity[] versions)
    {
        var item = new ShortUrlEntity("https://destination.example", "link", "Title", "Message", true);
        var storage = new Mock<StorageTableHelper>();
        storage.Setup(x => x.GetAllShortUrlEntities()).ReturnsAsync([item]);
        storage.Setup(x => x.GetPostVersions("link", null)).ReturnsAsync(versions.ToList());
        var publisher = new Mock<ISocialMediaPublisher>();
        return (new SchedulePost(Settings, publisher.Object, storage.Object), publisher, storage, item);
    }

    [TestMethod]
    public async Task LinkWithoutVersionsPostsSharedMessageToEveryPlatform()
    {
        var (scheduler, publisher, _, item) = Arrange(PostVersionEntity.FromLegacy(
            new ShortUrlEntity("https://destination.example", "link", "Title", "Message", true)));
        SocialPost? posted = null;
        publisher.Setup(x => x.PublishAsync(It.IsAny<SocialPost>(), true)).Callback<SocialPost, bool>((p, _) => posted = p)
            .Returns(Task.CompletedTask);

        await scheduler.PublishToSocial();

        Assert.AreEqual("Title\nMessage\n\nhttps://short.example/link", posted!.Text);
        Assert.IsNull(posted.Platforms, "Legacy links post to all platforms.");
        Assert.IsTrue(item.Posted);
    }

    [TestMethod]
    public async Task VersionedLinkPostsEachPlatformsTextToOnlyThosePlatforms()
    {
        var older = new PostVersionEntity("link", "bluesky", "old", createdAt: DateTimeOffset.UtcNow.AddHours(-1))
            { Timestamp = DateTimeOffset.UtcNow.AddHours(-1) };
        var bluesky = new PostVersionEntity("link", "bluesky", "Bsky {shorturl}") { Timestamp = DateTimeOffset.UtcNow };
        var mastodon = new PostVersionEntity("link", "mastodon:main", "Masto\n{shorturl}") { Timestamp = DateTimeOffset.UtcNow };
        var (scheduler, publisher, storage, item) = Arrange(bluesky, older, mastodon);
        SocialPost? posted = null;
        publisher.Setup(x => x.PublishAsync(It.IsAny<SocialPost>(), true)).Callback<SocialPost, bool>((p, _) => posted = p)
            .Returns(Task.CompletedTask);

        await scheduler.PublishToSocial();

        Assert.AreEqual("Bsky https://short.example/link", posted!.BlueskyText);
        Assert.AreEqual("Masto\nhttps://short.example/link", posted.MastodonText);
        Assert.IsTrue(posted.Targets("bluesky") && posted.Targets("Mastodon"));
        Assert.IsFalse(posted.Targets("linkedin") || posted.Targets("x") || posted.Targets("threads"));
        Assert.IsTrue(item.Posted);
        storage.Verify(x => x.MarkPostVersion(bluesky, PostVersionStatus.Published, null), Times.Once);
        storage.Verify(x => x.MarkPostVersion(older, It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }
}
