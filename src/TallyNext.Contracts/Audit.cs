using TallyNext.Domain.Audit;

namespace TallyNext.Contracts.Audit;

public sealed record AuditEntryDto(
    Guid Id,
    long ChainSequence,
    DateTime OccurredAtUtc,
    Guid? UserId,
    Guid? DeviceId,
    AuditSource Source,
    string Action,
    string EntityType,
    Guid? EntityId,
    string? Summary,
    string? ChangesJson,
    string? CorrelationId,
    Guid? ChangeId);

public sealed class AuditQuery
{
    public string? EntityType { get; set; }

    public Guid? EntityId { get; set; }

    public string? Action { get; set; }

    public DateTime? FromUtc { get; set; }

    public DateTime? ToUtc { get; set; }

    public int Page { get; set; } = 1;

    public int PageSize { get; set; } = Paging.DefaultPageSize;
}

public sealed record AuditVerificationResult(bool IsValid, long EntriesChecked, long? FirstBrokenSequence, string? Problem);
