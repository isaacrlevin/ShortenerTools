using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using ShortenerTools.Core.Domain;
using ShortenerTools.Core.Domain.Socials;
using System.Diagnostics;

namespace ShortenerTools.Functions.Functions;

public class SchedulePost(ShortenerSettings settings, ISocialMediaPublisher publisher, StorageTableHelper? tables = null)
{
    public string ShortenerBase => settings.CustomDomain.TrimEnd('/') + "/";

    [Function("TestShortUrl")]
    public async Task Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "test/{shortUrl}")]
        HttpRequestData req, string shortUrl, ExecutionContext context)
    {
        if (settings.ExternalPostingAllowed && Debugger.IsAttached && !string.IsNullOrWhiteSpace(shortUrl))
        {
            var storage = new StorageTableHelper(settings.DataStorage);
            var item = await storage.GetShortUrlEntity(new ShortUrlEntity(string.Empty, shortUrl));
            if (item is not null)
            {
                await publisher.PublishAsync(CreatePost(item), includeThreads: false);
            }
        }
    }

    [Function("ShortPostTimer")]
    public async Task ShortPostTimer([TimerTrigger("0 0 13,16,19,23 * * 1-5")] TimerInfo myTimer)
    {
        if (settings.ExternalPostingAllowed && !Debugger.IsAttached)
        {
            await PublishToSocial();
        }
    }

    [Function("ShortPostHttp")]
    public async Task ShortPostHttp(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "api/SchedulePost")]
        HttpRequestData req, ExecutionContext context)
    {
        // Under Aspire the host is not the debugged process, so a local run also qualifies.
        // The publisher itself skips sending when external posting is disabled (dry run).
        if (Debugger.IsAttached || IsLocalAspire)
        {
            await PublishToSocial(force: true);
        }
    }

    private bool IsLocalAspire =>
        Environment.GetEnvironmentVariable("IN_ASPIRE") == "true"
        && !string.Equals(settings.EnvironmentName, "Production", StringComparison.OrdinalIgnoreCase);

    internal async Task PublishToSocial(bool force = false)
    {
        if (!force && !settings.ExternalPostingAllowed)
        {
            return;
        }
        var storage = tables ?? new StorageTableHelper(settings.DataStorage);
        var items = await storage.GetAllShortUrlEntities();
        var item = items.Where(p => !p.Posted).OrderBy(p => p.Timestamp).FirstOrDefault();
        if (item is not null)
        {
            // Links with per-platform versions post exactly those; older links keep the single shared message.
            var versions = LatestVersionPerPlatform(await storage.GetPostVersions(item.RowKey));
            await publisher.PublishAsync(CreatePost(item, versions.Values.ToList()));
            item.Posted = true;
            await storage.UpdateShortUrlEntity(item);
            foreach (var version in versions.Values)
            {
                await storage.MarkPostVersion(version, PostVersionStatus.Published);
            }
        }
    }

    /// <summary>The newest non-legacy version for each platform, keyed by lower-case platform name.</summary>
    internal static Dictionary<string, PostVersionEntity> LatestVersionPerPlatform(IEnumerable<PostVersionEntity> versions) =>
        versions.Where(v => !v.IsLegacy && !string.IsNullOrWhiteSpace(v.Text))
            .GroupBy(v => PlatformOf(v.Account))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.Timestamp ?? DateTimeOffset.MinValue).First());

    // Accounts look like "bluesky" or "bluesky:main"; the part before ':' names the platform.
    private static string PlatformOf(string account)
    {
        var platform = account.Split(':')[0].Trim().ToLowerInvariant();
        return platform == "twitter" ? "x" : platform;
    }

    internal SocialPost CreatePost(ShortUrlEntity item, IReadOnlyCollection<PostVersionEntity> versions)
    {
        var post = CreatePost(item);
        if (versions.Count == 0) return post;

        var url = $"{ShortenerBase}{item.RowKey}";
        string? TextFor(string platform) => versions.FirstOrDefault(v => PlatformOf(v.Account) == platform)
            ?.Text.Replace(PostVersionEntity.ShortUrlToken, url);
        return post with
        {
            TwitterText = TextFor("x") ?? post.TwitterText,
            BlueskyText = TextFor("bluesky"),
            LinkedInText = TextFor("linkedin"),
            MastodonText = TextFor("mastodon"),
            ThreadsText = TextFor("threads"),
            Platforms = versions.Select(v => PlatformOf(v.Account)).ToHashSet()
        };
    }

    internal SocialPost CreatePost(ShortUrlEntity item)
    {
        var url = $"{ShortenerBase}{item.RowKey}";
        var text = $"{item.Message}\n\n{url}";
        if (!string.IsNullOrEmpty(item.Title))
        {
            text = $"{item.Title}\n{text}";
        }
        // Preserve the existing shortener's X fallback for long posts.
        var twitterText = text;
        if (twitterText.Length > 280)
        {
            twitterText = $"{item.Title}\n\n{url}";
            if (!string.IsNullOrEmpty(item.Title))
            {
                twitterText = $"{item.Title}\n{twitterText}";
            }
        }
        return new SocialPost(item.RowKey, item.Title, text, url)
        {
            TwitterText = twitterText
        };
    }

}
