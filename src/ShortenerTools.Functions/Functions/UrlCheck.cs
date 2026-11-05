using Azure;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using ShortenerTools.Core.Domain;
using ShortenerTools.Core.Messages;
using System.Net;

namespace ShortenerTools.Functions.Functions;

/// <summary>Tells the admin site whether a custom shortcode can still be claimed, before it tries to create one.</summary>
public class UrlCheck
{
    private readonly ILogger _logger;
    private readonly StorageTableHelper _storage;

    /// <summary>Creates the availability check.</summary>
    /// <param name="loggerFactory">The application logger factory.</param>
    /// <param name="storage">The application's table storage helper.</param>
    public UrlCheck(ILoggerFactory loggerFactory, StorageTableHelper storage)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(storage);
        _logger = loggerFactory.CreateLogger<UrlCheck>();
        _storage = storage;
    }

    /// <summary>Checks one shortcode (the <c>code</c> query parameter) against the same rules creation enforces.</summary>
    /// <param name="req">A GET request such as <c>/api/UrlCheck?code=aca</c>.</param>
    /// <returns>
    /// 200 with <see cref="ShortcodeCheckResponse"/> when the code is valid (available or not), 400 when it is
    /// missing or breaks the shortcode rules, 500 when storage cannot be read.
    /// </returns>
    [Function("UrlCheck")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "api/UrlCheck")] HttpRequestData req)
    {
        ArgumentNullException.ThrowIfNull(req);
        var code = System.Web.HttpUtility.ParseQueryString(req.Url.Query)["code"]?.Trim();

        // Creation treats an empty vanity as "generate one", so only an explicit code can be checked.
        var error = string.IsNullOrEmpty(code) ? "Enter a shortcode to check." : ShortcodePolicy.GetError(code);
        if (error is not null)
            return await Respond(req, HttpStatusCode.BadRequest, new { Message = error });

        try
        {
            // The same point read the redirect and creation use, so archived links count as taken too.
            var existing = await _storage.GetShortUrlEntity(new ShortUrlEntity(string.Empty, code!));
            return await Respond(req, HttpStatusCode.OK, new ShortcodeCheckResponse
            {
                Available = existing is null,
                Message = existing switch
                {
                    null => string.Empty,
                    { IsArchived: true } => "This shortcode belongs to an archived link and cannot be reused.",
                    _ => "This shortcode is already in use."
                }
            });
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "Unable to check shortcode availability.");
            return await Respond(req, HttpStatusCode.InternalServerError,
                new { Message = "Unable to check the shortcode right now." });
        }
    }

    private static async Task<HttpResponseData> Respond<T>(HttpRequestData req, HttpStatusCode status, T body)
    {
        var response = req.CreateResponse(status);
        await response.WriteAsJsonAsync(body);
        // WriteAsJsonAsync can reset the status code, so set it explicitly afterwards.
        response.StatusCode = status;
        return response;
    }
}
