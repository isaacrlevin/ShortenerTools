using Microsoft.Extensions.Logging;
using ShortenerTools.Core.Domain;
using ShortenerTools.Core.Domain.Socials;
using ShortenerTools.Core.Domain.Socials.Twitter;
using SocialPoster.Posting;
using SocialPoster.Posting.Services;

namespace ShortenerTools.Functions.Socials;

/// <summary>
/// Publishes through the SocialPoster platform clients (SocialPoster.Posting), using the account credentials in
/// <see cref="ShortenerSettings"/> (mirrored from SocialPoster via Azure App Configuration). SocialPoster is the only
/// posting implementation; without X API credentials, X falls back to an emailed intent link.
/// </summary>
public sealed class SocialPosterPublisher(
    ILogger<SocialPosterPublisher> logger,
    ShortenerSettings settings,
    IHttpClientFactory httpClientFactory,
    EmailService emailService) : ISocialMediaPublisher
{
    private const string DefaultMastodonInstance = "fosstodon.org";

    public async Task PublishAsync(SocialPost post, bool includeThreads = true)
    {
        if (!settings.ExternalPostingAllowed)
        {
            return;
        }

        await SendAsync("X", post, () => PostToXAsync(post));
        await SendAsync("Bluesky", post, () => PostAsync(Platform.Bluesky, post.BlueskyText ?? post.Text));
        await SendAsync("LinkedIn", post, () => PostAsync(Platform.LinkedIn, post.LinkedInText ?? post.Text));
        await SendAsync("Mastodon", post, () => PostAsync(Platform.Mastodon, post.MastodonText ?? post.Text));
        if (includeThreads)
        {
            await SendAsync("Threads", post, () => PostAsync(Platform.Threads, post.ThreadsText ?? post.Text));
        }
    }

    private async Task SendAsync(string platform, SocialPost post, Func<Task> send)
    {
        if (!post.Targets(platform)) return;
        try
        {
            await send();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Error when posting {PostKey} to {Platform}", post.Key, platform);
            await emailService.SendExceptionEmail(
                $"Error when posting {post.Key} to {platform}", exception, post.Text);
        }
    }

    private async Task PostToXAsync(SocialPost post)
    {
        var text = post.TwitterText ?? post.Text;
        var credentials = XCredentials();
        if (credentials.Values.Any(string.IsNullOrWhiteSpace))
        {
            // Without X API credentials, fall back to emailing a pre-filled intent link.
            var intentUrl = TwitterIntentHelper.BuildIntentUrl(text, settings.TwitterViaHandle);
            await emailService.SendTwitterIntentEmail($"Ready to post on X: {post.Title}", intentUrl, text);
            return;
        }

        await PostAsync(Platform.X, text);
    }

    private async Task PostAsync(Platform platform, string text)
    {
        var rendered = PostRenderer.Render(platform, text, _ => null);
        if (rendered.IsEmpty) throw new InvalidOperationException($"The {platform} post is empty.");
        if (rendered.IsOverLimit)
        {
            throw new InvalidOperationException(
                $"The {platform} post is {rendered.Length}/{rendered.Limit} characters.");
        }

        var credentials = CredentialStore(platform);
        using var http = httpClientFactory.CreateClient(ClientName(platform));
        var url = platform switch
        {
            Platform.X => await new XClient(http, credentials).PostAsync(rendered, []),
            Platform.Bluesky => await new BlueskyClient(http, credentials).PostAsync(rendered, []),
            Platform.LinkedIn => await PostToLinkedInAsync(http, credentials, rendered),
            Platform.Mastodon => await new MastodonClient(http, credentials).PostAsync(rendered, []),
            Platform.Threads => await new ThreadsClient(http, credentials).PostAsync(rendered, []),
            _ => throw new ArgumentOutOfRangeException(nameof(platform))
        };
        logger.LogInformation("{Platform} post published: {Url}", platform, url);
    }

    private static async Task<string> PostToLinkedInAsync(HttpClient http, ICredentialStore credentials, RenderedPost rendered)
    {
        var client = new LinkedInClient(http, credentials);
        // Resolves the person URN from the access token and stores it in the attempt-local credentials.
        await client.VerifyAccessTokenAsync();
        return await client.PostAsync(rendered, []);
    }

    // An isolated in-memory credential set per publish attempt.
    private InMemoryCredentialStore CredentialStore(Platform platform) =>
        new(platform switch
        {
            Platform.X => XCredentials(),
            Platform.Bluesky => new Dictionary<string, string?>
            {
                [SettingKeys.BlueskyHandle] = settings.BlueskyUserName,
                [SettingKeys.BlueskyAppPassword] = settings.BlueskyPassword
            },
            Platform.LinkedIn => new Dictionary<string, string?>
            {
                [SettingKeys.LinkedInAccessToken] = settings.LinkedInAccessToken
            },
            Platform.Mastodon => new Dictionary<string, string?>
            {
                [SettingKeys.MastodonInstance] = MastodonInstance(),
                [SettingKeys.MastodonAccessToken] = settings.MastodonAccessToken
            },
            Platform.Threads => new Dictionary<string, string?>
            {
                [SettingKeys.ThreadsUserId] = settings.ThreadsUserId,
                [SettingKeys.ThreadsAccessToken] = settings.ThreadsToken
            },
            _ => throw new ArgumentOutOfRangeException(nameof(platform))
        });

    private Dictionary<string, string?> XCredentials() => new()
    {
        [SettingKeys.XConsumerKey] = settings.TwitterConsumerKey,
        [SettingKeys.XConsumerSecret] = settings.TwitterConsumerSecret,
        [SettingKeys.XAccessToken] = settings.TwitterAccessToken,
        [SettingKeys.XAccessTokenSecret] = settings.TwitterAccessSecret
    };

    private string MastodonInstance()
    {
        var instance = string.IsNullOrWhiteSpace(settings.MastodonInstance)
            ? DefaultMastodonInstance
            : settings.MastodonInstance.Trim();
        return instance.Contains("://", StringComparison.Ordinal) ? instance : $"https://{instance}";
    }

    // AddHttpClient<T> names these typed clients after the simple type name.
    private static string ClientName(Platform platform) => platform switch
    {
        Platform.X => nameof(XClient),
        Platform.Bluesky => nameof(BlueskyClient),
        Platform.LinkedIn => nameof(LinkedInClient),
        Platform.Mastodon => nameof(MastodonClient),
        Platform.Threads => nameof(ThreadsClient),
        _ => throw new ArgumentOutOfRangeException(nameof(platform))
    };
}
