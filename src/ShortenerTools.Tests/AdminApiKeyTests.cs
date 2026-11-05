using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShortenerTools.Functions;

namespace ShortenerTools.Tests;

[TestClass]
public class AdminApiKeyTests
{
    [TestMethod]
    [DataRow("configured-key", "configured-key", true)]
    [DataRow("wrong-key", "configured-key", false)]
    [DataRow("", "configured-key", false)]
    [DataRow("configured-key", "", false)]
    [DataRow("configured-key", null, false)]
    public void KeyValidationFailsClosed(string supplied, string? expected, bool accepted)
    {
        var headers = new HttpHeadersCollection();
        headers.Add("X-Admin-Api-Key", supplied);
        Assert.AreEqual(accepted, AdminApiKeyMiddleware.HasValidKey(headers, expected));
    }

    [TestMethod]
    public void MissingHeaderIsRejected() =>
        Assert.IsFalse(AdminApiKeyMiddleware.HasValidKey(new HttpHeadersCollection(), "configured-key"));
}
