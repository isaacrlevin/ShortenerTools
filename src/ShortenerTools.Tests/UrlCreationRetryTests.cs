using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ShortenerTools.Core.Domain;
using ShortenerTools.Functions;
using System.Net;
using System.Text.Json;

namespace ShortenerTools.Tests;

[TestClass]
public class UrlCreationRetryTests
{
    [TestMethod]
    [DataRow("my-event")]
    [DataRow("")]
    public async Task IdentifiedApiRequestUsesStableKeysAndPreservesMetadata(string vanity)
    {
        var storage = new Mock<StorageTableHelper>();
        // Generated codes keep the sequential base-36 format: 9158 is "hco", so the next id is "hcp".
        var code = string.IsNullOrEmpty(vanity) ? "hcp" : vanity;
        if (string.IsNullOrEmpty(vanity))
        {
            storage.Setup(x => x.GetShortUrlEntityByCreationId("draft-123")).ReturnsAsync((ShortUrlEntity?)null);
            storage.Setup(x => x.GetNextTableId()).ReturnsAsync(9159);
            storage.Setup(x => x.IfShortUrlEntityExistByVanity("hcp")).ReturnsAsync(false);
        }
        storage.Setup(x => x.CreateOrGetShortUrlEntity(It.Is<ShortUrlEntity>(row =>
                row.CreationId == "draft-123" && row.RowKey == code && row.PartitionKey == code.Substring(0, 1)
                && row.Title == "Title" && row.Message == "Message" && row.Posted)))
            .ReturnsAsync(new ShortUrlEntity("https://example.com", code, "Original title", "Original message", false));
        var function = new UrlCreate(NullLoggerFactory.Instance,
            new ShortenerSettings { CustomDomain = "https://go.example" }, storage.Object);
        using var request = new FunctionRequest(JsonSerializer.Serialize(new
        {
            Url = "https://example.com", Vanity = vanity, CreationId = "draft-123",
            Title = "Title", Message = "Message", PostToSocial = false
        }));
        var response = await function.Run(request.Request, null!);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await request.ReadBody();
        StringAssert.Contains(body, "https://go.example/" + code);
        StringAssert.Contains(body, "Original message");
        storage.VerifyAll();
    }

    [TestMethod]
    public async Task RetriedGeneratedCreationReturnsTheFirstLinkWithoutAllocatingAnother()
    {
        var storage = new Mock<StorageTableHelper>(MockBehavior.Strict);
        storage.Setup(x => x.GetShortUrlEntityByCreationId("draft-123")).ReturnsAsync(
            new ShortUrlEntity("https://example.com", "hco", "Title", "Message", false) { CreationId = "draft-123" });
        storage.Setup(x => x.GetLatestPostVersion(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((PostVersionEntity?)null);
        using var request =
            new FunctionRequest("""{"Url":"https://example.com","CreationId":"draft-123"}""");
        var response = await new UrlCreate(NullLoggerFactory.Instance,
                new ShortenerSettings { CustomDomain = "https://go.example" }, storage.Object)
            .Run(request.Request, null!);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(await request.ReadBody(), "https://go.example/hco");
    }

    [TestMethod]
    public async Task OccupiedVanityStopsBeforeReturningAShortLink()
    {
        var storage = new Mock<StorageTableHelper>();
        storage.Setup(x => x.CreateOrGetShortUrlEntity(It.IsAny<ShortUrlEntity>())).ReturnsAsync((ShortUrlEntity?)null);
        using var request =
            new FunctionRequest("""{"Url":"https://example.com","Vanity":"taken","CreationId":"draft-123"}""");
        var response = await new UrlCreate(NullLoggerFactory.Instance, new ShortenerSettings(), storage.Object)
            .Run(request.Request, null!);
        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
    }

    [TestMethod]
    [DataRow(false, false, false, true)]
    [DataRow(true, false, false, false)]
    [DataRow(false, true, false, false)]
    [DataRow(false, false, true, false)]
    public async Task ReplayNeverOverwritesExistingRecords(bool archived, bool differentOwner, bool differentUrl,
        bool reusable)
    {
        var original = new ShortUrlEntity(differentUrl ? "https://other.example" : "https://example.com",
            "vanity", "Original title", "Original message", false)
        {
            CreationId = differentOwner ? "other-draft" : "draft-123", IsArchived = archived
        };
        var table = new Mock<TableClient>();
        var service = new Mock<TableServiceClient>();
        service.Setup(x => x.GetTableClient("UrlsDetails")).Returns(table.Object);
        table.Setup(x => x.AddEntityAsync(It.IsAny<ShortUrlEntity>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(409, "Already exists"));
        var storage = new ReplayStorage(service.Object, original);
        var candidate = new ShortUrlEntity("https://example.com", "vanity", "Changed title", "Changed message", false)
        {
            CreationId = "draft-123"
        };

        var result = await storage.CreateOrGetShortUrlEntity(candidate);

        if (reusable) Assert.AreSame(original, result);
        else Assert.IsNull(result);
        Assert.AreEqual("Original title", original.Title);
        Assert.AreEqual("Original message", original.Message);
        table.Verify(x => x.UpsertEntityAsync(It.IsAny<ShortUrlEntity>(), It.IsAny<TableUpdateMode>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class ReplayStorage(TableServiceClient service, ShortUrlEntity original)
        : StorageTableHelper(service)
    {
        public override Task<ShortUrlEntity?> GetShortUrlEntity(ShortUrlEntity row) =>
            Task.FromResult<ShortUrlEntity?>(original);
    }
}