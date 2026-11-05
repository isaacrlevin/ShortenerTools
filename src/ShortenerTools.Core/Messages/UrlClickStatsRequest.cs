namespace ShortenerTools.Core.Messages
{
    /// <summary>Selects click statistics for all links or a single vanity.</summary>
    public class UrlClickStatsRequest
    {
        /// <summary>Gets or sets the vanity; omitted or empty values select all links.</summary>
        public string? Vanity { get; set; }

        /// <summary>Gets or sets the inclusive first calendar date, in yyyy-MM-dd format.</summary>
        public string? StartDate { get; set; }

        /// <summary>Gets or sets the inclusive last calendar date, in yyyy-MM-dd format.</summary>
        public string? EndDate { get; set; }

        /// <summary>Gets or sets the display time zone; omitted values use the server's local zone.</summary>
        public string? TimeZoneId { get; set; }

        /// <summary>Gets or sets whether clicks flagged <c>IsCrawler</c> (link-preview fetchers, detected crawlers) are left
        /// out. Defaults to false so existing callers keep every click.</summary>
        public bool ExcludeCrawlers { get; set; }

        /// <summary>Creates a request compatible with the existing raw statistics endpoint.</summary>
        /// <param name="vanity">The optional short URL vanity.</param>
        public UrlClickStatsRequest(string? vanity)
        {
            Vanity = vanity;
        }
    }
}