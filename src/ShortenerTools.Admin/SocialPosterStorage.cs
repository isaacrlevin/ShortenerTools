#nullable enable

using System.Data.Common;
using SocialPoster.Core;

namespace ShortenerTools.Admin;

/// <summary>
/// Opens SocialPoster's SQLite store after the web host starts, retrying until it is available.
/// </summary>
/// <remarks>
/// The store lives on an Azure Files share. ACA keeps the previous revision running until the new one is healthy,
/// and while that replica holds the WAL database open, opens from another host fail. Initializing before Kestrel
/// listened meant the new revision crashed, never became healthy and the old one never stopped. Listening first lets
/// the cut-over happen; the old replica then releases the database and initialization succeeds.
/// </remarks>
public sealed class SocialPosterStorage(IServiceProvider services, ILogger<SocialPosterStorage> logger)
    : BackgroundService
{
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsReady => _ready.Task.IsCompletedSuccessfully;

    /// <summary>Waits until storage is initialized; returns false if <paramref name="timeout"/> elapses first.</summary>
    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await _ready.Task.WaitAsync(timeout, cancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public Task WaitAsync(CancellationToken cancellationToken) => _ready.Task.WaitAsync(cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await services.InitializeSocialPosterAsync(stoppingToken);
                _ready.TrySetResult();
                logger.LogInformation("SocialPoster storage initialized after {Attempts} attempt(s).", attempt);
                return;
            }
            catch (Exception exception) when (exception is DbException or IOException or UnauthorizedAccessException
                                              && !stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("SocialPoster storage is unavailable (attempt {Attempt}, {FailureType}: {Message}); " +
                                  "retrying in {Delay}.", attempt, exception.GetType().Name, exception.Message, delay);
            }

            await Task.Delay(delay, stoppingToken);
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxDelay.Ticks));
        }
    }
}
