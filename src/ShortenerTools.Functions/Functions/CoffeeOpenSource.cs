using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using ShortenerTools.Core.Domain;
using ShortenerTools.Core.Domain.Coffee;
using ShortenerTools.Core.Domain.Socials;

namespace ShortenerTools.Functions.Functions;

public sealed class CoffeeOpenSource(
    ILogger<CoffeeOpenSource> logger,
    ShortenerSettings settings,
    ICoffeeGuestFeed feed,
    ISocialMediaPublisher publisher,
    TimeProvider clock)
{
    private bool PostingAllowed => settings.ExternalPostingAllowed && settings.PostSocials;

    [Function("COSSTeaserTimer")]
    public async Task COSSTeaserTimer([TimerTrigger("0 0 17 * * MON")] TimerInfo timer)
    {
        if (!Debugger.IsAttached)
        {
            await COSSTeaserAsync();
        }
    }

    [Function("COSSAnnouncementTimer")]
    public async Task COSSAnnouncementTimer([TimerTrigger("0 0 17 * * *")] TimerInfo timer)
    {
        if (!Debugger.IsAttached)
        {
            await COSSAnnouncementAsync();
        }
    }

    [Function("COSSArchiveTimer")]
    public async Task COSSArchiveTimer([TimerTrigger("0 0 16 * * MON")] TimerInfo timer)
    {
        if (!Debugger.IsAttached)
        {
            await COSSArchiveAsync();
        }
    }

    [Function("COSSTeaserHttp")]
    public Task COSSTeaserHttp([HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData request) =>
        COSSTeaserAsync();

    [Function("COSSAnnouncementHttp")]
    public Task COSSAnnouncementHttp([HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData request) =>
        COSSAnnouncementAsync();

    [Function("COSSArchiveHttp")]
    public Task COSSArchiveHttp([HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData request) =>
        COSSArchiveAsync();

    [Function("COSSPublishHttp")]
    public async Task<HttpResponseData> COSSPublishHttp(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData request)
    {
        if (!PostingAllowed)
        {
            logger.LogInformation("Coffee publishing is disabled in this environment.");
            return request.CreateResponse(HttpStatusCode.Forbidden);
        }
        string? guestKey;
        try
        {
            guestKey = await JsonSerializer.DeserializeAsync<string>(request.Body);
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Coffee publishing requires a JSON string guest key.");
            return request.CreateResponse(HttpStatusCode.BadRequest);
        }
        if (string.IsNullOrWhiteSpace(guestKey))
        {
            logger.LogWarning("Coffee publishing requires a nonempty guest key.");
            return request.CreateResponse(HttpStatusCode.BadRequest);
        }
        var guest = (await feed.GetGuestsAsync()).FirstOrDefault(
            guest => guest.IsPublished && guest.PartitionKey == guestKey);
        if (guest is null)
        {
            logger.LogWarning("No published Coffee guest found for {GuestKey}", guestKey);
            return request.CreateResponse(HttpStatusCode.NotFound);
        }
        await PublishAsync(CoffeePostFactory.Published(guest));
        var response = request.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync("Complete");
        return response;
    }

    private Task COSSTeaserAsync() => COSSUpcomingAsync(TimeSpan.FromDays(5), CoffeePostFactory.Teaser);
    private Task COSSAnnouncementAsync() => COSSUpcomingAsync(TimeSpan.FromHours(3), CoffeePostFactory.Announcement);

    private async Task COSSUpcomingAsync(TimeSpan window, Func<Guest, SocialPost> createPost)
    {
        if (!PostingAllowed)
        {
            return;
        }
        var now = clock.GetUtcNow().UtcDateTime;
        var guest = (await feed.GetGuestsAsync())
            .Where(guest => guest.DateTimeUTC > now && guest.DateTimeUTC < now + window)
            .OrderBy(guest => guest.DateTimeUTC).FirstOrDefault();
        if (guest is not null)
        {
            await PublishAsync(createPost(guest));
        }
        else
        {
            logger.LogInformation("No upcoming Coffee guest within {Window}", window);
        }
    }

    private async Task COSSArchiveAsync()
    {
        if (!PostingAllowed)
        {
            return;
        }
        var guests = (await feed.GetGuestsAsync()).Where(guest => guest.IsPublished).ToList();
        if (guests.Count == 0)
        {
            logger.LogInformation("No published Coffee guests available for an archive post.");
            return;
        }
        await PublishAsync(CoffeePostFactory.Archive(guests[Random.Shared.Next(guests.Count)]));
    }

    private Task PublishAsync(SocialPost post)
    {
        logger.LogInformation("Publishing Coffee post for {GuestKey}", post.Key);
        return publisher.PublishAsync(post);
    }
}
