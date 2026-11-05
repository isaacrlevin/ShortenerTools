using System.Net;
using System.Net.Http.Json;
using Moq;
using Polly.Timeout;
using ShortenerTools.Admin;
using ShortenerTools.Core.Messages;
using SocialPoster.Components;
using SocialPoster.Core;
using SocialPoster.Posting;

namespace ShortenerTools.Tests;

[TestClass]
public class ComposerLinkWorkflowTests
{
    private const string Source = "https://example.com/article";
    private const string Short = "https://go.example/my-event";

    [TestMethod]
    public async Task SubmissionCreatesLinkAndRewritesSharedAndCustomizedPosts()
    {
        using var handler = new LinkApi();
        var workflow = Workflow(handler);
        workflow.Vanity = "my-event";
        workflow.Title = "Article title";
        Assert.AreEqual(0, handler.Requests.Count, "Entering options must not create a link.");

        var result = await workflow.PrepareAsync(Submission());

        var request = handler.Requests.Single();
        Assert.AreEqual(Source, request.Url);
        Assert.AreEqual("my-event", request.Vanity);
        Assert.AreEqual("Article title", request.Title);
        Assert.AreEqual($"Read this: {Source}.", request.Message);
        Assert.IsFalse(request.PostToSocial);
        Assert.AreEqual($"Read this: {Short}.", result.BaseTemplate);
        Assert.AreEqual($"Customized ({Short}) and https://other.example/", result.Templates[Platform.Bluesky]);
        Assert.AreEqual($"No URL yet\n{Short}", result.Templates[Platform.LinkedIn]);
        Assert.AreSame(Submission().Media, result.Media);
    }

    [TestMethod]
    public async Task StagedSubmissionQueuesLinkForTimerAndStopsBeforePosting()
    {
        using var handler = new LinkApi();
        var workflow = Workflow(handler);
        workflow.StageForTimer = true;

        var exception = await Assert.ThrowsExactlyAsync<ComposerStagedException>(
            () => workflow.PrepareAsync(Submission()));

        var request = handler.Requests.Single();
        Assert.IsTrue(request.PostToSocial, "The timer only picks up unposted links.");
        Assert.AreEqual("Read this:", request.Message, "The timer appends the short link itself.");
        CollectionAssert.AreEquivalent(
            new[] { ("bluesky", "Customized ({shorturl}) and https://other.example/"), ("linkedin", "No URL yet\n{shorturl}") },
            request.Posts!.Select(post => (post.Account, post.Text)).ToArray(),
            "Each selected platform stages its own text, with the short link left as a token.");
        StringAssert.Contains(exception.Message, Short);
        Assert.AreEqual(Short, workflow.LastShortUrl);

        var reloaded = Workflow(handler);
        reloaded.RestoreState(workflow.CaptureState());
        Assert.IsTrue(reloaded.StageForTimer);

        workflow.StageForTimer = false;
        await workflow.PrepareAsync(Submission());
        Assert.AreNotEqual(handler.Requests[0].CreationId, handler.Requests[1].CreationId);
    }

    [TestMethod]
    public async Task StagingWithoutDestinationUrlRefusesInsteadOfPosting()
    {
        using var handler = new LinkApi();
        var workflow = Workflow(handler);
        workflow.StageForTimer = true;

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => workflow.PrepareAsync(Submission() with { SourceUrl = "" }));

