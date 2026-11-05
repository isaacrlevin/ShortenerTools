using Azure;
using ShortenerTools.Core.Domain;
using ShortenerTools.Core.Messages;
using ShortenerTools.Functions.Functions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System.Net;
using System.Text.Json;

namespace ShortenerTools.Tests;

[TestClass]
public class UrlClickStatsByDayTests
{
    [TestMethod]
    [DataRow(null, "https://short.example/link")]
    [DataRow("https://custom.example/", "https://custom.example/link")]
    public async Task ReturnsUpstreamShapeAndForwardsOptionalFilters(string? domain, string expectedUrl)
    {
        var storage = new Mock<StorageTableHelper>();
        storage.Setup(x => x.GetDailyStats(It.Is<UrlClickStatsRequest>(input =>
                input.Vanity == "link" && input.StartDate == "2026-10-01" &&
                input.EndDate == "2026-10-03" && input.TimeZoneId == "UTC")))
            .ReturnsAsync(new ClickDateList
            {
                Items = new() { new ClickDate { DateClicked = new DateTime(2026, 10, 1), Count = 2 } }
            });
        using var request = new FunctionRequest(
            """{"vanity":"link","startDate":"2026-10-01","endDate":"2026-10-03","timeZoneId":"UTC"}""");
        var function = CreateFunction(storage.Object, domain);
        var response = await function.Run(request.Request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var result = JsonSerializer.Deserialize<ClickDateList>(await request.ReadBody(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.AreEqual(expectedUrl, result.Url);
        Assert.AreEqual(2, result.Items.Single().Count);
        Assert.AreEqual(new DateTime(2026, 10, 1), result.Items.Single().DateClicked);
        storage.VerifyAll();
    }

    [TestMethod]
    public async Task AllLinksAndEmptyResultsAreSupported()
    {
        var storage = new Mock<StorageTableHelper>();
        storage.Setup(x => x.GetDailyStats(It.Is<UrlClickStatsRequest>(x => x.Vanity == null)))
            .ReturnsAsync(new ClickDateList());
        using var request = new FunctionRequest();
        var response = await CreateFunction(storage.Object).Run(request.Request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var result = JsonSerializer.Deserialize<ClickDateList>(await request.ReadBody(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.AreEqual(string.Empty, result.Url);
        Assert.AreEqual(0, result.Items.Count);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("null")]
    [DataRow("{")]
    [DataRow("""{"StartDate":"invalid"}""")]
    [DataRow("""{"StartDate":"2026-10-03","EndDate":"2026-10-01"}""")]
    [DataRow("""{"TimeZoneId":"invalid"}""")]
    public async Task InvalidRequestsReturn400WithoutAccessingStorage(string body)
    {
        var storage = new Mock<StorageTableHelper>(MockBehavior.Strict);
        using var request = new FunctionRequest(body);
        var response = await CreateFunction(storage.Object).Run(request.Request);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        StringAssert.Contains(await request.ReadBody(), "essage");
        storage.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task StorageFailureReturns500InsteadOfEmptySuccess()
    {
        var storage = new Mock<StorageTableHelper>();
        storage.Setup(x => x.GetDailyStats(It.IsAny<UrlClickStatsRequest>()))
            .ThrowsAsync(new RequestFailedException(503, "Unavailable"));
        using var request = new FunctionRequest();
        var response = await CreateFunction(storage.Object).Run(request.Request);
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        StringAssert.Contains(await request.ReadBody(), "Unable to load daily click statistics.");
    }

    [TestMethod]
    public async Task MalformedStoredDatesReturn500InsteadOfEmptySuccess()
    {
        var storage = new Mock<StorageTableHelper>();
        storage.Setup(x => x.GetDailyStats(It.IsAny<UrlClickStatsRequest>()))
            .ThrowsAsync(new FormatException("Malformed stored date"));
        using var request = new FunctionRequest();
        var response = await CreateFunction(storage.Object).Run(request.Request);
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [TestMethod]
    public async Task NullRequestIsRejected()
    {
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() =>
            CreateFunction(new StorageTableHelper()).Run(null!));
    }

    private static UrlClickStatsByDay CreateFunction(StorageTableHelper storage, string? domain = null) =>
        new(NullLoggerFactory.Instance, new ShortenerSettings { CustomDomain = domain! }, storage);
}
