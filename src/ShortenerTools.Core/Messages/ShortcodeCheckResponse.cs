namespace ShortenerTools.Core.Messages
{
    /// <summary>Whether a shortcode can still be claimed, and why not when it cannot.</summary>
    public class ShortcodeCheckResponse
    {
        public bool Available { get; set; }

        /// <summary>Empty when the code is available; otherwise the reason it is not.</summary>
        public string Message { get; set; } = string.Empty;
    }
}
