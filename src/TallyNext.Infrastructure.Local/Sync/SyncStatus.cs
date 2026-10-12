namespace TallyNext.Infrastructure.Local.Sync;

public enum SyncState
{
    NotSignedIn,
    Offline,
    Syncing,
    UpToDate,

    /// <summary>Local changes are saved on this computer but not yet accepted by the server.</summary>
    PendingChanges,

    /// <summary>Conflicts or rejected changes are waiting for a decision.</summary>
    NeedsAttention,
    SignInRequired,
    UpdateRequired,
    Error,
}

public sealed record SyncStatusSnapshot(
    SyncState State,
    int PendingCount,
    int AttentionCount,
    DateTime? LastSuccessUtc,
    string? Message,
    DateTime? NextAttemptUtc)
{
    public static SyncStatusSnapshot Initial { get; } = new(SyncState.NotSignedIn, 0, 0, null, null, null);
}

/// <summary>Observable synchronization status for the UI (thread-safe).</summary>
public sealed class SyncStatusTracker
{
    private readonly Lock _gate = new();
    private SyncStatusSnapshot _current = SyncStatusSnapshot.Initial;

    public event Action<SyncStatusSnapshot>? Changed;

    public SyncStatusSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public void Publish(Func<SyncStatusSnapshot, SyncStatusSnapshot> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        SyncStatusSnapshot next;
        lock (_gate)
        {
            next = update(_current);
            _current = next;
        }

        Changed?.Invoke(next);
    }
}
