using System.Globalization;
using ShortenerTools.Core.Domain;

namespace ShortenerTools.Core.Messages
{
    /// <summary>Paging, sorting, and column filters for the URL manager.</summary>
    public class UrlListQuery
    {
        /// <summary>Number of matching links to skip.</summary>
        public int Skip { get; set; }

        /// <summary>Maximum number of links to return, up to 100.</summary>
        public int Take { get; set; } = 100;

        /// <summary>Column sorts in priority order.</summary>
        public List<UrlListSort> Sorts { get; set; } = new();

        /// <summary>Column filters, combined with AND.</summary>
        public List<UrlListFilter> Filters { get; set; } = new();

        /// <summary>Validates the paging bounds, columns, operators, and filter values.</summary>
        /// <exception cref="ArgumentException">The query is invalid.</exception>
        public void Validate()
        {
            if (Skip < 0 || Take < 1 || Take > 100)
                throw new ArgumentException("Skip must be nonnegative and take must be between 1 and 100.");
            if (Sorts == null || Filters == null)
                throw new ArgumentException("Sorts and filters must be arrays.");

            foreach (var sort in Sorts)
            {
                if (sort == null || !IsColumn(sort.Column))
                    throw new ArgumentException("Unsupported sort column.");
            }

            foreach (var filter in Filters)
            {
                if (filter == null || !IsColumn(filter.Column))
                    throw new ArgumentException("Unsupported filter column.");
                var operators = filter.Column == nameof(ShortUrlEntity.Timestamp)
                    ? new[] { "is", "is not", "is after", "is on or after", "is before", "is on or before", "is empty", "is not empty" }
                    : new[] { "contains", "not contains", "equals", "not equals", "starts with", "ends with", "is empty", "is not empty" };
                if (!operators.Contains(filter.Operator))
                    throw new ArgumentException("Unsupported filter operator.");
                if (filter.Operator is "is empty" or "is not empty")
                    continue;
                if (filter.Value == null)
                    throw new ArgumentException("A filter value is required.");
                if (filter.Column == nameof(ShortUrlEntity.Timestamp) &&
                    !DateTimeOffset.TryParse(filter.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
                    throw new ArgumentException("Invalid timestamp filter value.");
            }
        }

        /// <summary>Applies filters and deterministic sorting before selecting a page.</summary>
        /// <param name="items">Active links from storage.</param>
        /// <returns>The requested page and the total number of matching links.</returns>
        public ListResponse Apply(IEnumerable<ShortUrlEntity> items)
        {
            Validate();
            var matching = items.Where(item => Filters.All(filter => Matches(item, filter))).ToList();
            IOrderedEnumerable<ShortUrlEntity>? ordered = null;
            foreach (var sort in Sorts)
            {
                Func<ShortUrlEntity, IComparable?> key = item => GetValue(item, sort.Column);
                ordered = ordered == null
                    ? (sort.Descending ? matching.OrderByDescending(key) : matching.OrderBy(key))
                    : (sort.Descending ? ordered.ThenByDescending(key) : ordered.ThenBy(key));
            }

            ordered ??= matching.OrderByDescending(item => item.Timestamp);
            return new ListResponse(ordered.ThenBy(item => item.PartitionKey, StringComparer.Ordinal)
                .ThenBy(item => item.RowKey, StringComparer.Ordinal).Skip(Skip).Take(Take).ToList(), matching.Count);
        }

        private static bool IsColumn(string column) =>
            column is nameof(ShortUrlEntity.Timestamp) or nameof(ShortUrlEntity.RowKey)
                or nameof(ShortUrlEntity.Title) or nameof(ShortUrlEntity.Url);

        private static IComparable? GetValue(ShortUrlEntity item, string column) => column switch
        {
            nameof(ShortUrlEntity.Timestamp) => item.Timestamp,
            nameof(ShortUrlEntity.RowKey) => item.RowKey,
            nameof(ShortUrlEntity.Title) => item.Title,
            nameof(ShortUrlEntity.Url) => item.Url,
            _ => throw new ArgumentException("Unsupported column.")
        };

        private static bool Matches(ShortUrlEntity item, UrlListFilter filter)
        {
            var value = GetValue(item, filter.Column);
            if (filter.Column == nameof(ShortUrlEntity.Timestamp))
            {
                if (filter.Operator == "is empty")
                    return value == null;
                if (filter.Operator == "is not empty")
                    return value != null;
                if (item.Timestamp == null)
                    return false;
                var date = DateTimeOffset.Parse(filter.Value!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).Date;
                var timestampDate = item.Timestamp.Value.Date;
                return filter.Operator switch
                {
                    "is" => timestampDate == date,
                    "is not" => timestampDate != date,
                    "is after" => timestampDate > date,
                    "is on or after" => timestampDate >= date,
                    "is before" => timestampDate < date,
                    "is on or before" => timestampDate <= date,
                    _ => throw new ArgumentException("Unsupported timestamp filter operator.")
                };
            }

            var text = (string?)value;
            return filter.Operator switch
            {
                "is empty" => string.IsNullOrWhiteSpace(text),
                "is not empty" => !string.IsNullOrWhiteSpace(text),
                "contains" => text?.Contains(filter.Value!, StringComparison.OrdinalIgnoreCase) == true,
                "not contains" => text?.Contains(filter.Value!, StringComparison.OrdinalIgnoreCase) != true,
                "equals" => string.Equals(text, filter.Value, StringComparison.OrdinalIgnoreCase),
                "not equals" => !string.Equals(text, filter.Value, StringComparison.OrdinalIgnoreCase),
                "starts with" => text?.StartsWith(filter.Value!, StringComparison.OrdinalIgnoreCase) == true,
                "ends with" => text?.EndsWith(filter.Value!, StringComparison.OrdinalIgnoreCase) == true,
                _ => throw new ArgumentException("Unsupported string filter operator.")
            };
        }
    }

    /// <summary>A URL manager column sort.</summary>
    public class UrlListSort
    {
        /// <summary>The entity property to sort.</summary>
        public string Column { get; set; } = string.Empty;

        /// <summary>Whether to sort in descending order.</summary>
        public bool Descending { get; set; }
    }

    /// <summary>A URL manager column filter.</summary>
    public class UrlListFilter
    {
        /// <summary>The entity property to filter.</summary>
        public string Column { get; set; } = string.Empty;

        /// <summary>The column's filter operator.</summary>
        public string Operator { get; set; } = string.Empty;

        /// <summary>The text or round-trip timestamp value; empty operators need no value.</summary>
        public string? Value { get; set; }
    }
}
