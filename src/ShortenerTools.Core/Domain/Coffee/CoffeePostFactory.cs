using System.Globalization;
using System.Text.RegularExpressions;
using ShortenerTools.Core.Domain.Socials;

namespace ShortenerTools.Core.Domain.Coffee;

public static class CoffeePostFactory
{
    public static SocialPost Teaser(Guest guest)
    {
        var (date, zone) = GetPacificDate(guest);
        var url = "https://www.coffeeandopensource.com/schedule.html";
        return Create(guest,
            "Coming up this week on Coffee & OSS I will be chatting with [HANDLE] about all sorts of #tech and #opensource topics. Streaming live on #Twitch this " +
            $"{date.ToString("dddd MMMM d", CultureInfo.InvariantCulture)}{DaySuffix(date.Day)} at " +
            $"{date.ToString("h:mm tt", CultureInfo.InvariantCulture)} {zone}. Come say hello and join the conversation. \r\n{url}", url);
    }

    public static SocialPost Announcement(Guest guest)
    {
        var (date, zone) = GetPacificDate(guest);
        var url = "https://www.twitch.tv/isaacrlevin";
        return Create(guest,
            "Coming up on Coffee & OSS I will be chatting with [HANDLE] about all sorts of #tech and #opensource topics. Streaming live today on #Twitch at " +
            $"{date.ToString("h:mm tt", CultureInfo.InvariantCulture)} {zone}. Come say hello and join the conversation. \r\n{url}", url);
    }

    public static SocialPost Archive(Guest guest) => Create(guest,
        "From the Coffee & OSS Archives, I chatted with [HANDLE] about all sorts of great #tech and #oss topics. " +
        $"Access the stream or listen to the podcast below. Be sure to like/subscribe. Thanks for tuning in! \r\n{GuestUrl(guest)}",
        GuestUrl(guest));

    public static SocialPost Published(Guest guest) => Create(guest,
        "Had a great time chatting with [HANDLE] on Coffee & OSS today about all kinds of #tech topics. " +
        $"Video is live on YouTube and podcast is available wherever you find them. Take a look/listen and thanks! \r\n{GuestUrl(guest)}",
        GuestUrl(guest));

    private static SocialPost Create(Guest guest, string template, string url)
    {
        var name = Regex.Replace(string.Join(" ", guest.PartitionKey.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant())), @"\d+", "*");
        var text = template.Replace("[HANDLE]", name);
        return new SocialPost(guest.PartitionKey, guest.GuestName, text, url)
        {
            TwitterText = WithHandle(template, guest, "X", name),
            MastodonText = WithHandle(template.Replace("@CoffeeAndOSS", "@CoffeeAndOSS@mastodon.social"),
                guest, "Mastodon", name, useFullValue: true),
            LinkedInText = text.Replace("@CoffeeAndOSS", "Coffee and Open Source"),
            BlueskyText = WithHandle(template.Replace("@CoffeeAndOSS", "@coffeeandopensource.com")
                .Replace("Coffee & OSS", "@coffeeandopensource.com"), guest, "Bluesky", name),
            ThreadsText = WithHandle(template, guest, "Threads", name)
        };
    }

    private static string WithHandle(string template, Guest guest, string platform, string fallback, bool useFullValue = false)
    {
        var handle = fallback;
        if (guest.Socials is not null && guest.Socials.TryGetValue(platform, out var value) &&
            !string.IsNullOrWhiteSpace(value))
        {
            handle = useFullValue ? value : "@" + value.TrimEnd('/').Split('/').Last().TrimStart('@');
        }
        return template.Replace("[HANDLE]", handle);
    }

    private static string GuestUrl(Guest guest) =>
        $"https://www.coffeeandopensource.com/guest/{guest.PartitionKey}.html";

    private static (DateTime Date, string Zone) GetPacificDate(Guest guest)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        var utc = DateTime.SpecifyKind(guest.DateTimeUTC, DateTimeKind.Utc);
        var date = TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
        return (date, zone.IsDaylightSavingTime(date) ? "PDT" : "PST");
    }

    internal static string DaySuffix(int day) => day is >= 11 and <= 13 ? "th" : (day % 10) switch
    {
        1 => "st",
        2 => "nd",
        3 => "rd",
        _ => "th"
    };
}
