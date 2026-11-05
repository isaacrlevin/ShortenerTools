using Azure;
using Azure.Data.Tables;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using ShortenerTools.Core.Domain;

namespace ShortenerTools.Tests;

[TestClass]
public class ShortcodeCreationTests
{
    [TestMethod]
    [DataRow("my-event")]
    [DataRow("My_Event123")]
    [DataRow("")]
    [DataRow(null)]
    public void AcceptsCustomCodesOrAutomaticGeneration(string? code) =>
        Assert.IsNull(ShortcodePolicy.GetError(code));

    [TestMethod]
    [DataRow("KEY")]
    [DataRow("key")]
    [DataRow("API")]
    [DataRow("health")]
    [DataRow("my/event")]
    [DataRow("my?event")]
    [DataRow("my#event")]
    [DataRow("my event")]
    public void RejectsReservedOrInvalidNewCodes(string code) =>
        Assert.IsNotNull(ShortcodePolicy.GetError(code));

    [TestMethod]
    public void RejectsOverlongCode() => Assert.IsNotNull(ShortcodePolicy.GetError(new string('a', 129)));

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CreationNeverOverwritesExistingMetadata(bool collision)
    {
        var entity = new ShortUrlEntity("https://example.com", "my-event", "Title", "Message", false);
        var table = new Mock<TableClient>();
        var service = new Mock<TableServiceClient>();
        service.Setup(x => x.GetTableClient("UrlsDetails")).Returns(table.Object);
        if (collision)
            table.Setup(x => x.AddEntityAsync(entity, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new RequestFailedException(409, "Entity already exists"));
        else
            table.Setup(x => x.AddEntityAsync(entity, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Mock.Of<Response>());

        var created = await new StorageTableHelper(service.Object).TryCreateShortUrlEntity(entity);

        Assert.AreEqual(!collision, created);
        Assert.AreEqual("m", entity.PartitionKey);
        Assert.AreEqual("my-event", entity.RowKey);
        Assert.AreEqual("Title", entity.Title);
        Assert.AreEqual("Message", entity.Message);
        Assert.IsTrue(entity.Posted, "SocialPoster links must not enter the legacy publishing queue.");
        table.Verify(x => x.AddEntityAsync(entity, It.IsAny<CancellationToken>()), Times.Once);
        table.Verify(x => x.UpsertEntityAsync(It.IsAny<ShortUrlEntity>(), It.IsAny<TableUpdateMode>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
