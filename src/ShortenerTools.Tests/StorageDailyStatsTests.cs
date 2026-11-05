using Azure;
using Azure.Data.Tables;
using ShortenerTools.Core.Domain;
using ShortenerTools.Core.Messages;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace ShortenerTools.Tests;

[TestClass]
public class StorageDailyStatsTests
{
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("a'b")]
    public async Task QueriesOnlyDateColumnAndEscapesVanityAcrossPages(string? vanity)
    {
        var table = new Mock<TableClient>();
        var service = new Mock<TableServiceClient>();
        service.Setup(x => x.GetTableClient("ClickStats")).Returns(table.Object);
        var pages = new[]
        {
            Page<TableEntity>.FromValues(new[] { Click("2026-10-01 12:00") }, "next", Mock.Of<Response>()),
            Page<TableEntity>.FromValues(new[] { Click("2026-10-01 13:00"), Click("2026-10-02 00:00") },
                null, Mock.Of<Response>())
        };
        var expectedFilter = string.IsNullOrEmpty(vanity)
            ? null : TableClient.CreateQueryFilter($"PartitionKey eq {vanity}");
        table.Setup(x => x.QueryAsync<TableEntity>(expectedFilter, null,
                It.Is<IEnumerable<string>>(columns => columns.SequenceEqual(new[] { "Datetime" })),
                It.IsAny<CancellationToken>()))
            .Returns(AsyncPageable<TableEntity>.FromPages(pages));

        var result = await new StorageTableHelper(service.Object).GetDailyStats(
            new UrlClickStatsRequest(vanity) { TimeZoneId = "UTC" });

        Assert.AreEqual(2, result.Items.Count);
        Assert.AreEqual(2, result.Items[0].Count);
        Assert.AreEqual(1, result.Items[1].Count);
        Assert.AreEqual(string.Empty, result.Url);
        table.VerifyAll();
    }

    [TestMethod]
    public async Task ExcludeCrawlersSkipsFlaggedRowsButKeepsRowsWithoutTheColumn()
    {
        var table = new Mock<TableClient>();
        var service = new Mock<TableServiceClient>();
        service.Setup(x => x.GetTableClient("ClickStats")).Returns(table.Object);
        var page = Page<TableEntity>.FromValues(new[]
        {
            Click("2026-10-01 12:00"),                                            // older row, no IsCrawler column
            new TableEntity { ["Datetime"] = "2026-10-01 13:00", ["IsCrawler"] = false },
            new TableEntity { ["Datetime"] = "2026-10-01 14:00", ["IsCrawler"] = true }
        }, null, Mock.Of<Response>());
        table.Setup(x => x.QueryAsync<TableEntity>((string?)null, null,
                It.Is<IEnumerable<string>>(columns => columns.SequenceEqual(new[] { "Datetime", "IsCrawler" })),
                It.IsAny<CancellationToken>()))
            .Returns(AsyncPageable<TableEntity>.FromPages(new[] { page }));

        var result = await new StorageTableHelper(service.Object).GetDailyStats(
            new UrlClickStatsRequest(null) { TimeZoneId = "UTC", ExcludeCrawlers = true });

        Assert.AreEqual(2, result.Items.Single().Count);
        table.VerifyAll();
    }

    [TestMethod]
    public void UserAgentParserCrawlersAreFlaggedAsCrawlersNotMobile()
    {
        var click = new ClickStatsEntity(new AnalyticsEntry
        {
            Agent = "Googlebot/2.1 (+http://www.google.com/bot.html)",
            LongUrl = new Uri("https://destination.example/page"),
            ShortUrl = "link",
            TimeStamp = DateTime.UtcNow
        }, "destination.example/page");

        Assert.IsTrue(click.IsCrawler);
        Assert.IsFalse(click.IsMobile);
    }

    // Click RowKeys are UTC ticks; the query bounds are the range widened by a two-day margin for time zones.
    private static string Ticks(int year, int month, int day) =>
        new DateTime(year, month, day).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);

    [TestMethod]
    public void StatsFilterUsesAnIndexedRowKeyRangeAndOnlyWithAVanity()
    {
        // No partition key means no index to range over, so dates cannot narrow a whole-table query.
        Assert.IsNull(StorageTableHelper.BuildStatsFilter(new UrlClickStatsRequest(null)));
        Assert.IsNull(StorageTableHelper.BuildStatsFilter(
            new UrlClickStatsRequest(null) { StartDate = "2026-10-01", EndDate = "2026-10-31" }));

        var partition = TableClient.CreateQueryFilter($"PartitionKey eq {"aca"}");
        Assert.AreEqual(partition, StorageTableHelper.BuildStatsFilter(new UrlClickStatsRequest("aca")));
        Assert.AreEqual(
            partition + " and " + TableClient.CreateQueryFilter($"RowKey lt {Ticks(2026, 11, 3)}"),
            StorageTableHelper.BuildStatsFilter(new UrlClickStatsRequest("aca") { EndDate = "2026-10-31" }));

        var filter = StorageTableHelper.BuildStatsFilter(
            new UrlClickStatsRequest("aca") { StartDate = "2026-10-01", EndDate = "2026-10-31" })!;
        Assert.AreEqual(partition
            + " and " + TableClient.CreateQueryFilter($"RowKey ge {Ticks(2026, 9, 29)}")
            + " and " + TableClient.CreateQueryFilter($"RowKey lt {Ticks(2026, 11, 3)}"), filter);
        StringAssert.DoesNotMatch(filter, new System.Text.RegularExpressions.Regex("Timestamp"));
    }

    [TestMethod]
    public void StatsFilterDropsBoundsThatWouldNotSortAgainstEighteenDigitRowKeys()
    {
        var filter = StorageTableHelper.BuildStatsFilter(
            new UrlClickStatsRequest("aca") { StartDate = "0001-01-01", EndDate = "9999-12-31" });
        Assert.AreEqual(TableClient.CreateQueryFilter($"PartitionKey eq {"aca"}"), filter);
    }

    [TestMethod]
    public async Task GetStatsForOneVanityReadsOnlyThatPartitionsRowKeyRange()
    {
        var table = new Mock<TableClient>();
        var service = new Mock<TableServiceClient>();
        service.Setup(x => x.GetTableClient("ClickStats")).Returns(table.Object);
        var page = Page<ClickStatsEntity>.FromValues(new[]
        {
            new ClickStatsEntity { Datetime = "2026-09-30 23:00" },   // inside the query margin, before the range
            new ClickStatsEntity { Datetime = "2026-10-01 08:00" },
            new ClickStatsEntity { Datetime = "2026-10-15 09:00" },
            new ClickStatsEntity { Datetime = "2026-11-01 00:30" }    // after the range
        }, null, Mock.Of<Response>());
        var request = new UrlClickStatsRequest("aca")
        {
            StartDate = "2026-10-01", EndDate = "2026-10-31", TimeZoneId = "UTC"
        };
        table.Setup(x => x.QueryAsync<ClickStatsEntity>(StorageTableHelper.BuildStatsFilter(request), null, null,
                It.IsAny<CancellationToken>()))
            .Returns(AsyncPageable<ClickStatsEntity>.FromPages(new[] { page }));

        var result = await new StorageTableHelper(service.Object).GetStats(request);

        CollectionAssert.AreEqual(new[] { "2026-10-15 09:00", "2026-10-01 08:00" },
            result.Select(click => click.Datetime).ToArray());
        table.VerifyAll();
        service.Verify(x => x.GetTableClient("UrlsDetails"), Times.Never);
    }

    [TestMethod]
    public async Task GetStatsForAllLinksWithDatesQueriesEachLinkPartitionByKeyRange()
    {
        var (service, table) = ClickServiceWithLinks("a", "b", "c");
        var request = new UrlClickStatsRequest(null)
        {
            StartDate = "2026-10-01", EndDate = "2026-10-31", TimeZoneId = "UTC"
        };
        SetupPartition(table, request, "a", new ClickStatsEntity { Datetime = "2026-10-02 10:00" });
        SetupPartition(table, request, "b",
            new ClickStatsEntity { Datetime = "2026-10-20 10:00" },
            new ClickStatsEntity { Datetime = "2026-09-30 23:00" });   // inside the margin, outside the range
        SetupPartition(table, request, "c");                           // a link with no clicks in range

        var result = await new StorageTableHelper(service.Object).GetStats(request);

        CollectionAssert.AreEqual(new[] { "2026-10-20 10:00", "2026-10-02 10:00" },
            result.Select(click => click.Datetime).ToArray());
        table.VerifyAll();
        // Never a Timestamp filter or an unfiltered whole-table read.
        table.Verify(x => x.QueryAsync<ClickStatsEntity>(It.Is<string?>(f => f == null || f.Contains("Timestamp")),
            It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task GetDailyStatsForAllLinksWithDatesAlsoFansOutAndCountsPerDay()
    {
        var (service, table) = ClickServiceWithLinks("a", "b");
        var request = new UrlClickStatsRequest(null)
        {
            StartDate = "2026-10-01", EndDate = "2026-10-31", TimeZoneId = "UTC"
        };
        foreach (var (vanity, dates) in new[] { ("a", new[] { "2026-10-02 10:00", "2026-10-02 11:00" }),
                     ("b", new[] { "2026-10-02 12:00", "2026-10-03 00:00" }) })
        {
            table.Setup(x => x.QueryAsync<TableEntity>(StorageTableHelper.BuildStatsFilter(request, vanity), null,
                    It.Is<IEnumerable<string>>(columns => columns.SequenceEqual(new[] { "Datetime" })),
                    It.IsAny<CancellationToken>()))
                .Returns(AsyncPageable<TableEntity>.FromPages(new[]
                {
                    Page<TableEntity>.FromValues(dates.Select(Click).ToArray(), null, Mock.Of<Response>())
                }));
        }

        var result = await new StorageTableHelper(service.Object).GetDailyStats(request);

        CollectionAssert.AreEqual(new[] { 3, 1 }, result.Items.Select(item => item.Count).ToArray());
        table.VerifyAll();
    }

    [TestMethod]
    public async Task GetStatsPropagatesAFailedPartitionQuery()
    {
        var (service, table) = ClickServiceWithLinks("a", "b");
        var request = new UrlClickStatsRequest(null) { StartDate = "2026-10-01", TimeZoneId = "UTC" };
        SetupPartition(table, request, "a", new ClickStatsEntity { Datetime = "2026-10-02 10:00" });
        table.Setup(x => x.QueryAsync<ClickStatsEntity>(StorageTableHelper.BuildStatsFilter(request, "b"), null, null,
                It.IsAny<CancellationToken>()))
            .Throws(new RequestFailedException("throttled"));

        await Assert.ThrowsExactlyAsync<RequestFailedException>(
            () => new StorageTableHelper(service.Object).GetStats(request));
    }

    private static (Mock<TableServiceClient> Service, Mock<TableClient> Clicks) ClickServiceWithLinks(
        params string[] vanities)
    {
        var clicks = new Mock<TableClient>();
        var links = new Mock<TableClient>();
        var service = new Mock<TableServiceClient>();
        service.Setup(x => x.GetTableClient("ClickStats")).Returns(clicks.Object);
        service.Setup(x => x.GetTableClient("UrlsDetails")).Returns(links.Object);
        links.Setup(x => x.QueryAsync<TableEntity>(TableClient.CreateQueryFilter($"RowKey ne {"KEY"}"), null,
                It.Is<IEnumerable<string>>(columns => columns.SequenceEqual(new[] { "RowKey" })),
                It.IsAny<CancellationToken>()))
            .Returns(AsyncPageable<TableEntity>.FromPages(new[]
            {
                Page<TableEntity>.FromValues(
                    vanities.Select(v => new TableEntity("p", v)).ToArray(), null, Mock.Of<Response>())
            }));
        return (service, clicks);
    }

    private static void SetupPartition(Mock<TableClient> clicks, UrlClickStatsRequest request, string vanity,
        params ClickStatsEntity[] rows) =>
        clicks.Setup(x => x.QueryAsync<ClickStatsEntity>(StorageTableHelper.BuildStatsFilter(request, vanity), null,
                null, It.IsAny<CancellationToken>()))
            .Returns(AsyncPageable<ClickStatsEntity>.FromPages(new[]
            {
                Page<ClickStatsEntity>.FromValues(rows, null, Mock.Of<Response>())
            }));

    [TestMethod]
    public async Task GetStatsRejectsInvalidDatesBeforeQuerying()
    {
        var table = new Mock<TableClient>(MockBehavior.Strict);
        var service = new Mock<TableServiceClient>();
        service.Setup(x => x.GetTableClient("ClickStats")).Returns(table.Object);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => new StorageTableHelper(service.Object)
            .GetStats(new UrlClickStatsRequest(null) { StartDate = "10/01/2026" }));
    }

    private static TableEntity Click(string date) => new() { ["Datetime"] = date };
}
