using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Configuration;

namespace ShortenerTools.Functions;

/// <summary>
/// Keeps management triggers inaccessible to browser clients. The admin host
/// supplies this server-side key after authenticating the administrator cookie.
/// The public redirect trigger is intentionally the only unauthenticated HTTP
/// function.
/// </summary>
public sealed class AdminApiKeyMiddleware(IConfiguration configuration) : IFunctionsWorkerMiddleware
{
    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var request = await context.GetHttpRequestDataAsync();
        if (request is null || string.Equals(context.FunctionDefinition.Name, "UrlRedirect", StringComparison.Ordinal))
        {
            await next(context);
            return;
        }

        var expectedKey = configuration["AdminApiKey"];
        if (!HasValidKey(request.Headers, expectedKey))
        {
            var response = request.CreateResponse(HttpStatusCode.Unauthorized);
            await response.WriteStringAsync("Authentication is required.");
            context.GetInvocationResult().Value = response;
            return;
        }

        await next(context);
    }

    internal static bool HasValidKey(HttpHeadersCollection headers, string? expectedKey) =>
        !string.IsNullOrWhiteSpace(expectedKey)
        && headers.TryGetValues("X-Admin-Api-Key", out var suppliedValues)
        && suppliedValues.Any(value => Matches(value, expectedKey));

    private static bool Matches(string supplied, string expected)
    {
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(suppliedHash, expectedHash);
    }
}
