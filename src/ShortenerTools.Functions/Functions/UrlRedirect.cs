using Azure.Core;
using ShortenerTools.Core.Domain;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Abstractions;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace ShortenerTools.Functions
{
    public class UrlRedirect
    {
        private readonly ILogger _logger;
        private readonly ShortenerSettings _settings;
        private readonly StorageTableHelper _storage;


        /// <summary>Creates the redirect function using the existing settings and storage helper.</summary>
        /// <param name="loggerFactory">The application logger factory.</param>
        /// <param name="settings">The shortener configuration.</param>
        /// <param name="storage">The application's table storage helper.</param>
        public UrlRedirect(ILoggerFactory loggerFactory, ShortenerSettings settings, StorageTableHelper storage)
        {
            ArgumentNullException.ThrowIfNull(loggerFactory);
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentNullException.ThrowIfNull(storage);
            _logger = loggerFactory.CreateLogger<UrlRedirect>();
            _settings = settings;
            _storage = storage;
        }

        /// <summary>
        /// User-agent fragments of the link-preview fetchers for the networks SocialPoster posts to (X, LinkedIn,
        /// Facebook/Threads, Bluesky). They must follow the redirect to build a card from the destination page, so
        /// they bypass the "bot" filter. Their visits are still recorded, flagged with <see cref="ClickStatsEntity.IsCrawler"/>.
        /// SocialPoster's own Open Graph scraper (its fallback when Bluesky's card service fails) is listed so its
        /// fetches are flagged too. Meta's AI crawler (meta-externalagent) is deliberately not included.
        /// </summary>
        internal static readonly string[] LinkPreviewAgents =
            ["Twitterbot", "LinkedInBot", "facebookexternalhit", "Bluesky Cardyb", "SocialPoster/"];

        /// <summary>robots.txt product tokens allowed to crawl short links; everything else is disallowed.</summary>
        private static readonly string[] LinkPreviewRobotsTokens =
            ["Twitterbot", "LinkedInBot", "facebookexternalhit", "Cardyb"];

        internal static readonly string RobotsBody =
            string.Concat(LinkPreviewRobotsTokens.Select(token => $"User-agent: {token}\n")) +
            "Disallow:\n\nUser-agent: *\nDisallow: /";

        internal static bool IsLinkPreviewAgent(string? userAgent) =>
            userAgent is { Length: > 0 } agentText &&
            LinkPreviewAgents.Any(agent => agentText.Contains(agent, StringComparison.OrdinalIgnoreCase));

        [Function("UrlRedirect")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "{shortUrl}")]
                HttpRequestData req,
            string shortUrl,
            ExecutionContext context)
        {
            string redirectUrl = "https://isaaclevin.com";

            if (shortUrl == "robots.txt")
            {
                _logger.LogInformation("Request for robots.txt.");
                var resp = req.CreateResponse(HttpStatusCode.OK);
                resp.WriteString(RobotsBody,
                    System.Text.Encoding.UTF8);
                return resp;
            }

            if (shortUrl.Contains("."))
            {
                _logger.LogInformation($"Request for {shortUrl}.");
                var resp = req.CreateResponse(HttpStatusCode.OK);
                await resp.WriteStringAsync(RobotsBody,
                    System.Text.Encoding.UTF8);
                return resp;
            }

            if (!string.IsNullOrWhiteSpace(shortUrl))
            {
                redirectUrl = _settings.DefaultRedirectUrl ?? redirectUrl;

                var tempUrl = new ShortUrlEntity(string.Empty, shortUrl);
                var newUrl = await _storage.GetShortUrlEntity(tempUrl);

                if (newUrl != null && !newUrl.IsArchived)
                {
                    _logger.LogInformation("Found active redirect.");

                    var referrer = string.Empty;
                    
                    if (req.Headers.TryGetValues("Referer", out var referrerValues))
                    {                        
                        referrer = referrerValues.FirstOrDefault();
                    }

                    var userAgent = string.Empty;
                    if (req.Headers.TryGetValues("User-Agent", out var userAgentValues))
                    {
                        userAgent = string.Join(" ", userAgentValues);

                    }

                    var isLinkPreview = IsLinkPreviewAgent(userAgent);
                    if (!isLinkPreview && userAgent.Contains("bot", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation($"Request for {shortUrl}.");
                        var resp = req.CreateResponse(HttpStatusCode.OK);
                        resp.WriteString(RobotsBody,
                            System.Text.Encoding.UTF8);
                        return resp;
                    }

                    AnalyticsEntry parsed = new AnalyticsEntry
                    {
                        Agent = userAgent,
                        Referrer = (!string.IsNullOrEmpty(referrer) ? new Uri(referrer) : null),
                        LongUrl = new Uri(newUrl.Url),
                        ShortUrl = newUrl.RowKey,
                        TimeStamp = DateTime.UtcNow                         
                    };

                    var page = parsed.LongUrl.AsPage(HttpUtility.ParseQueryString);

                    _logger.LogInformation("Recording redirect click.");

                    var click = new ClickStatsEntity(parsed, page);
                    if (isLinkPreview)
                    {
                        click.IsCrawler = true;
                    }

                    await _storage.SaveClickStatsEntity(click);

                    redirectUrl = WebUtility.UrlDecode(newUrl.Url);
                }
                else
                {
                    _logger.LogInformation("Missing or archived link {ShortUrl}; using fallback.", shortUrl);
                }
            }
            else
            {
                _logger.LogInformation("Bad Link, resorting to fallback.");
            }

            var res = req.CreateResponse(HttpStatusCode.Redirect);
            res.Headers.Add("Location", redirectUrl);
            return res;

        }
    }
}
