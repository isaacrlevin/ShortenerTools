using ShortenerTools.Core.Domain;
using ShortenerTools.Core.Messages;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;

namespace ShortenerTools.Functions.Functions;

/// <summary>Provides daily aggregates while leaving the raw statistics endpoint unchanged.</summary>
public class UrlClickStatsByDay
{
    private readonly ILogger _logger;
    private readonly ShortenerSettings _settings;
    private readonly StorageTableHelper _storage;

    /// <summary>Creates the daily statistics function.</summary>
    /// <param name="loggerFactory">The application logger factory.</param>
    /// <param name="settings">The existing shortener configuration.</param>
    /// <param name="storage">The application's table storage helper.</param>
    public UrlClickStatsByDay(ILoggerFactory loggerFactory, ShortenerSettings settings, StorageTableHelper storage)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(storage);
        _logger = loggerFactory.CreateLogger<UrlClickStatsByDay>();
        _settings = settings;
        _storage = storage;
    }

    /// <summary>Returns daily counts for all links or one vanity and an optional inclusive date range.</summary>
    /// <param name="req">A JSON request with optional Vanity, StartDate, EndDate, and TimeZoneId.</param>
    /// <returns>Daily counts, or an explicit validation/server error response.</returns>
    [Function("UrlClickStatsByDay")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "api/UrlClickStatsByDay")] HttpRequestData req)
    {
        ArgumentNullException.ThrowIfNull(req);
        UrlClickStatsRequest input;
        try
        {
            input = await JsonSerializer.DeserializeAsync<UrlClickStatsRequest>(req.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new ArgumentException("A statistics request is required.");
            _ = new DailyClickAggregation(input);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            _logger.LogWarning(ex, "Invalid daily statistics request.");
            var invalid = req.CreateResponse(HttpStatusCode.BadRequest);
            await invalid.WriteAsJsonAsync(new { Message = ex.Message });
            invalid.StatusCode = HttpStatusCode.BadRequest;
            return invalid;
        }

        try
        {
            var result = await _storage.GetDailyStats(input);
            if (!string.IsNullOrEmpty(input.Vanity))
            {
                var host = string.IsNullOrEmpty(_settings.CustomDomain)
                    ? req.Url.GetLeftPart(UriPartial.Authority)
                    : _settings.CustomDomain;
                result.Url = Utility.GetShortUrl(host.TrimEnd('/'), input.Vanity);
            }
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(result);
            return response;
        }
        catch (Exception ex) when (ex is Azure.RequestFailedException or FormatException or ArgumentException or InvalidOperationException)
        {
            _logger.LogError(ex, "Unable to aggregate daily click statistics.");
            var failure = req.CreateResponse(HttpStatusCode.InternalServerError);
            await failure.WriteAsJsonAsync(new { Message = "Unable to load daily click statistics." });
            failure.StatusCode = HttpStatusCode.InternalServerError;
            return failure;
        }
    }
}
