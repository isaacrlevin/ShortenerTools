using Azure.Data.Tables;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShortenerTools.Core.Domain;

namespace ShortenerTools.Tests;

/// <summary>Runs against a local Azurite table endpoint and is skipped when Azurite is not running.</summary>
[TestClass]
public class PostVersionTests
{
    /// <summary>Override with AZURITE_TABLE_CONNECTION when Azurite runs on a non-default port.</summary>
    private static readonly string AzuriteConnection =
        Environment.GetEnvironmentVariable("AZURITE_TABLE_CONNECTION") ?? "UseDevelopmentStorage=true";

    private static async Task<StorageTableHelper> CreateHelperAsync()
    {
        // These tests write rows, so never let an override point them at a live account.
        if (!AzuriteConnection.Contains("UseDevelopmentStorage", StringComparison.OrdinalIgnoreCase)
            && !AzuriteConnection.Contains("devstoreaccount1", StringComparison.Ordinal))
        {
            Assert.Inconclusive("AZURITE_TABLE_CONNECTION must target the Azurite development account.");
        }

        var service = new TableServiceClient(AzuriteConnection);
        try
        {
            await service.CreateTableIfNotExistsAsync("PostVersions");
        }
        catch (Exception exception)
        {
            Assert.Inconclusive($"Azurite table endpoint is unreachable: {exception.Message}");
        }

        return new StorageTableHelper(service);
    }

    [TestMethod]
    public async Task VersionsAreAppendOnlyAndNewestFirstPerAccount()
    {
        var helper = await CreateHelperAsync();
        var code = $"pv-{Guid.NewGuid():N}";
        var start = DateTimeOffset.UtcNow;

        await helper.AddPostVersion(new PostVersionEntity(code, "bluesky:main", "v1", createdAt: start));
        await helper.AddPostVersion(new PostVersionEntity(code, "bluesky:main", "v2", createdAt: start.AddMinutes(1)));
        await helper.AddPostVersion(new PostVersionEntity(code, "bluesky:main2", "other", createdAt: start));
        await helper.AddPostVersion(new PostVersionEntity(code, "mastodon", "m1", createdAt: start));

        var bluesky = await helper.GetPostVersions(code, "bluesky:main");
        CollectionAssert.AreEqual(new[] { "v2", "v1" }, bluesky.Select(v => v.Text).ToArray());
        Assert.AreEqual("v2", (await helper.GetLatestPostVersion(code, "bluesky:main"))!.Text);
        Assert.AreEqual(4, (await helper.GetPostVersions(code)).Count);
    }

    [TestMethod]
    public async Task LegacyLinkWithoutVersionsReturnsOneSynthesizedVersion()
    {
        var helper = await CreateHelperAsync();
        var code = $"legacy-{Guid.NewGuid():N}";
        await helper.SaveShortUrlEntity(new ShortUrlEntity("https://example.com", code, "Title", "Old message"));

        var versions = await helper.GetPostVersions(code);

        Assert.AreEqual(1, versions.Count);
        Assert.IsTrue(versions[0].IsLegacy);
        Assert.AreEqual("Old message", versions[0].Text);
    }
}
