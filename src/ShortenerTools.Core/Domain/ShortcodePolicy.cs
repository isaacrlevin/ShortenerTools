namespace ShortenerTools.Core.Domain;

/// <summary>Rules for newly requested custom shortcodes; existing links are never rewritten.</summary>
public static class ShortcodePolicy
{
    public static string? GetError(string? shortcode)
    {
        if (string.IsNullOrWhiteSpace(shortcode)) return null;
        var value = shortcode.Trim();
        if (value.Length > 128 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            return "Use up to 128 letters, numbers, hyphens or underscores for the custom shortcode.";
        if (new[] { "KEY", "api", "health", "alive" }.Contains(value, StringComparer.OrdinalIgnoreCase))
            return "This shortcode is reserved. Choose a different custom shortcode.";
        return null;
    }
}