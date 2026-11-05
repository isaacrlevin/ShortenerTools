using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ShortenerTools.Core.Domain.Coffee;

public interface ICoffeeGuestFeed
{
    Task<IReadOnlyList<Guest>> GetGuestsAsync();
}

public sealed class CoffeeGuestFeed(HttpClient client) : ICoffeeGuestFeed
{
    public const string FeedUrl =
        "https://raw.githubusercontent.com/isaacrlevin/CoffeeAndOpenSource.com/main/data/guests.json";

    public async Task<IReadOnlyList<Guest>> GetGuestsAsync() =>
        Parse(await client.GetStringAsync(FeedUrl));

    internal static IReadOnlyList<Guest> Parse(string json)
    {
        var document = JObject.Parse(json);
        return document.Properties().Select(property =>
        {
            var guest = property.Value.ToObject<Guest>()
                ?? throw new JsonSerializationException($"Missing guest data for {property.Name}.");
            if (string.IsNullOrWhiteSpace(guest.PartitionKey))
            {
                throw new JsonSerializationException($"Missing guest partition key for {property.Name}.");
            }
            return guest;
        }).ToList();
    }
}
