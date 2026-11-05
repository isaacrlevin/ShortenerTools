using Azure;
using Azure.Data.Tables;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.Json;

namespace ShortenerTools.Core.Domain
{
    public class ShortUrlEntity : ITableEntity
    {
        public string Url { get; set; }

        public string Title { get; set; }

        public string Message { get; set; }

        /// <summary>Optional idempotency token for links created by a SocialPoster draft.</summary>
        public string? CreationId { get; set; }

        public string ShortUrl { get; set; }

        public bool IsArchived { get; set; } = false;

        public bool Posted { get; set; }

        public string PartitionKey { get; set; }
        public string RowKey { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }

        //[IgnoreDataMember]
        //public int ClickCount { get; set; }

        public ShortUrlEntity() { }

        public ShortUrlEntity(string longUrl, string endUrl)
        {
            Initialize(longUrl, endUrl, string.Empty, string.Empty, true);
        }

        public ShortUrlEntity(string longUrl, string endUrl, string title)
        {
            Initialize(longUrl, endUrl, title, string.Empty, true);
        }


        public ShortUrlEntity(string longUrl, string endUrl, string title, string message)
        {
            Initialize(longUrl, endUrl, title, message, true);
        }

        public ShortUrlEntity(string longUrl, string endUrl, string title, string message, bool postToSocial)
        {
            Initialize(longUrl, endUrl, title, message, postToSocial);
        }

        private void Initialize(string longUrl, string endUrl, string title, string message, bool postToSocial)
        {
            PartitionKey = endUrl.First().ToString();
            RowKey = endUrl;
            Url = longUrl;
            Title = title;
            Message = message;
            IsArchived = false;
            Posted = !postToSocial;
        }

        public static ShortUrlEntity GetEntity(string longUrl, string endUrl, string title, string message)
        {
            return new ShortUrlEntity
            {
                PartitionKey = endUrl.First().ToString(),
                RowKey = endUrl,
                Url = longUrl,
                Title = title,
                Message = message
            };
        }
    }
}
