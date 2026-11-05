/*
```c#
Input:

    {
        // [Required] The url you wish to have a short version for
        "url": "https://docs.microsoft.com/en-ca/azure/azure-functions/functions-create-your-first-function-visual-studio",

        // [Optional] Title of the page, or text description of your choice.
        "title": "Quickstart: Create your first function in Azure using Visual Studio"

        // [Optional] the end of the URL. If nothing one will be generated for you.
        "vanity": "azFunc"
    }

Output:
    {
        "ShortUrl": "http://c5m.ca/azFunc",
        "LongUrl": "https://docs.microsoft.com/en-ca/azure/azure-functions/functions-create-your-first-function-visual-studio"
    }
*/

using ShortenerTools.Core.Domain;
using ShortenerTools.Core.Messages;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ShortenerTools.Functions
{
    public class UrlCreate
    {
        private readonly ILogger _logger;
        private readonly ShortenerSettings _settings;
        private readonly StorageTableHelper? _storage;

        public UrlCreate(ILoggerFactory loggerFactory, ShortenerSettings settings, StorageTableHelper? storage = null)
        {
            _logger = loggerFactory.CreateLogger<UrlList>();
            _settings = settings;
            _storage = storage;
        }

        [Function("UrlCreate")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "api/UrlCreate")]
            HttpRequestData req,
            ExecutionContext context
        )
        {
            _logger.LogInformation($"__trace creating shortURL: {req}");
            string userId = string.Empty;
            ShortRequest input;
            var result = new ShortResponse();

            try
            {
                // Validation of the inputs
                if (req == null)
                {
                    return req.CreateResponse(HttpStatusCode.NotFound);
                }

                using (var reader = new StreamReader(req.Body))
                {
                    var strBody = await reader.ReadToEndAsync();
                    input = JsonSerializer.Deserialize<ShortRequest>(strBody,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (input == null)
                    {
                        return req.CreateResponse(HttpStatusCode.NotFound);
                    }
                }

                // If the Url parameter only contains whitespaces or is empty return with BadRequest.
                if (string.IsNullOrWhiteSpace(input.Url))
                {
                    var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badResponse.WriteAsJsonAsync(new { Message = "The url parameter can not be empty." });
                    return badResponse;
                }

                // Validates if input.url is a valid aboslute url, aka is a complete refrence to the resource, ex: http(s)://google.com
                if (!Uri.TryCreate(input.Url.Trim(), UriKind.Absolute, out var destination) ||
                    destination.Scheme is not ("https" or "http"))
                {
                    var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badResponse.WriteAsJsonAsync(new
                    {
                        Message =
                            $"{input.Url} is not a valid absolute Url. The Url parameter must start with 'http://' or 'https://'."
                    });
                    return badResponse;
                }

                StorageTableHelper stgHelper = _storage ?? new StorageTableHelper(_settings.DataStorage);

                string longUrl = input.Url.Trim();
                string vanity = string.IsNullOrWhiteSpace(input.Vanity) ? "" : input.Vanity.Trim();
                string title = string.IsNullOrWhiteSpace(input.Title) ? "" : input.Title.Trim();
                string message = string.IsNullOrWhiteSpace(input.Message) ? "" : input.Message.Trim();
                bool postToSocial = input.PostToSocial;

                var creationId = string.IsNullOrWhiteSpace(input.CreationId) ? null : input.CreationId.Trim();
                if (creationId is { Length: > 128 })
                {
                    var invalid = req.CreateResponse(HttpStatusCode.BadRequest);
                    await invalid.WriteAsJsonAsync(new { Message = "CreationId must be at most 128 characters." });
                    return invalid;
                }


                var shortcodeError = ShortcodePolicy.GetError(vanity);
                if (shortcodeError is not null)
                {
                    var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badResponse.WriteAsJsonAsync(new { Message = shortcodeError });
                    return badResponse;
                }

                ShortUrlEntity? newRow = null;
                var created = false;
                // A retried identified creation returns the link its first attempt generated.
                if (string.IsNullOrEmpty(vanity) && creationId is not null
                    && await stgHelper.GetShortUrlEntityByCreationId(creationId) is { } existing
                    && existing.Url == longUrl)
                {
                    newRow = existing;
                    created = true;
                }

                // Custom codes are claimed once. Generated codes can retry a concurrent collision.
                for (var attempt = 0; !created && attempt < 10; attempt++)
                {
                    var code = !string.IsNullOrEmpty(vanity) ? vanity : await Utility.GetValidEndUrl("", stgHelper);
                    var candidate = new ShortUrlEntity(longUrl, code, title, message, postToSocial)
                    {
                        CreationId = creationId
                    };
                    newRow = await stgHelper.CreateOrGetShortUrlEntity(candidate);
                    created = newRow is not null;
                    if (created || !string.IsNullOrEmpty(vanity)) break;
                }

                if (!created || newRow is null)
                {
                    var conflict = req.CreateResponse(HttpStatusCode.Conflict);
                    await conflict.WriteAsJsonAsync(new
                    {
                        Message =
                            "This shortcode already exists. Choose another shortcode or retry automatic generation."
                    });
                    return conflict;
                }

                foreach (var post in input.Posts ?? [])
                {
                    if (string.IsNullOrWhiteSpace(post.Account) || string.IsNullOrWhiteSpace(post.Text)) continue;
                    // A retried creation returns the existing link; skip versions already stored for that text.
                    var latest = await stgHelper.GetLatestPostVersion(newRow.RowKey, post.Account);
                    if (latest is { IsLegacy: false } && latest.Text == post.Text.Trim()) continue;
                    await stgHelper.AddPostVersion(new PostVersionEntity(
                        newRow.RowKey, post.Account, post.Text.Trim(), newRow.Url));
                }

                var host = string.IsNullOrEmpty(_settings.CustomDomain)
                    ? req.Url.Host
                    : _settings.CustomDomain.ToString();
                result = new ShortResponse(host, newRow.Url, newRow.RowKey, newRow.Title, newRow.Message);

                _logger.LogInformation("Short Url created.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unexpected error was encountered.");

                var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badResponse.WriteAsJsonAsync(new { ex.Message });
                return badResponse;
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(result);

            return response;
        }
    }
}