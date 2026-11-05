using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using ShortenerTools.Functions.Functions;

namespace ShortenerTools.Tests;

[TestClass]
public class DescriptionPrivacyTests
{
    [TestMethod]
    public async Task ProviderFailureDoesNotLogOrReturnPrivateContent()
    {
        const string privateContent = "synthetic-private-prompt-and-response";
        var client = new Mock<IChatClient>();
        client.Setup(x => x.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(privateContent));
        var logger = new Mock<ILogger<CreateDescription>>();
        using var request = new FunctionRequest("""{"url":"https://example.com","title":"test"}""");

        await new CreateDescription(logger.Object, client.Object).Run(request.Request);

        Assert.AreEqual(HttpStatusCode.BadRequest, request.Response.StatusCode);
        StringAssert.Contains(await request.ReadBody(), "Description generation failed.");
        Assert.IsFalse((await request.ReadBody()).Contains(privateContent));
        logger.Verify(x => x.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state.ToString() == "Description generation failed."),
            It.Is<Exception?>(exception => exception == null),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    [TestMethod]
    public async Task DisabledDescriptionGenerationNeedsNoClient()
    {
        using var request = new FunctionRequest();
        await new CreateDescription(Mock.Of<ILogger<CreateDescription>>()).Run(request.Request);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, request.Response.StatusCode);
    }
}
