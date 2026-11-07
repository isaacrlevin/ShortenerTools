using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShortenerTools.Core.Domain
{
    public class AnalyticsEntry
    {
        public string ShortUrl { get; set; }
        public Uri LongUrl { get; set; }
        public DateTime TimeStamp { get; set; }
        public Uri Referrer { get; set; }
        public string Agent { get; set; }

        /// <summary>ISO 3166-1 alpha-2 country code of the visitor, from Cloudflare's CF-IPCountry header.</summary>
        public string? Country { get; set; }

        /// <summary>Visitor region (state/province), from Cloudflare's visitor location headers.</summary>
        public string? Region { get; set; }

        /// <summary>Visitor city, from Cloudflare's visitor location headers.</summary>
        public string? City { get; set; }

        /// <summary>Visitor latitude from cf-iplatitude, rounded to one decimal place.</summary>
        public double? Latitude { get; set; }

        /// <summary>Visitor longitude from cf-iplongitude, rounded to one decimal place.</summary>
        public double? Longitude { get; set; }
    }
}
