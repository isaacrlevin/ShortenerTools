
using System.Dynamic;
using System.Web;
using System;
using Azure.Data.Tables;
using Azure;
using System.Runtime.Serialization;

namespace ShortenerTools.Core.Domain
{
    public class ClickStatsEntity : ITableEntity
    {
        //public string Id { get; set; }
        public string Datetime { get; set; }

        [IgnoreDataMember]
        public DateTime Date  => DateTime.Parse(Datetime).ToLocalTime();

        public string Page { get; set; }
        public string ShortUrl { get; set; }
        public string Campaign { get; set; }
        public string Agent { get; set; }
        public string Browser { get; set; }
        public object BrowserVersion { get; set; }
        public string BrowserWithVersion { get; set; }
        public bool IsDesktop { get; set; }
        public string Platform { get; set; }
        public object PlatformVersion { get; set; }
        public string PlatformWithVersion { get; set; }
        public string Host { get; set; }
        public string ReferrerUrl { get; set; }

        public string ReferrerHost { get; set; }


        public bool IsCrawler { get; set; }
        public bool IsMobile { get; set; }

        public string MobileManufacturer { get; set; }
        public string MobileModel { get; set; }
        public string MobileDevice { get; set; }

        // Visitor location from Cloudflare's geolocation headers. The visitor's IP address is never stored.
        // Clicks recorded before the short-link domain was proxied through Cloudflare have no location.
        public string? Country { get; set; }
        public string? Region { get; set; }
        public string? City { get; set; }

        // Approximate visitor position (one decimal place, about 11 km) for the Statistics map. Stored only as a pair.
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }

        public string PartitionKey { get; set; }
        public string RowKey { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }

        public ClickStatsEntity() { }

        public ClickStatsEntity(string vanity)
        {
            PartitionKey = vanity;
            RowKey = Guid.NewGuid().ToString();
            Datetime = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        }

        public ClickStatsEntity(AnalyticsEntry parsed, string page)
        {
            // cosmos DB 
            var normalize = new[] { '/' };


            this.Page = page.TrimEnd(normalize);
            if (!string.IsNullOrWhiteSpace(parsed.ShortUrl))
            {
                this.ShortUrl = parsed.ShortUrl;
            }
       
            if (parsed.Referrer != null)
            {
                this.ReferrerUrl = parsed.Referrer.AbsoluteUri;
                this.ReferrerHost = parsed.Referrer.DnsSafeHost;
            }
            if (!string.IsNullOrWhiteSpace(parsed.Agent))
            {
                this.Agent = parsed.Agent;
                try
                {
                    var parser = UAParser.Parser.GetDefault();
                    var client = parser.Parse(parsed.Agent);
                    {
                        var browser = client.UA.Family;
                        var version = client.UA.Major;
                        var browserVersion = $"{browser} {version}";
                        this.Browser = browser;
                        this.BrowserVersion = version;
                        this.BrowserWithVersion = browserVersion;
                    }
                    if (client.Device.IsSpider)
                    {
                        this.IsCrawler = true;
                    }
                    if (parsed.Agent.ToLowerInvariant().Contains("mobile"))
                    {
                        this.IsMobile = true;
                        var manufacturer = client.Device.Brand;
                        this.MobileManufacturer = manufacturer;
                        var model = client.Device.Model;
                        this.MobileModel = model;
                        this.MobileDevice = $"{manufacturer} {model}";
                    }
                    else
                    {
                        this.IsDesktop = true;
                    }
                    if (!string.IsNullOrWhiteSpace(client.OS.Family))
                    {
                        this.Platform = client.OS.Family;
                        this.PlatformVersion = client.OS.Major;
                        this.PlatformWithVersion = $"{client.OS.Family} {client.OS.Major}";
                    }
                }
                catch (Exception ex)
                {                  
                }
            }

            this.Country = parsed.Country;
            this.Region = parsed.Region;
            this.City = parsed.City;
            this.Latitude = parsed.Latitude;
            this.Longitude = parsed.Longitude;

            this.Timestamp = parsed.TimeStamp;
            this.Host = parsed.LongUrl.DnsSafeHost;
            this.PartitionKey = this.ShortUrl;
            this.RowKey = this.Timestamp.Value.Ticks.ToString();
            this.Datetime = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        }
    }
}