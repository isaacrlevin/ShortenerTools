using Azure.Core.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System.Net;
using System.Text;

namespace ShortenerTools.Tests;

internal sealed class FunctionRequest : IDisposable
{
    private readonly ServiceProvider _services;
    public HttpRequestData Request { get; }
    public HttpResponseData Response { get; }

    public FunctionRequest(string body = "{}", string? userAgent = null, string? referrer = null, string? url = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        _services = new ServiceCollection()
            .Configure<WorkerOptions>(options => options.Serializer = new JsonObjectSerializer())
            .BuildServiceProvider();
        var context = new Mock<FunctionContext>();
        context.SetupProperty(x => x.InstanceServices, _services);
        var response = new Mock<HttpResponseData>(context.Object);
        response.SetupProperty(x => x.StatusCode, HttpStatusCode.OK);
        response.SetupProperty(x => x.Body, new MemoryStream());
        response.SetupProperty(x => x.Headers, new HttpHeadersCollection());
        Response = response.Object;

        var requestHeaders = new HttpHeadersCollection();
        if (userAgent is not null)
            requestHeaders.Add("User-Agent", userAgent);
        if (referrer is not null)
            requestHeaders.Add("Referer", referrer);
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
            requestHeaders.Add(name, value);
        var request = new Mock<HttpRequestData>(context.Object);
        request.SetupGet(x => x.Body).Returns(new MemoryStream(Encoding.UTF8.GetBytes(body)));
        request.SetupGet(x => x.Headers).Returns(requestHeaders);
        request.SetupGet(x => x.Url).Returns(new Uri(url ?? "https://short.example/api/UrlClickStatsByDay"));
        request.Setup(x => x.CreateResponse()).Returns(Response);
        Request = request.Object;
    }

    public async Task<string> ReadBody()
    {
        Response.Body.Position = 0;
        using var reader = new StreamReader(Response.Body, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    public void Dispose()
    {
        Request.Body.Dispose();
        Response.Body.Dispose();
        _services.Dispose();
    }
}
