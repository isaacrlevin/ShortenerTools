using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShortenerTools.Core.Domain;
using ShortenerTools.Functions.Functions;

namespace ShortenerTools.Tests;

[TestClass]
public class StagingSafetyTests
{
    [TestMethod]
    [DataRow("Staging", false, false)]
    [DataRow("staging", false, false)]
    [DataRow("Production", true, false)]
    [DataRow("Production", false, true)]
    [DataRow("Development", false, true)]
    public void PostingPolicyPreservesProductionAndFailsClosedInStaging(
        string environment, bool disabled, bool expected)
    {
        var settings = new ShortenerSettings
        {
            EnvironmentName = environment,
            DisableExternalPosting = disabled
        };

        Assert.AreEqual(expected, settings.ExternalPostingAllowed);
    }

    [TestMethod]
    public async Task StagingEmailDoesNotRequireCredentialsOrSend()
    {
        var settings = new ShortenerSettings { EnvironmentName = "Staging" };
        var service = new EmailService(NullLoggerFactory.Instance, settings);

        await service.SendTwitterIntentEmail("staging", "https://example.com", "test");
    }

    [TestMethod]
    public async Task StagingSchedulerDoesNotAccessStorageOrProviders()
    {
        var settings = new ShortenerSettings
        {
            EnvironmentName = "Staging",
            CustomDomain = "https://staging.example/"
        };
        var service = new SchedulePost(settings, null!);

        Assert.AreEqual("https://staging.example/", service.ShortenerBase);
        await service.ShortPostTimer(null!);
    }
}
