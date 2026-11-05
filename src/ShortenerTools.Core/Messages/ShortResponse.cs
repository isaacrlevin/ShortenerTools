namespace ShortenerTools.Core.Messages
{
    public class ShortResponse
    {
        public string ShortUrl { get; set; }
        public string LongUrl { get; set; }
        public string Title { get; set; }

        public string Message { get; set; }

        public ShortResponse() { }
        public ShortResponse(string host, string longUrl, string endUrl, string title, string message)
        {
            LongUrl = longUrl;
            ShortUrl = string.Concat(host, "/", endUrl);
            Title = title;
            Message = message;
        }
    }
}