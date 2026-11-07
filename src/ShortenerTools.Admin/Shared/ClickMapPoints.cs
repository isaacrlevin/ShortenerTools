using System.Globalization;
using ShortenerTools.Core.Domain;

namespace ShortenerTools.Admin.Shared;

/// <summary>One circle on the click map.</summary>
/// <param name="Latitude">Circle centre latitude.</param>
/// <param name="Longitude">Circle centre longitude.</param>
/// <param name="Label">Place shown in the tooltip, e.g. "Seattle, Washington, US".</param>
/// <param name="Clicks">Number of clicks the circle represents.</param>
/// <param name="CountryOnly">True when the clicks have only a country and are drawn at its centre.</param>
public sealed record ClickMapPoint(double Latitude, double Longitude, string Label, int Clicks, bool CountryOnly);

/// <summary>Groups clicks into map circles and formats click locations for display.</summary>
public static class ClickMapPoints
{
    /// <summary>
    /// Clicks with coordinates are grouped by position (stored rounded to about 11 km); clicks with only a country
    /// are grouped at the country's centre. Clicks with neither, or with an unknown country code, are left out.
    /// </summary>
    public static IReadOnlyList<ClickMapPoint> Build(IEnumerable<ClickStatsEntity> clicks)
    {
        var located = new Dictionary<(double, double), List<ClickStatsEntity>>();
        var countryOnly = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var click in clicks)
        {
            if (click.Latitude is { } latitude && click.Longitude is { } longitude)
            {
                var key = (Math.Round(latitude, 1), Math.Round(longitude, 1));
                if (!located.TryGetValue(key, out var group))
                    located[key] = group = new List<ClickStatsEntity>();
                group.Add(click);
            }
            else if (!string.IsNullOrEmpty(click.Country) && CountryCentroids.ByCode.ContainsKey(click.Country))
            {
                countryOnly[click.Country] = countryOnly.GetValueOrDefault(click.Country) + 1;
            }
        }

        var points = located.Select(entry => new ClickMapPoint(entry.Key.Item1, entry.Key.Item2,
                // One position can hold several spellings (or a missing city); label it with the most common one.
                entry.Value.GroupBy(CityLabel).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                    .First().Key,
                entry.Value.Count, CountryOnly: false))
            .Concat(countryOnly.Select(entry =>
            {
                var centre = CountryCentroids.ByCode[entry.Key];
                return new ClickMapPoint(centre.Latitude, centre.Longitude,
                    $"{CountryName(entry.Key)} (country only)", entry.Value, CountryOnly: true);
            }));

        // Largest first, so the map can draw small circles on top of big ones.
        return points.OrderByDescending(p => p.Clicks).ThenBy(p => p.Label, StringComparer.Ordinal).ToList();
    }

    /// <summary>Whether a click can be placed on the map.</summary>
    public static bool IsMappable(ClickStatsEntity click) =>
        (click.Latitude.HasValue && click.Longitude.HasValue) ||
        (!string.IsNullOrEmpty(click.Country) && CountryCentroids.ByCode.ContainsKey(click.Country));

    /// <summary>English country name for an ISO code, or the code itself when .NET doesn't know it.</summary>
    public static string CountryName(string code)
    {
        try
        {
            return new RegionInfo(code).EnglishName;
        }
        catch (ArgumentException)
        {
            return code;
        }
    }

    /// <summary>"City, Region, CC", skipping missing parts.</summary>
    public static string CityLabel(ClickStatsEntity click) =>
        string.Join(", ", new[] { click.City ?? "Unknown city", click.Region, click.Country }
            .Where(part => !string.IsNullOrEmpty(part)));
}
