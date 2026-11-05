using Azure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using ShortenerTools.Core.Domain;
using ShortenerTools.Core.Messages;
using ShortenerTools.Functions.Functions;
using System.Net;
using System.Text.Json;

namespace ShortenerTools.Tests;

[TestClass]
public class UrlCheckTests
{
    [TestMethod]
    public async Task UnusedCodeIsAvailable()
    {
        var storage = new Mock<StorageTableHelper>();
        storage.Setup(x => x.GetShortUrlEntity(It.Is<ShortUrlEntity>(row => row.RowKey == "aca")))
            .ReturnsAsync((ShortUrlEntity?)null);

        var result = await Check(storage.Object, "aca");

        Assert.AreEqual(HttpStatusCode.OK, result.Status);
        Assert.IsTrue(result.Body.Available);
        Assert.AreEqual(string.Empty, result.Body.Message);
        storage.VerifyAll();
    }

    [TestMethod]
    public async Task ActiveLinkMakesTheCodeUnavailable()
    {
        var storage = new Mock<StorageTableHelper>();
        storage.Setup(x => x.GetShortUrlEntity(It.IsAny<ShortUrlEntity>()))
            .ReturnsAsync(new ShortUrlEntity("https://example.com", "aca"));

        var result = await Check(storage.Object, "aca");

        Assert.AreEqual(HttpStatusCode.OK, result.Status);
        Assert.IsFalse(result.Body.Available);
        StringAssert.Contains(result.Body.Message, "already in use");
    }

    [TestMethod]
    public async Task ArchivedLinkStillMakesTheCodeUnavailable()
    {
        // Creation never overwrites an archived code, so reporting it as free would only fail later with a 409.
        var storage = new Mock<StorageTableHelper>();
        storage.Setup(x => x.GetShortUrlEntity(It.IsAny<ShortUrlEntity>())).ReturnsAsync(
            new ShortUrlEntity("https://example.com", "aca") { IsArchived = true });

        var result = await Check(storage.Object, "aca");

        Assert.AreEqual(HttpStatusCode.OK, result.Status);
        Assert.IsFalse(result.Body.Available);
        StringAssert.Contains(result.Body.Message, "archived");
    }

    [TestMethod]
    public async Task SurroundingWhitespaceIsIgnoredAndTheLookupUsesThePartitionOfTheFirstCharacter()
    {
        var storage = new Mock<StorageTableHelper>();
        storage.Setup(x => x.GetShortUrlEntity(
                It.Is<ShortUrlEntity>(row => row.RowKey == "aca" && row.PartitionKey == "a")))
            .ReturnsAsync((ShortUrlEntity?)null);

        var result = await Check(storage.Object, "%20aca%20");

        Assert.AreEqual(HttpStatusCode.OK, result.Status);
        Assert.IsTrue(result.Body.Available);
        storage.VerifyAll();
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("%20%20")]
    [DataRow("has%20space")]
    [DataRow("dot.ted")]
    [DataRow("api")]
    [DataRow("KEY")]
    [DataRow("Health")]
    public async Task MissingOrInvalidCodesReturn400WithoutReadingStorage(string code)
    {
        var storage = new Mock<StorageTableHelper>(MockBehavior.Strict);

        var result = await Check(storage.Object, code);

        Assert.AreEqual(HttpStatusCode.BadRequest, result.Status);
        StringAssert.Contains(result.Raw, "Message");
        storage.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task MissingCodeParameterReturns400()
    {
        var storage = new Mock<StorageTableHelper>(MockBehavior.Strict);
        using var request = new FunctionRequest(url: "https://short.example/api/UrlCheck");

        var response = await new UrlCheck(NullLoggerFactory.Instance, storage.Object).Run(request.Request);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        storage.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task StorageFailureReturns500InsteadOfReportingTheCodeFree()
    {
        var storage = new Mock<StorageTableHelper>();
        storage.Setup(x => x.GetShortUrlEntity(It.IsAny<ShortUrlEntity>()))
            .ThrowsAsync(new RequestFailedException(503, "Unavailable"));

        var result = await Check(storage.Object, "aca");

        Assert.AreEqual(HttpStatusCode.InternalServerError, result.Status);
        StringAssert.Contains(result.Raw, "Unable to check");
    }

    [TestMethod]
    public async Task NullRequestIsRejected()
    {
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() =>
            new UrlCheck(NullLoggerFactory.Instance, new StorageTableHelper()).Run(null!));
    }

    private static async Task<(HttpStatusCode Status, ShortcodeCheckResponse Body, string Raw)> Check(
        StorageTableHelper storage, string encodedCode)
    {
        using var request = new FunctionRequest(url: "https://short.example/api/UrlCheck?code=" + encodedCode);
        var response = await new UrlCheck(NullLoggerFactory.Instance, storage).Run(request.Request);
        var raw = await request.ReadBody();
        var body = JsonSerializer.Deserialize<ShortcodeCheckResponse>(raw,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new ShortcodeCheckResponse();
        return (response.StatusCode, body, raw);
    }
}
