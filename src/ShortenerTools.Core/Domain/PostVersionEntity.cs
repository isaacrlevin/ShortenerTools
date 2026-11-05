using Azure;
using Azure.Data.Tables;

namespace ShortenerTools.Core.Domain
{
    /// <summary>
    /// One immutable version of the post text for a short link on a single social account.
    /// Stored in the PostVersions table: PartitionKey is the shortcode and RowKey is
    /// "{account}|{reverse ticks}", so a prefix range returns one account's versions newest first.
    /// </summary>
    public class PostVersionEntity : ITableEntity
    {
        public const string Separator = "|";

        /// <summary>Placeholder for the link's short URL, which is unknown when a version is authored.</summary>
        public const string ShortUrlToken = "{shorturl}";

        public string Account { get; set; } = string.Empty;

        public string Text { get; set; } = string.Empty;

        public string? Url { get; set; }

        /// <summary>Draft, Published, or Failed.</summary>
        public string Status { get; set; } = PostVersionStatus.Draft;

        public string? ExternalPostId { get; set; }

        public string? CreatedBy { get; set; }

        /// <summary>True for a synthesized version built from a link created before versioning existed.</summary>
        [System.Runtime.Serialization.IgnoreDataMember]
        public bool IsLegacy { get; set; }

        public string PartitionKey { get; set; } = string.Empty;
        public string RowKey { get; set; } = string.Empty;
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }

        public PostVersionEntity() { }

        public PostVersionEntity(string shortcode, string account, string text, string? url = null,
            string status = PostVersionStatus.Draft, DateTimeOffset? createdAt = null)
        {
            Account = account;
            PartitionKey = shortcode;
            RowKey = CreateRowKey(account, createdAt ?? DateTimeOffset.UtcNow);
            Text = text;
            Url = url;
            Status = status;
        }

        /// <summary>Builds a key whose lexical order is newest first within an account.</summary>
        public static string CreateRowKey(string account, DateTimeOffset createdAt) =>
            $"{SanitizeAccount(account)}{Separator}{(long.MaxValue - createdAt.UtcTicks):D19}";

        /// <summary>Removes characters Table Storage keys forbid, plus the key separator.</summary>
        public static string SanitizeAccount(string account) =>
            new(account.Trim().Select(c => c is '/' or '\\' or '#' or '?' or '|' || char.IsControl(c) ? '_' : c)
                .ToArray());

        /// <summary>Wraps a pre-versioning link's single message as version 1 so callers have one read path.</summary>
        public static PostVersionEntity FromLegacy(ShortUrlEntity link) => new()
        {
            PartitionKey = link.RowKey,
            RowKey = CreateRowKey("legacy", link.Timestamp ?? DateTimeOffset.UnixEpoch),
            Account = "legacy",
            Text = link.Message ?? string.Empty,
            Url = link.Url,
            Status = link.Posted ? PostVersionStatus.Published : PostVersionStatus.Draft,
            Timestamp = link.Timestamp,
            IsLegacy = true
        };
    }

    public static class PostVersionStatus
    {
        public const string Draft = "Draft";
        public const string Published = "Published";
        public const string Failed = "Failed";
    }
}
