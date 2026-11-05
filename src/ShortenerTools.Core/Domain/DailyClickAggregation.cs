using System.Globalization;
using ShortenerTools.Core.Messages;

namespace ShortenerTools.Core.Domain;

/// <summary>Aggregates stored click dates without retaining individual telemetry records.</summary>
public sealed class DailyClickAggregation
{
    private readonly DateTime? _start;
    private readonly DateTime? _end;
    private readonly TimeZoneInfo _timeZone;
    private readonly SortedDictionary<DateTime, int> _counts = new();

    /// <summary>Validates date bounds and selects the display time zone.</summary>
    /// <param name="request">The inclusive date range and optional display time zone.</param>
    public DailyClickAggregation(UrlClickStatsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _start = ParseDate(request.StartDate, nameof(request.StartDate));
        _end = ParseDate(request.EndDate, nameof(request.EndDate));
        if (_start > _end)
            throw new ArgumentException("StartDate must not be later than EndDate.");

        try
        {
            _timeZone = string.IsNullOrEmpty(request.TimeZoneId)
                ? TimeZoneInfo.Local
                : TimeZoneInfo.FindSystemTimeZoneById(request.TimeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ArgumentException("TimeZoneId must identify a valid time zone.", nameof(request), ex);
        }
    }

    /// <summary>Returns a click's display date using the existing statistics date conversion.</summary>
    /// <param name="datetime">The timestamp stored in the click's Datetime column.</param>
    /// <param name="timeZone">The display time zone.</param>
    /// <returns>The converted calendar date.</returns>
    public static DateTime GetDisplayDate(string datetime, TimeZoneInfo timeZone)
        => GetDisplayTimestamp(datetime, timeZone).Date;

    public static DateTime GetDisplayTimestamp(string datetime, TimeZoneInfo timeZone)
    {
        // Legacy zone-less timestamps are treated as UTC by ClickStatsEntity.Date.ToLocalTime().
        var utc = DateTime.Parse(datetime, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        return TimeZoneInfo.ConvertTimeFromUtc(utc, timeZone);
    }

    /// <summary>Adds one stored click when it falls within the requested dates.</summary>
    /// <param name="datetime">The timestamp stored in the click's Datetime column.</param>
    public void Add(string datetime)
    {
        var date = GetDisplayDate(datetime, _timeZone);
        if ((_start.HasValue && date < _start.Value) || (_end.HasValue && date > _end.Value))
            return;
        _counts[date] = _counts.GetValueOrDefault(date) + 1;
    }

    /// <summary>Returns whether a stored click falls within the requested dates (always true without dates).</summary>
    /// <param name="datetime">The timestamp stored in the click's Datetime column.</param>
    public bool Includes(string datetime)
    {
        if (!_start.HasValue && !_end.HasValue)
            return true;
        var date = GetDisplayDate(datetime, _timeZone);
        return (!_start.HasValue || date >= _start.Value) && (!_end.HasValue || date <= _end.Value);
    }

    /// <summary>Returns the accumulated counts in chronological order.</summary>
    /// <returns>One entry per date with at least one matching click.</returns>
    public List<ClickDate> GetItems() =>
        _counts.Select(x => new ClickDate { DateClicked = x.Key, Count = x.Value }).ToList();

    private static DateTime? ParseDate(string? value, string name)
    {
        if (value is null)
            return null;
        if (!DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
            throw new ArgumentException($"{name} must use yyyy-MM-dd format.");
        return date;
    }
}
