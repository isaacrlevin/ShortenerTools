using ShortenerTools.Core.Domain;
using ShortenerTools.Core.Messages;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ShortenerTools.Tests;

[TestClass]
public class DailyClickAggregationTests
{
    [TestMethod]
    [DataRow("2026-10-06 00:30")]
    [DataRow("2026-10-06T00:30:00Z")]
    public void ServerHostedDetailsMatchBrowserCalendarDates(string timestamp)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        var display = DailyClickAggregation.GetDisplayTimestamp(timestamp, zone);
        Assert.AreEqual(new DateTime(2026, 10, 5, 17, 30, 0), display);
        Assert.AreEqual(display.Date, DailyClickAggregation.GetDisplayDate(timestamp, zone));
    }

    [TestMethod]
    public void CountsAreOrderedAndBoundsAreInclusive()
    {
        var aggregation = new DailyClickAggregation(new UrlClickStatsRequest(null)
        {
            StartDate = "2026-10-01", EndDate = "2026-10-03", TimeZoneId = "UTC"
        });
        foreach (var date in new[] { "2026-10-03 23:59", "2026-10-01 00:00", "2026-10-01 12:00",
                     "2026-09-30 23:59", "2026-10-04 00:00" })
            aggregation.Add(date);

        var items = aggregation.GetItems();
        Assert.AreEqual(2, items.Count);
        Assert.AreEqual(new DateTime(2026, 10, 1), items[0].DateClicked);
        Assert.AreEqual(2, items[0].Count);
        Assert.AreEqual(new DateTime(2026, 10, 3), items[1].DateClicked);
        Assert.AreEqual(1, items[1].Count);
    }

    [TestMethod]
    public void EmptyAndOpenEndedRangesAreSupported()
    {
        var aggregation = new DailyClickAggregation(new UrlClickStatsRequest(null)
        {
            StartDate = "2026-10-01", TimeZoneId = "UTC"
        });
        Assert.AreEqual(0, aggregation.GetItems().Count);
        aggregation.Add("2026-09-30 23:59");
        aggregation.Add("2027-01-01 00:00");
        Assert.AreEqual(1, aggregation.GetItems().Count);
    }

    [TestMethod]
    public void EndOnlyRangeIsSupported()
    {
        var aggregation = new DailyClickAggregation(new UrlClickStatsRequest(null)
        {
            EndDate = "2026-10-01", TimeZoneId = "UTC"
        });
        aggregation.Add("2026-10-01 23:59");
        aggregation.Add("2026-10-02 00:00");
        Assert.AreEqual(1, aggregation.GetItems().Count);
    }

    [TestMethod]
    public void DefaultZoneMatchesExistingRawStatisticsDate()
    {
        var raw = new ClickStatsEntity { Datetime = "2026-10-06 00:30" };
        var aggregation = new DailyClickAggregation(new UrlClickStatsRequest(null));
        aggregation.Add(raw.Datetime);
        Assert.AreEqual(raw.Date.Date, aggregation.GetItems()[0].DateClicked);
    }

    [TestMethod]
    public void BrowserTimeZonePreservesMidnightAndDaylightSavingBoundaries()
    {
        var aggregation = new DailyClickAggregation(new UrlClickStatsRequest("vanity")
        {
            TimeZoneId = "America/Los_Angeles"
        });
        aggregation.Add("2026-10-06 00:30");
        aggregation.Add("2026-11-01 08:30");
        aggregation.Add("2026-11-01 09:30");
        var items = aggregation.GetItems();
        Assert.AreEqual(new DateTime(2026, 10, 5), items[0].DateClicked);
        Assert.AreEqual(new DateTime(2026, 11, 1), items[1].DateClicked);
        Assert.AreEqual(2, items[1].Count);
    }

    [TestMethod]
    [DataRow("invalid", null)]
    [DataRow("2026-02-30", null)]
    [DataRow("10/01/2026", null)]
    [DataRow("", null)]
    [DataRow(null, "invalid")]
    [DataRow("2026-10-02", "2026-10-01")]
    public void InvalidDatesAreRejected(string? start, string? end)
    {
        Assert.ThrowsExactly<ArgumentException>(() => new DailyClickAggregation(
            new UrlClickStatsRequest(null) { StartDate = start, EndDate = end }));
    }

    [TestMethod]
    public void InvalidTimeZoneAndNullInputAreRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new DailyClickAggregation(
            new UrlClickStatsRequest(null) { TimeZoneId = "not-a-time-zone" }));
        Assert.ThrowsExactly<ArgumentNullException>(() => new DailyClickAggregation(null!));
    }

    [TestMethod]
    public void MalformedStoredDatesAreNotSilentlyDropped()
    {
        var aggregation = new DailyClickAggregation(new UrlClickStatsRequest(null));
        Assert.ThrowsExactly<FormatException>(() => aggregation.Add("invalid"));
    }

    [TestMethod]
    public void ExistingTimestampFormatsRetainRawDateSemantics()
    {
        foreach (var date in new[] { "2026-10-06 00:30", "2026-10-06T00:30:00Z",
                     "2026-10-06T00:30:00+02:00" })
        {
            var raw = new ClickStatsEntity { Datetime = date };
            Assert.AreEqual(raw.Date.Date, DailyClickAggregation.GetDisplayDate(date, TimeZoneInfo.Local));
        }
    }
}
