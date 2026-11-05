using ShortenerTools.Core.Domain;
using ShortenerTools.Core.Messages;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;

namespace ShortenerTools.Tests;

[TestClass]
public class ExistingBehaviorTests
{
    [TestMethod]
    public void LegacyStatsRequestStillDeserializesWithoutNewFields()
    {
        var request = JsonSerializer.Deserialize<UrlClickStatsRequest>(
            """{"vanity":"existing"}""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.AreEqual("existing", request.Vanity);
        Assert.IsNull(request.StartDate);
        Assert.IsNull(request.EndDate);
        Assert.IsNull(request.TimeZoneId);
    }

    [TestMethod]
    public void SocialQueueMetadataStillRoundTrips()
    {
        var entity = new ShortUrlEntity("https://destination.example", "link", "Title", "Message", true);
        var copy = JsonSerializer.Deserialize<ShortUrlEntity>(JsonSerializer.Serialize(entity))!;
        Assert.AreEqual("Title", copy.Title);
        Assert.AreEqual("Message", copy.Message);
        Assert.IsFalse(copy.Posted);
        Assert.IsFalse(copy.IsArchived);
        Assert.AreEqual("l", copy.PartitionKey);
        Assert.AreEqual("link", copy.RowKey);
    }

    [TestMethod]
    public void PagingFilteringAndSortingRetainTotalCountAndSocialFields()
    {
        var items = Enumerable.Range(1, 5).Select(index =>
            new ShortUrlEntity($"https://destination.example/{index}", $"link{index}",
                $"Example {index}", $"Message {index}", true)).ToList();
        var query = new UrlListQuery
        {
            Skip = 1, Take = 2,
            Filters = new() { new UrlListFilter { Column = "Title", Operator = "contains", Value = "EXAMPLE" } },
            Sorts = new() { new UrlListSort { Column = "RowKey", Descending = true } }
        };
        var result = query.Apply(items);
        Assert.AreEqual(5, result.TotalCount);
        Assert.AreEqual(2, result.UrlList.Count);
        Assert.AreEqual("link4", result.UrlList[0].RowKey);
        Assert.AreEqual("link3", result.UrlList[1].RowKey);
        Assert.AreEqual("Message 4", result.UrlList[0].Message);
        Assert.IsFalse(result.UrlList[0].Posted);
    }
}
