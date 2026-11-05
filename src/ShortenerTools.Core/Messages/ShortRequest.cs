using ShortenerTools.Core.Domain;

namespace ShortenerTools.Core.Messages
{
    public class ShortRequest
    {
        public string Vanity { get; set; }

        public string? CreationId { get; set; }

        public string Url { get; set; }

        public string Title { get; set; }

        public string Message { get; set; }

        public bool PostToSocial { get; set; } = true;

        /// <summary>Optional per-account post text; each entry is stored as a new version.</summary>
        public List<AccountPost>? Posts { get; set; }
    }

    public class AccountPost
    {
        public string Account { get; set; } = string.Empty;

        public string Text { get; set; } = string.Empty;
    }
}
