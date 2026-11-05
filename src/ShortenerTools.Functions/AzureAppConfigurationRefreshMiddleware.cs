using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using ShortenerTools.Core.Domain;

namespace ShortenerTools.Functions;

public sealed class AzureAppConfigurationRefreshMiddleware(
    IConfigurationRefresherProvider refresherProvider,
    IConfiguration configuration,
    ShortenerSettings settings) : IFunctionsWorkerMiddleware
{
    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        foreach (var refresher in refresherProvider.Refreshers)
        {
            await refresher.TryRefreshAsync(context.CancellationToken);
        }

        settings.TwitterConsumerKey = configuration["TwitterConsumerKey"];
        settings.TwitterConsumerSecret = configuration["TwitterConsumerSecret"];
        settings.TwitterAccessToken = configuration["TwitterAccessToken"];
        settings.TwitterAccessSecret = configuration["TwitterAccessSecret"];
        settings.BlueskyUserName = configuration["BlueskyUserName"];
        settings.BlueskyPassword = configuration["BlueskyPassword"];
        settings.LinkedInAccessToken = configuration["LinkedInAccessToken"];
        settings.MastodonInstance = configuration["MastodonInstance"];
        settings.MastodonAccessToken = configuration["MastodonAccessToken"];
        settings.ThreadsToken = configuration["ThreadsToken"];
        settings.ThreadsUserId = configuration["ThreadsUserId"];
        // SocialPoster has no X "via" handle setting; this key is set directly in App Configuration.
        settings.TwitterViaHandle = configuration["TwitterViaHandle"];

        await next(context);
    }
}