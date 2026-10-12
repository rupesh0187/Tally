using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
using TallyNext.Infrastructure.Local.Session;

namespace TallyNext.Infrastructure.Local.Sync;

/// <summary>
/// Runs sync cycles in the background: on a timer, immediately when the network comes back or a user asks, and
/// with exponential backoff plus jitter after failures. At most one cycle runs at a time; stopping cancels the
/// current cycle (all sync state transitions are transactional, so cancellation is safe).
/// </summary>
public sealed class SyncScheduler(SyncEngine engine, SyncStatusTracker status, DeviceSession session, TimeProvider clock, ILogger<SyncScheduler> logger) : IAsyncDisposable
{
    public static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly Lock _gate = new();
    private CancellationTokenSource? _stopping;
    private Task? _loop;

    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _loop is { IsCompleted: false };
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_loop is { IsCompleted: false })
            {
                return;
            }

            _stopping = new CancellationTokenSource();
            var token = _stopping.Token;
            _loop = Task.Run(() => RunAsync(token), CancellationToken.None);
        }

        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        session.Changed += TriggerNow;
    }

    /// <summary>Requests a cycle as soon as possible (manual "Sync now", network restored, sign-in).</summary>
    public void TriggerNow()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A cycle is already requested.
        }
    }

    public async Task StopAsync()
    {
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        session.Changed -= TriggerNow;
        Task? loop;
        lock (_gate)
        {
            _stopping?.Cancel();
            loop = _loop;
        }

        if (loop is not null)
        {
            try
            {
                await loop;
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _stopping?.Dispose();
        _signal.Dispose();
    }

    /// <summary>Delay before attempt <paramref name="failures"/>+1: 2 s, 4 s, 8 s, ... capped at 5 min, ±20 % jitter.</summary>
    public static TimeSpan Backoff(int failures, double jitterSample)
    {
        var exponent = Math.Clamp(failures - 1, 0, 20);
        var raw = Math.Min(MaxBackoff.TotalMilliseconds, BaseBackoff.TotalMilliseconds * Math.Pow(2, exponent));
        var jitter = 0.8 + (0.4 * Math.Clamp(jitterSample, 0, 1));
        return TimeSpan.FromMilliseconds(raw * jitter);
    }

    private async Task RunAsync(CancellationToken stopping)
    {
        var failures = 0;
        while (!stopping.IsCancellationRequested)
        {
            var delay = Interval;
            if (session.User is not null)
            {
                try
                {
                    await engine.SyncOnceAsync(stopping);
                    failures = 0;
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                    break;
                }
                catch (SyncTransportException ex)
                {
                    failures++;
                    delay = ex.Failure switch
                    {
                        SyncTransportFailure.Offline => Backoff(failures, Random.Shared.NextDouble()),
                        SyncTransportFailure.UpgradeRequired => TimeSpan.FromHours(1),
                        SyncTransportFailure.Unauthorized or SyncTransportFailure.AccessRevoked => Interval * 5,
                        _ => Backoff(failures, Random.Shared.NextDouble()),
                    };
                    logger.LogInformation("Sync attempt failed ({Failure}); next attempt in {Delay}", ex.Failure, delay);
                }
                catch (Exception ex)
                {
                    failures++;
                    delay = Backoff(failures, Random.Shared.NextDouble());
                    logger.LogError(ex, "Sync cycle failed unexpectedly");
                    status.Publish(s => s with { State = SyncState.Error, Message = "Synchronization failed. It will be retried automatically." });
                }
            }

            var next = clock.GetUtcNow().UtcDateTime.Add(delay);
            status.Publish(s => s with { NextAttemptUtc = next });
            try
            {
                await _signal.WaitAsync(delay, stopping);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable)
        {
            TriggerNow();
        }
    }
}
