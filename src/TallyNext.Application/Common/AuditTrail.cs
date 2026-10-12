using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TallyNext.Application.Abstractions;
using TallyNext.Domain.Audit;

namespace TallyNext.Application.Common;

/// <summary>
/// Records business events. Entries are added to the same unit of work as the change they describe, so they
/// commit (or roll back) atomically with it; the persistence layer seals them into the hash chain on save.
/// </summary>
public interface IAuditTrail
{
    void Record(
        Guid? companyId,
        string action,
        string entityType,
        Guid? entityId,
        string? summary = null,
        object? before = null,
        object? after = null,
        Guid? changeId = null,
        AuditSource? source = null,
        Guid? deviceId = null);
}

internal sealed class AuditTrail(IAccountingDbContext db, IExecutionContext context, TimeProvider clock) : IAuditTrail
{
    public void Record(
        Guid? companyId,
        string action,
        string entityType,
        Guid? entityId,
        string? summary = null,
        object? before = null,
        object? after = null,
        Guid? changeId = null,
        AuditSource? source = null,
        Guid? deviceId = null)
    {
        db.AuditEntries.Add(AuditEntry.Record(
            companyId,
            action,
            entityType,
            entityId,
            clock.GetUtcNow().UtcDateTime,
            context.UserId,
            deviceId ?? context.DeviceId,
            source ?? context.AuditSource,
            summary,
            AuditDiff.Describe(before, after),
            context.CorrelationId,
            changeId));
    }
}

/// <summary>
/// Produces the controlled field-level description stored with an audit record:
/// creates store <c>{"new":{...}}</c>, deletes <c>{"old":{...}}</c>, updates only the changed fields
/// <c>{"changes":{"Name":{"old":"A","new":"B"}}}</c>. Bookkeeping fields (timestamps, versions) are excluded.
/// </summary>
public static class AuditDiff
{
    private static readonly HashSet<string> Ignored = new(StringComparer.Ordinal)
    {
        "CreatedAtUtc", "UpdatedAtUtc", "UpdatedByUserId", "Version", "NormalizedName",
    };

    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    public static string? Describe(object? before, object? after)
    {
        if (before is null && after is null)
        {
            return null;
        }

        var oldNode = before is null ? null : Strip(JsonSerializer.SerializeToNode(before, before.GetType(), Options));
        var newNode = after is null ? null : Strip(JsonSerializer.SerializeToNode(after, after.GetType(), Options));

        if (oldNode is null)
        {
            return new JsonObject { ["new"] = newNode }.ToJsonString();
        }

        if (newNode is null)
        {
            return new JsonObject { ["old"] = oldNode }.ToJsonString();
        }

        if (oldNode is not JsonObject oldObj || newNode is not JsonObject newObj)
        {
            return new JsonObject { ["old"] = oldNode, ["new"] = newNode }.ToJsonString();
        }

        var changes = new JsonObject();
        foreach (var (key, newValue) in newObj)
        {
            var oldValue = oldObj[key];
            if (!JsonNode.DeepEquals(oldValue, newValue))
            {
                changes[key] = new JsonObject { ["old"] = oldValue?.DeepClone(), ["new"] = newValue?.DeepClone() };
            }
        }

        return changes.Count == 0 ? null : new JsonObject { ["changes"] = changes }.ToJsonString();
    }

    private static JsonNode? Strip(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(p => p.Key).Where(Ignored.Contains).ToList())
            {
                obj.Remove(key);
            }
        }

        return node;
    }
}
