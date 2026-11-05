using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MudBlazor;
using Polly;
using ShortenerTools.Core.Domain;
using ShortenerTools.Core.Messages;
using SocialPoster.Components;
using SocialPoster.Core;
using SocialPoster.Core.Services;
using SocialPoster.Posting;
using SocialPoster.Posting.Services;

namespace ShortenerTools.Admin;

/// <summary>Creates the draft's short link before SocialPoster publishes or freezes a schedule.</summary>
public sealed class ComposerLinkWorkflow(IHttpClientFactory clients, ContactService? contacts = null,
    IDialogService? dialogs = null) : IComposerWorkflow
{
    public string Vanity { get; set; } = "";
    public string Title { get; set; } = "";
    public string LastShortUrl { get; private set; } = "";

    /// <summary>
    /// When set, Publish creates the link as unposted for the ShortPostTimer function and stops before
    /// SocialPoster posts anything. The host shows a modal, then aborts with ComposerStagedException, which the
    /// composer treats as a quiet stop rather than an error.
    /// </summary>
    public bool StageForTimer { get; set; }

    public async Task<ComposerSubmission> PrepareAsync(ComposerSubmission submission)
    {
        var source = submission.SourceUrl.Trim();
        if (string.IsNullOrEmpty(source))
        {
            // Staging must never fall through to an immediate post.
            if (StageForTimer)
                throw new InvalidOperationException(
                    "Enter a destination URL before staging for the timer. Nothing was posted.");
            if (!string.IsNullOrWhiteSpace(Vanity))
                throw new InvalidOperationException("Enter a destination URL before choosing a custom shortcode.");
            return submission;
        }

        if (!Uri.TryCreate(source, UriKind.Absolute, out var destination) ||
            destination.Scheme is not ("https" or "http"))
            throw new InvalidOperationException("Enter a complete HTTP or HTTPS destination URL.");
        if (ShortcodePolicy.GetError(Vanity) is { } error)
            throw new InvalidOperationException(error);

        // Persisted draft identity plus immutable destination/code makes timeouts and reloads safe to retry.
        // Staging gets its own identity so it never collapses onto an already-posted link for the same draft.
        var identity = StageForTimer
            ? new[] { submission.DraftId, source, Vanity.Trim(), "staged" }
            : [submission.DraftId, source, Vanity.Trim()];
        var creationId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(identity))));
        var request = new ShortUrlRequest
        {
            Url = source, Vanity = Vanity.Trim(), CreationId = creationId, PostToSocial = StageForTimer,
            Title = string.IsNullOrWhiteSpace(Title) ? destination.Host : Title.Trim(),
            // The timer appends the short link itself, so a staged message must not carry the destination URL.
            Message = StageForTimer
                ? RemoveUrl(await RenderAsync(null, submission.BaseTemplate, submission), source)
                : submission.BaseTemplate,
            Posts = StageForTimer ? await StagedPostsAsync(submission, source) : null
        };
        using var response = await SendCreateAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var message = "The short link could not be created. Nothing was published or scheduled.";
            try
            {
                var body = await response.Content.ReadFromJsonAsync<JsonElement>();
                if (body.ValueKind == JsonValueKind.Object)
                    foreach (var property in body.EnumerateObject())
                        if (property.Name.Equals("Message", StringComparison.OrdinalIgnoreCase)
                            && property.Value.ValueKind == JsonValueKind.String)
                            message = property.Value.GetString();
            }
            catch (JsonException)
            {
            }

            throw new InvalidOperationException(message);
        }

        ShortResponse link;
        try
        {
            link = await response.Content.ReadFromJsonAsync<ShortResponse>();
            if (!Uri.TryCreate(link?.ShortUrl, UriKind.Absolute, out var shortUri)
                || shortUri.Scheme is not ("https" or "http"))
                throw new JsonException("Invalid short link response.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The link response was invalid. Retry to recover the same link.",
                exception);
        }

        var previous = LastShortUrl;
        LastShortUrl = link.ShortUrl;
        if (StageForTimer)
        {
            var notice = $"Staged for the next scheduled timer run as {link.ShortUrl}. Nothing was posted yet.";
            if (dialogs is not null)
                await dialogs.ShowMessageBoxAsync("Staged for the timer", notice, yesText: "OK");
            throw new ComposerStagedException(notice);
        }
        return submission with
        {
            BaseTemplate = Rewrite(submission.BaseTemplate, source, previous, link.ShortUrl),
            Templates = submission.Templates.ToDictionary(pair => pair.Key,
                pair => Rewrite(pair.Value, source, previous, link.ShortUrl))
        };
    }

    // The composer only reports the exception types it expects; anything else escapes and kills the circuit,
    // leaving Publish spinning. Resilience rejections (e.g. a cold management API) become a retryable message.
    private async Task<HttpResponseMessage> SendCreateAsync(ShortUrlRequest request)
    {
        using var client = clients.CreateClient("management");
        try
        {
            return await client.PostAsJsonAsync("/api/UrlCreate", request);
        }
        catch (ExecutionRejectedException exception)
        {
            throw new InvalidOperationException(
                "The link service did not respond in time. Nothing was published or scheduled; try again.",
                exception);
        }
    }

    private static string Rewrite(string text, string source, string previous, string shortUrl)
    {
        var found = false;
        var rewritten = Regex.Replace(text, "https?://[^\\s<>\"']+", match =>
        {
            var token = match.Value;
            while (token.Length > 0)
            {
                if (SameUrl(token, source) || SameUrl(token, previous) || SameUrl(token, shortUrl))
                {
                    found = true;
                    return shortUrl + match.Value[token.Length..];
                }

                if (token[^1] is not ('.' or ',' or ';' or '!' or '?' or ')' or ']' or '}')) break;
                token = token[..^1];
            }

            return match.Value;
        }, RegexOptions.IgnoreCase);
        return found ? rewritten : rewritten.TrimEnd() + (string.IsNullOrWhiteSpace(rewritten) ? "" : "\n") + shortUrl;
    }

    /// <summary>
    /// One version per selected platform, rendered to its final text (mentions resolved). The short link does not
    /// exist yet, so the destination URL becomes a token the timer function swaps for the real link.
    /// </summary>
    private async Task<List<AccountPost>> StagedPostsAsync(ComposerSubmission submission, string source)
    {
        var posts = new List<AccountPost>();
        foreach (var (platform, template) in submission.Templates)
        {
            var text = await RenderAsync(platform, template, submission);
            var tokenised = ReplaceUrl(text, source, PostVersionEntity.ShortUrlToken);
            if (!tokenised.Contains(PostVersionEntity.ShortUrlToken))
                tokenised = tokenised.TrimEnd() + (tokenised.Length == 0 ? "" : "\n") + PostVersionEntity.ShortUrlToken;
            posts.Add(new AccountPost { Account = platform.ToString().ToLowerInvariant(), Text = tokenised });
        }

        return posts;
    }

    /// <summary>Turns a template's <c>{@key}</c> mentions into the platform's handle, or plain names when there is none.</summary>
    private async Task<string> RenderAsync(Platform? platform, string template, ComposerSubmission submission)
    {
        var keys = submission.Templates.Values.Append(submission.BaseTemplate).SelectMany(MentionTemplate.GetMentionKeys)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var known = contacts is null || keys.Count == 0
            ? new Dictionary<string, Contact>(StringComparer.OrdinalIgnoreCase)
            : await contacts.GetByKeysAsync(keys);
        // The renderer shortens Bluesky URLs for display and keeps the full link in facets, but the timer builds its
        // own facets from the stored text, so Bluesky mentions are resolved here and URLs are left whole.
        if (platform is { } target and not Platform.Bluesky)
            return PostRenderer.Render(target, template, key => known.GetValueOrDefault(key)).DisplayText;
        return string.Concat(MentionTemplate.Parse(template).Select(segment => segment switch
        {
            MentionSegment { Key: var key } when platform == Platform.Bluesky
                                              && known.GetValueOrDefault(key) is { } contact
                                              && contact.HasIdentity(Platform.Bluesky) =>
                "@" + contact.BlueskyHandle!.TrimStart('@'),
            MentionSegment mention => known.GetValueOrDefault(mention.Key)?.DisplayName ?? mention.Raw,
            TextSegment text => text.Text,
            _ => ""
        }));
    }

    private static string RemoveUrl(string text, string url) => ReplaceUrl(text, url, "").Trim();

    private static string ReplaceUrl(string text, string url, string replacement) =>
        Regex.Replace(text, "https?://[^\\s<>\"']+", match =>
        {
            var token = match.Value;
            while (token.Length > 0)
            {
                if (SameUrl(token, url)) return replacement.Length == 0 ? "" : replacement + match.Value[token.Length..];
                if (token[^1] is not ('.' or ',' or ';' or '!' or '?' or ')' or ']' or '}')) break;
                token = token[..^1];
            }

            return match.Value;
        }, RegexOptions.IgnoreCase);

    private static bool SameUrl(string left, string right) =>
        Uri.TryCreate(left, UriKind.Absolute, out var a) && Uri.TryCreate(right, UriKind.Absolute, out var b)
                                                         && a.AbsoluteUri == b.AbsoluteUri;

    public string CaptureState() => JsonSerializer.Serialize(new SavedOptions(Vanity, Title, LastShortUrl, StageForTimer));

    public void RestoreState(string state)
    {
        Reset();
        if (string.IsNullOrEmpty(state)) return;
        try
        {
            if (JsonSerializer.Deserialize<SavedOptions>(state) is not { } options) return;
            Vanity = options.Vanity ?? "";
            Title = options.Title ?? "";
            LastShortUrl = options.LastShortUrl ?? "";
            StageForTimer = options.StageForTimer;
        }
        catch (JsonException)
        {
        }
    }

    public void Reset() => (Vanity, Title, LastShortUrl, StageForTimer) = ("", "", "", false);

    private sealed record SavedOptions(string Vanity, string Title, string LastShortUrl, bool StageForTimer = false);
}