        Assert.AreEqual(0, handler.Requests.Count);
    }

    [TestMethod]
    public async Task PlainPostSkipsShortenerAndKeepsSubmission()
    {
        using var handler = new LinkApi();
        var submission = Submission() with { SourceUrl = "" };
        Assert.AreSame(submission, await Workflow(handler).PrepareAsync(submission));
        Assert.AreEqual(0, handler.Requests.Count);
    }

    [TestMethod]
    public async Task ConflictStopsSubmissionWithoutChangingDraft()
    {
        using var handler = new LinkApi { Status = HttpStatusCode.Conflict };
        var workflow = Workflow(handler);
        var original = Submission();
        var exception =
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => workflow.PrepareAsync(original));
        StringAssert.Contains(exception.Message, "already exists");
        Assert.AreEqual($"Read this: {Source}.", original.BaseTemplate);
        Assert.AreEqual("", workflow.LastShortUrl);
    }

    [TestMethod]
    public async Task ReloadAndRetryReuseCreationIdentityAndAvoidDuplicateUrls()
    {
        using var handler = new LinkApi();
        var workflow = Workflow(handler);
        workflow.Vanity = "my-event";
        var prepared = await workflow.PrepareAsync(Submission());
        var reloaded = Workflow(handler);
        reloaded.RestoreState(workflow.CaptureState());
        var retried = await reloaded.PrepareAsync(prepared);
        Assert.AreEqual(handler.Requests[0].CreationId, handler.Requests[1].CreationId);
        Assert.AreEqual("my-event", handler.Requests[1].Vanity);
        Assert.AreEqual(prepared.BaseTemplate, retried.BaseTemplate);
        CollectionAssert.AreEquivalent(prepared.Templates.ToArray(), retried.Templates.ToArray());

        await reloaded.PrepareAsync(Submission() with { DraftId = "another-post" });
        Assert.AreNotEqual(handler.Requests[0].CreationId, handler.Requests[2].CreationId);
    }

    [TestMethod]
    public async Task LostResponseCanRetryWithTheSameCreationIdentity()
    {
        using var handler = new LinkApi { FailOnce = true };
        var workflow = Workflow(handler);
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => workflow.PrepareAsync(Submission()));
        await workflow.PrepareAsync(Submission());
        Assert.AreEqual(handler.Requests[0].CreationId, handler.Requests[1].CreationId);
    }

    [TestMethod]
    public async Task ResilienceTimeoutStopsSubmissionWithAMessageTheComposerReports()
    {
        // The composer only catches InvalidOperationException and friends; a raw Polly rejection killed the circuit.
        using var handler = new LinkApi { Failure = new TimeoutRejectedException(TimeSpan.FromSeconds(10)) };
        var workflow = Workflow(handler);
        var exception =
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => workflow.PrepareAsync(Submission()));
        StringAssert.Contains(exception.Message, "did not respond in time");
        Assert.IsInstanceOfType<TimeoutRejectedException>(exception.InnerException);
        Assert.AreEqual("", workflow.LastShortUrl);
    }

    [TestMethod]
    public async Task ChangingVanityReplacesThePreviousShortUrl()
    {
        using var handler = new LinkApi();
        var workflow = Workflow(handler);
        var prepared = await workflow.PrepareAsync(Submission());
        workflow.Vanity = "new-code";
        handler.ShortUrl = "https://go.example/new-code";
        var updated = await workflow.PrepareAsync(prepared);
        Assert.IsFalse(updated.BaseTemplate.Contains(Short));
        StringAssert.Contains(updated.BaseTemplate, handler.ShortUrl);
        Assert.AreNotEqual(handler.Requests[0].CreationId, handler.Requests[1].CreationId);
    }

    [TestMethod]
    [DataRow("ftp://example.com", "")]
    [DataRow(Source, "my/event")]
    [DataRow(Source, "KEY")]
    [DataRow("", "vanity-without-url")]
    public async Task InvalidOptionsNeverReachStorage(string source, string vanity)
    {
        using var handler = new LinkApi();
        var workflow = Workflow(handler);
        workflow.Vanity = vanity;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            workflow.PrepareAsync(Submission() with { SourceUrl = source }));
        Assert.AreEqual(0, handler.Requests.Count);
    }

    private static readonly IReadOnlyList<MediaAttachment> Media = [];

    private static ComposerSubmission Submission() => new("draft-123", Source, $"Read this: {Source}.",
        new Dictionary<Platform, string>
        {
            [Platform.Bluesky] = $"Customized ({Source}) and https://other.example/",
            [Platform.LinkedIn] = "No URL yet"
        }, Media);

    private static ComposerLinkWorkflow Workflow(LinkApi handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("management")).Returns(() =>
            new HttpClient(handler, false) { BaseAddress = new Uri("https://management.example") });
        return new ComposerLinkWorkflow(factory.Object);
    }

    private sealed class LinkApi : HttpMessageHandler
    {
        public List<ShortUrlRequest> Requests { get; } = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string ShortUrl { get; set; } = Short;
        public bool FailOnce { get; set; }
        public Exception? Failure { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((await request.Content!.ReadFromJsonAsync<ShortUrlRequest>(ct))!);
            if (Failure is not null) throw Failure;
            if (FailOnce)
            {
                FailOnce = false;
                throw new HttpRequestException("Response lost after creation.");
            }

            return new HttpResponseMessage(Status)
            {
                Content = Status == HttpStatusCode.OK
                    ? JsonContent.Create(new ShortResponse { ShortUrl = ShortUrl })
                    : JsonContent.Create(new { Message = "This shortcode already exists." })
            };
        }
    }
}