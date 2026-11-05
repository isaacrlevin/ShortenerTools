using Newtonsoft.Json;

namespace ShortenerTools.Core.Domain.Coffee;

public sealed class Guest
{
    public string PartitionKey { get; set; } = string.Empty;
    public int RowKey { get; set; }
    public string DateTimeAsString { get; set; } = string.Empty;
    public DateTime DateTimeUTC { get; set; }
    public string GuestName { get; set; } = string.Empty;
    public string GuestHandle { get; set; } = string.Empty;
    public string GuestLink { get; set; } = string.Empty;
    public bool IsPublished { get; set; }
    public string Topic { get; set; } = string.Empty;
    public string YouTubeVideoId { get; set; } = string.Empty;
    public string GuestBio { get; set; } = string.Empty;
    public bool HaveAudio { get; set; }
    public string SpotifyLink { get; set; } = string.Empty;
    public string GPLink { get; set; } = string.Empty;
    public string APLink { get; set; } = string.Empty;

    [JsonConverter(typeof(SocialsConverter))]
    public Dictionary<string, string> Socials { get; set; } = new();
    public List<Social> SocialsConverted { get; set; } = new();
}
