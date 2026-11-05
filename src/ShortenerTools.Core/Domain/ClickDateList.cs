namespace ShortenerTools.Core.Domain;

/// <summary>Contains daily click counts and the optional short URL they describe.</summary>
public class ClickDateList
{
    /// <summary>Gets or sets the daily counts, ordered by ascending date.</summary>
    public List<ClickDate> Items { get; set; } = new();

    /// <summary>Gets or sets the short URL, or an empty string for all URLs.</summary>
    public string Url { get; set; } = string.Empty;
}
