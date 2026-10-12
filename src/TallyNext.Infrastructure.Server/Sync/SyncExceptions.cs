using System.Text.Json;

namespace TallyNext.Infrastructure.Server.Sync;

/// <summary>The row changed on the server since the device's base version. Nothing is applied.</summary>
public sealed class SyncConflictException(string reason, long serverVersion, JsonElement? serverSnapshot) : Exception(reason)
{
    public string Reason { get; } = reason;

    public long ServerVersion { get; } = serverVersion;

    public JsonElement? ServerSnapshot { get; } = serverSnapshot;
}

/// <summary>The change cannot be applied yet (for example a referenced row has not been uploaded). Retry later.</summary>
public sealed class SyncDeferredException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>The change is structurally invalid (wrong ids, wrong entity type, forbidden field changes).</summary>
public sealed class SyncRejectedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>The client speaks an unsupported protocol or schema version (HTTP 426).</summary>
public sealed class SyncVersionUnsupportedException(string message) : Exception(message);
