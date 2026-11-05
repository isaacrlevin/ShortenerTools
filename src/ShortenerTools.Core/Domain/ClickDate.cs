namespace ShortenerTools.Core.Domain;

/// <summary>Represents the number of clicks on a calendar date.</summary>
public class ClickDate
{
    /// <summary>Gets or sets the date in the requested display time zone.</summary>
    public DateTime DateClicked { get; set; }

    /// <summary>Gets or sets the number of clicks on that date.</summary>
    public int Count { get; set; }
}
