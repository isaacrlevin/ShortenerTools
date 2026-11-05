namespace ShortenerTools.Core.Domain
{
    public class ShortenerSettings
    {
        public string EnvironmentName { get; set; }

        public bool DisableExternalPosting { get; set; }

        // Staging cannot opt into real social or email traffic.
        public bool ExternalPostingAllowed =>
            !DisableExternalPosting &&
            !string.Equals(EnvironmentName, "Staging", StringComparison.OrdinalIgnoreCase);

        public string DefaultRedirectUrl { get; set; }
        public string CustomDomain { get; set; }
        public string DataStorage { get; set; }
        public string TwitterConsumerKey { get; set; }
        public string TwitterConsumerSecret { get; set; }
        public string TwitterAccessToken { get; set; }
        public string TwitterAccessSecret { get; set; }

        /// <summary>
        /// Optional: your Twitter/X handle (without @) added as the "via" attribution on intent URLs.
        /// </summary>
        public string TwitterViaHandle { get; set; }

        public string MastodonAccessToken { get; set; }
        public string MastodonInstance { get; set; } = "";
        public string LinkedInAccessToken { get; set; }
        public string BlueskyUserName { get; set; }
        public string BlueskyPassword { get; set; }
        public bool PostSocials { get; set; }
        public string EmailFrom { get; set; }
        public string EmailTo { get; set; }
        public string COMMUNICATION_SERVICES_CONNECTION_STRING { get; set; }

        public string ThreadsToken { get; set; }
        public string ThreadsUserId { get; set; }
    }
}