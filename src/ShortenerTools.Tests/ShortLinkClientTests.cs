using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShortenerTools.Admin;

namespace ShortenerTools.Tests;

[TestClass]
public class ShortLinkClientTests
{
    [TestMethod]
    public async Task FreeCodeIsAvailableAndTheCodeIsEscapedInTheQuery()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"Available":true,"Message":""}""");

        var check = await Client(handler).CheckAsync("a b&c");

        Assert.AreEqual(ShortcodeStatus.Available, check.Status);
        Assert.AreEqual(HttpMethod.Get, handler.Method);
        Assert.AreEqual("/api/UrlCheck?code=a%20b%26c", handler.PathAndQuery);
    }

    [TestMethod]
    public async Task TakenCodeIsInUseWithTheServerReason()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"Available":false,"Message":"This shortcode belongs to an archived link and cannot be reused."}""");

        var check = await Client(handler).CheckAsync("aca");

        Assert.AreEqual(ShortcodeStatus.InUse, check.Status);
        StringAssert.Contains(check.Message, "archived");
    }

    [TestMethod]
    public async Task TakenCodeWithoutAMessageStillExplainsItself()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"Available":false}""");

        var check = await Client(handler).CheckAsync("aca");

        Assert.AreEqual(ShortcodeStatus.InUse, check.Status);
        StringAssert.Contains(check.Message, "already in use");
    }

    [TestMethod]
    public async Task RejectedCodeIsInvalidWithTheServerReason()
    {
        var handler = new StubHandler(HttpStatusCode.BadRequest, """{"Message":"This shortcode is reserved."}""");

        var check = await Client(handler).CheckAsync("api");

        Assert.AreEqual(ShortcodeStatus.Invalid, check.Status);
        Assert.AreEqual("This shortcode is reserved.", check.Message);
    }

    [TestMethod]
    public async Task ServerErrorDuringACheckSurfacesAsAnExceptionNotAFalseAvailable()
    {
        var handler = new StubHandler(HttpStatusCode.InternalServerError, """{"Message":"Unable to check."}""");

        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => Client(handler).CheckAsync("aca"));
    }

    [TestMethod]
    public async Task CreateSendsAnUnqueuedTrimmedRequestWithAStableIdentity()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"ShortUrl":"https://short.example/aca","LongUrl":"https://learn.example.com/page"}""");

        var created = await Client(handler).CreateAsync("  https://learn.example.com/page ", " aca ", "creation-1");

        Assert.IsTrue(created.Succeeded);
        Assert.AreEqual("https://short.example/aca", created.ShortUrl);
        Assert.AreEqual(HttpMethod.Post, handler.Method);
        Assert.AreEqual("/api/UrlCreate", handler.PathAndQuery);
        using var body = JsonDocument.Parse(handler.Body!);
        var root = body.RootElement;
        Assert.AreEqual("https://learn.example.com/page", Property(root, "Url").GetString());
        Assert.AreEqual("aca", Property(root, "Vanity").GetString());
        Assert.AreEqual("creation-1", Property(root, "CreationId").GetString());
        Assert.AreEqual("learn.example.com", Property(root, "Title").GetString());
        Assert.IsFalse(Property(root, "PostToSocial").GetBoolean(), "A generated link must never enter the post queue.");
    }

    [TestMethod]
    public async Task CreateWithoutACustomCodeSendsAnEmptyVanityToGenerateOne()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"ShortUrl":"https://short.example/x1"}""");

        await Client(handler).CreateAsync("https://example.com", null, "creation-2");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.AreEqual(string.Empty, Property(body.RootElement, "Vanity").GetString());
    }

    [TestMethod]
    public async Task ConflictIsReportedDistinctlyFromOtherFailures()
    {
        var handler = new StubHandler(HttpStatusCode.Conflict, """{"Message":"This shortcode already exists."}""");

        var created = await Client(handler).CreateAsync("https://example.com", "aca", "creation-3");

        Assert.IsFalse(created.Succeeded);
        Assert.IsTrue(created.Conflict);
        Assert.AreEqual("This shortcode already exists.", created.Error);
    }

    [TestMethod]
    public async Task BadRequestSurfacesTheServerMessageWithoutBeingAConflict()
    {
        var handler = new StubHandler(HttpStatusCode.BadRequest, """{"Message":"The url parameter can not be empty."}""");

        var created = await Client(handler).CreateAsync("https://example.com", "", "creation-4");

        Assert.IsFalse(created.Conflict);
        Assert.AreEqual("The url parameter can not be empty.", created.Error);
    }

    [TestMethod]
    public async Task NonJsonErrorBodiesFallBackToAGenericMessage()
    {
        var handler = new StubHandler(HttpStatusCode.BadGateway, "<html>bad gateway</html>");

        var created = await Client(handler).CreateAsync("https://example.com", "", "creation-5");

        Assert.IsFalse(created.Succeeded);
        Assert.AreEqual("Unable to create the link.", created.Error);
    }

    [TestMethod]
    public async Task SuccessWithoutAShortUrlIsAnError()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");

        await Assert.ThrowsExactlyAsync<JsonException>(
            () => Client(handler).CreateAsync("https://example.com", "", "creation-6"));
    }

    // The client serializes with web defaults (camelCase) and the API reads names case-insensitively.
    private static JsonElement Property(JsonElement element, string name) =>
        element.EnumerateObject().Single(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    private static ShortLinkClient Client(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://management.test") });

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? PathAndQuery { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            PathAndQuery = request.RequestUri!.PathAndQuery;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }
}
