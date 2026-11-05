#nullable enable

using Azure;
using Azure.Data.AppConfiguration;
using SocialPoster.Core.Services;
using SocialPoster.Posting.Services;

namespace ShortenerTools.Admin;

/// <summary>
/// Mirrors provider credentials managed in SocialPoster to Azure App Configuration,
/// the shared source read by the Functions apps.
/// </summary>
public sealed class SocialAccountsConfigurationPublisher : BackgroundService
{
    private const string SentinelKey = "SocialPoster:Accounts:Sentinel";

    private static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(5);

    // While the empty-store guard is active every pass issues a remote read; poll slowly to stay within
    // low request quotas (Developer tier: 3,000 included requests/day, 6,000/hour).
    private static readonly TimeSpan GuardedSyncInterval = TimeSpan.FromSeconds(60);

    // Store keys must be SocialPoster's persisted names (e.g. "bluesky.handle"), not the constant names.
    private static readonly (string StoreKey, string ConfigKey)[] AccountSettings =
    [
        (SettingKeys.BlueskyHandle, "Shortener:BlueskyUserName"),
        (SettingKeys.BlueskyAppPassword, "Shortener:BlueskyPassword"),
        (SettingKeys.XConsumerKey, "Shortener:TwitterConsumerKey"),
        (SettingKeys.XConsumerSecret, "Shortener:TwitterConsumerSecret"),
        (SettingKeys.XAccessToken, "Shortener:TwitterAccessToken"),
        (SettingKeys.XAccessTokenSecret, "Shortener:TwitterAccessSecret"),
        (SettingKeys.LinkedInAccessToken, "Shortener:LinkedInAccessToken"),
        (SettingKeys.MastodonInstance, "Shortener:MastodonInstance"),
        (SettingKeys.MastodonAccessToken, "Shortener:MastodonAccessToken"),
        (SettingKeys.ThreadsAccessToken, "Shortener:ThreadsToken"),
        (SettingKeys.ThreadsUserId, "Shortener:ThreadsUserId")
    ];

    private readonly SecretStore _secretStore;
    private readonly SocialPosterStorage _storage;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SocialAccountsConfigurationPublisher> _logger;
    private readonly Dictionary<string, string> _lastPublished = new(StringComparer.Ordinal);
    private bool _loggedEmptyStoreGuard;

    public SocialAccountsConfigurationPublisher(
        SecretStore secretStore,
        SocialPosterStorage storage,
        IConfiguration configuration,
        ILogger<SocialAccountsConfigurationPublisher> logger)
    {
        _secretStore = secretStore;
        _storage = storage;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connectionString = _configuration.GetConnectionString("social-accounts");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogInformation(
                "Azure App Configuration sync is disabled; no social-accounts connection string is configured.");
            return;
        }

        // Never read the store before it opens; an unavailable store must not publish cleared accounts.
        await _storage.WaitAsync(stoppingToken);
        var client = new ConfigurationClient(connectionString);
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = SyncInterval;
            try
            {
                if (await SynchronizeAsync(client, stoppingToken))
                {
                    delay = GuardedSyncInterval;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Could not sync SocialPoster account settings to Azure App Configuration.");
            }

            await Task.Delay(delay, stoppingToken);
        }
    }

    // One list request instead of one GET per key, so the guard stays cheap on low-request-quota tiers.
    private static async Task<bool> RemoteHasAccountValuesAsync(ConfigurationClient client,
        CancellationToken cancellationToken)
    {
        var accountKeys = AccountSettings.Select(setting => setting.ConfigKey).ToHashSet(StringComparer.Ordinal);
        var selector = new SettingSelector { KeyFilter = "Shortener:*" };
        await foreach (var setting in client.GetConfigurationSettingsAsync(selector, cancellationToken))
        {
            if (accountKeys.Contains(setting.Key) && !string.IsNullOrEmpty(setting.Value))
            {
                return true;
            }
        }

        return false;
    }

    /// <returns><c>true</c> when publishing was skipped by the empty-store guard.</returns>
    private async Task<bool> SynchronizeAsync(ConfigurationClient client, CancellationToken cancellationToken)
    {
        var snapshot = await _secretStore.GetSnapshotAsync(
            AccountSettings.Select(setting => setting.StoreKey).ToArray(), cancellationToken);
        var changed = false;

        // Before this process has published anything, an entirely empty store looks the same as a wiped
        // one (recreated file share, restored subscription). Do not overwrite existing remote accounts
        // with blanks in that case; clearing accounts at runtime still publishes normally.
        if (_lastPublished.Count == 0
            && AccountSettings.All(setting => string.IsNullOrEmpty(snapshot.GetValueOrDefault(setting.StoreKey)))
            && await RemoteHasAccountValuesAsync(client, cancellationToken))
        {
            if (!_loggedEmptyStoreGuard)
            {
                _logger.LogWarning(
                    "SocialPoster has no account settings but Azure App Configuration still holds values. " +
                    "Skipping publish to avoid wiping them; re-enter accounts or restore the data directory.");
                _loggedEmptyStoreGuard = true;
            }

            return true;
        }

        _loggedEmptyStoreGuard = false;

        foreach (var (storeKey, configKey) in AccountSettings)
        {
            snapshot.TryGetValue(storeKey, out var configuredValue);
            // Publish empty values for cleared accounts so they override any stale
            // legacy environment setting still present on a Functions container.
            var value = configuredValue ?? "";
            if (_lastPublished.TryGetValue(configKey, out var previous) && previous == value)
            {
                continue;
            }

            await client.SetConfigurationSettingAsync(
                new ConfigurationSetting(configKey, value), cancellationToken: cancellationToken);

            _lastPublished[configKey] = value;
            changed = true;
        }

        if (changed)
        {
            await client.SetConfigurationSettingAsync(
                new ConfigurationSetting(SentinelKey, DateTimeOffset.UtcNow.ToString("O")),
                cancellationToken: cancellationToken);
            _logger.LogInformation("SocialPoster account settings were synchronized to Azure App Configuration.");
        }

        return false;
    }
}