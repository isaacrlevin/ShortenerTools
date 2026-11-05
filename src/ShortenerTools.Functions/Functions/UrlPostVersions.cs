using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using ShortenerTools.Core.Domain;
using System.Net;

namespace ShortenerTools.Functions
{
    /// <summary>Lists the stored post versions for a short link, optionally for one account.</summary>
    public class UrlPostVersions(ShortenerSettings settings, StorageTableHelper? storage = null)
    {
        [Function("UrlPostVersions")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "api/UrlPostVersions/{vanity}")]
            HttpRequestData req, string vanity)
        {
            var helper = storage ?? new StorageTableHelper(settings.DataStorage);
            var account = System.Web.HttpUtility.ParseQueryString(req.Url.Query)["account"];
            var versions = await helper.GetPostVersions(vanity, account);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(versions.Select(v => new
            {
                v.Account,
                v.Text,
                v.Url,
                v.Status,
                v.ExternalPostId,
                v.CreatedBy,
                v.IsLegacy,
                CreatedAt = v.Timestamp,
                v.RowKey
            }));
            return response;
        }
    }
}
