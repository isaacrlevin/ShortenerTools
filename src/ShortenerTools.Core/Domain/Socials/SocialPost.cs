namespace ShortenerTools.Core.Domain.Socials;

public sealed record SocialPost(string Key, string Title, string Text, string Url)
{
    public string? TwitterText { get; init; }
    public string? MastodonText { get; init; }
    public string? BlueskyText { get; init; }
    public string? LinkedInText { get; init; }
    public string? ThreadsText { get; init; }

    /// <summary>Platforms to publish to (x, bluesky, linkedin, mastodon, threads); null publishes to all.</summary>
    public IReadOnlySet<string>? Platforms { get; init; }

    public bool Targets(string platform) => Platforms is null || Platforms.Contains(platform.ToLowerInvariant());
}

public interface ISocialMediaPublisher
{
    Task PublishAsync(SocialPost post, bool includeThreads = true);
}
