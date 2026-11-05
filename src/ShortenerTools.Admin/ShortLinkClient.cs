using System.Net;
using System.Text.Json;
using ShortenerTools.Core.Domain;
using ShortenerTools.Core.Messages;

namespace ShortenerTools.Admin;

/// <summary>Whether a typed shortcode can be claimed.</summary>
public enum ShortcodeStatus
{
    Available,
    InUse,
    Invalid
}

/// <summary>The outcome of checking one shortcode; <see cref="Message"/> explains anything but Available.</summary>
public sealed record ShortcodeCheck(ShortcodeStatus Status, string Message);

/// <summary>The outcome of creating a link: the short URL on success, otherwise the reason it failed.</summary>
public sealed record ShortLinkCreation(string? ShortUrl, string? Error, bool Conflict = false)
{
    public bool Succeeded => ShortUrl is not null;
}

/// <summary>Creates links and checks custom shortcodes through the management API.</summary>
public sealed class ShortLinkClient(HttpClient http)
{
    /// <summary>Asks the management API whether <paramref name="code"/> is free. Throws on transport or server errors.</summary>
    public async Task<ShortcodeCheck> CheckAsync(string code, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("/api/UrlCheck?code=" + Uri.EscapeDataString(code), cancellationToken);
        if (response.StatusCode == HttpStatusCode.BadRequest)
            return new ShortcodeCheck(ShortcodeStatus.Invalid,
                await ReadMessageAsync(response, cancellationToken) ?? "This shortcode is not allowed.");

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ShortcodeCheckResponse>(cancellationToken)
                     ?? throw new JsonException("The shortcode check response was empty.");
        return result.Available
            ? new ShortcodeCheck(ShortcodeStatus.Available, string.Empty)
            : new ShortcodeCheck(ShortcodeStatus.InUse,
                string.IsNullOrWhiteSpace(result.Message) ? "This shortcode is already in use." : result.Message);
    }

    /// <summary>
    /// Creates a link that is never queued for posting (<c>PostToSocial = false</c>). <paramref name="creationId"/> makes
    /// a retried or double-clicked request return the link the first attempt made.
    /// </summary>
    public async Task<ShortLinkCreation> CreateAsync(string url, string? vanity, string creationId,
        CancellationToken cancellationToken = default)
    {
        var destination = url.Trim();
        var request = new ShortUrlRequest
        {
            Url = destination,
            Vanity = vanity?.Trim() ?? string.Empty,
            CreationId = creationId,
            PostToSocial = false,
            Title = Uri.TryCreate(destination, UriKind.Absolute, out var uri) ? uri.Host : string.Empty
        };
        using var response = await http.PostAsJsonAsync("/api/UrlCreate", request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return new ShortLinkCreation(null,
                await ReadMessageAsync(response, cancellationToken) ?? "Unable to create the link.",
                response.StatusCode == HttpStatusCode.Conflict);

        var created = await response.Content.ReadFromJsonAsync<ShortResponse>(cancellationToken);
        return string.IsNullOrWhiteSpace(created?.ShortUrl)
            ? throw new JsonException("The short link response was empty.")
            : new ShortLinkCreation(created.ShortUrl, null);
    }

    private static async Task<string?> ReadMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            if (body.ValueKind == JsonValueKind.Object)
                foreach (var property in body.EnumerateObject())
                    if (property.Name.Equals("Message", StringComparison.OrdinalIgnoreCase)
                        && property.Value.ValueKind == JsonValueKind.String)
                        return property.Value.GetString();
        }
        catch (JsonException)
        {
        }

        return null;
    }
}
