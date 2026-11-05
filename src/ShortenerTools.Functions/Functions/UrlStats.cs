using ShortenerTools.Core.Domain;
using ShortenerTools.Core.Messages;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;

namespace ShortenerTools.Functions.Functions
{
    public class UrlStats
    {
        private readonly ILogger _logger;
        private readonly ShortenerSettings _settings;

        public UrlStats(ILoggerFactory loggerFactory, ShortenerSettings settings)
        {
            _logger = loggerFactory.CreateLogger<UrlStats>();
            _settings = settings;
        }

        [Function("UrlStats")]
        public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "api/UrlStats")] HttpRequestData req,
        ExecutionContext context)
        {
            _logger.LogInformation($"HTTP trigger: Stats");

            string userId = string.Empty;
            UrlClickStatsRequest input;
            var result = new ClickStatsEntityList();

            try
            {
                StorageTableHelper stgHelper = new StorageTableHelper(_settings.DataStorage) { Logger = _logger };
                if (req != null)
                {
                    using (var reader = new StreamReader(req.Body))
                    {
                        var strBody = await reader.ReadToEndAsync();
                        input = (string.IsNullOrWhiteSpace(strBody)
                                    ? null
                                    : JsonSerializer.Deserialize<UrlClickStatsRequest>(strBody,
                                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }))
                                ?? new UrlClickStatsRequest(null);
                        // Optional StartDate/EndDate/TimeZoneId limit the rows read; without them every click is returned.
                        result.ClickStatsList = await stgHelper.GetStats(input);
                    }
                }
                else
                {
                    return req.CreateResponse(HttpStatusCode.NotFound);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unexpected error was encountered.");
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { Message = $"{ex.Message}" });
                return badRequest;
            }

            var writeTimer = System.Diagnostics.Stopwatch.StartNew();
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(result);
            _logger.LogInformation("UrlStats: serialized {Rows} rows in {ElapsedMs} ms.",
                result.ClickStatsList?.Count ?? 0, writeTimer.ElapsedMilliseconds);
            return response;
        }
    }
}
