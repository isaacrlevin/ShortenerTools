using System.Collections.Generic;
using ShortenerTools.Core.Domain;

namespace ShortenerTools.Core.Messages
{
    public class ListResponse
    {
        public List<ShortUrlEntity> UrlList { get; set; }
        public int TotalCount { get; set; }

        public ListResponse() { }
        public ListResponse(List<ShortUrlEntity> list)
        {
            UrlList = list;
        }
        public ListResponse(List<ShortUrlEntity> list, int totalCount)
        {
            UrlList = list;
            TotalCount = totalCount;
        }
    }
}
