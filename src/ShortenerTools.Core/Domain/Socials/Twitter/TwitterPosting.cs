using System.Text.RegularExpressions;

namespace ShortenerTools.Core.Domain.Socials.Twitter
{
    /// <summary>
    /// Helper for building X (Twitter) intent URLs that allow one-click posting
    /// without requiring API credentials.
    /// Documentation: https://docs.x.com/x-for-websites/post-button/overview
    /// </summary>
    public static class TwitterIntentHelper
    {
        private const string IntentBaseUrl = "https://x.com/intent/post";

        /// <summary>
        /// Builds an X intent URL for one-click posting.
        /// Hashtags already present in the text are preserved in-place.
        /// The optional <paramref name="via"/> parameter adds a username attribution (@handle).
        /// </summary>
        /// <param name="text">The full tweet text (may include #hashtags).</param>
        /// <param name="via">Optional Twitter/X username to attribute the post to (with or without leading @).</param>
        public static string BuildIntentUrl(string text, string? via = null)
        {
            var queryParams = new List<string>
            {
                $"text={Uri.EscapeDataString(text)}"
            };

            if (!string.IsNullOrWhiteSpace(via))
            {
                queryParams.Add($"via={Uri.EscapeDataString(via.TrimStart('@'))}");
            }

            return $"{IntentBaseUrl}?{string.Join("&", queryParams)}";
        }

        /// <summary>
        /// Extracts all hashtags (e.g. #azure, #dotnet) found in the given text.
        /// </summary>
        public static IReadOnlyList<string> ExtractHashtags(string text)
        {
            return Regex.Matches(text, @"#\w+")
                        .Select(m => m.Value)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
        }
    }
}
