using ShortenerTools.Core.Domain;
using System.ComponentModel.DataAnnotations;

namespace ShortenerTools.Core.Messages
{
    public class ShortUrlRequest : IValidatableObject
    {
        private string _vanity;

        public string Title { get; set; } = string.Empty;

        public string Message { get; set; } = string.Empty;

        public string? CreationId { get; set; }

        public bool PostToSocial { get; set; } = true;

        /// <summary>Optional per-account post text; each entry is stored as a new version.</summary>
        public List<AccountPost>? Posts { get; set; }

        public string Vanity
        {
            get
            {
                return _vanity != null ? _vanity : string.Empty;
            }
            set
            {
                _vanity = value;
            }
        }

        [Required]
        [Url]
        public string Url { get; set; }

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (ShortcodePolicy.GetError(Vanity) is { } error)
                yield return new ValidationResult(error, [nameof(Vanity)]);
        }
    }
}
