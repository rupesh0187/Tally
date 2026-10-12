using TallyNext.Domain.Common;
using TallyNext.Domain.Vouchers;

namespace TallyNext.Contracts.Vouchers;

public sealed record VoucherTypeDto(Guid Id, string Name, VoucherBaseType BaseType, string Abbreviation, bool IsSystem, bool IsActive);

public sealed class VoucherLineInput
{
    public Guid LedgerId { get; set; }

    public DrCr Side { get; set; } = DrCr.Debit;

    /// <summary>Positive amount with at most two decimal places. Never rounded silently.</summary>
    public decimal Amount { get; set; }

    public string? Narration { get; set; }
}

public sealed class VoucherInput
{
    /// <summary>Optional client-generated UUIDv7. Re-sending the same id with the same content is idempotent.</summary>
    public Guid? Id { get; set; }

    public Guid VoucherTypeId { get; set; }

    public DateOnly Date { get; set; }

    public string? Narration { get; set; }

    public string? Reference { get; set; }

    public DateOnly? ReferenceDate { get; set; }

    public List<VoucherLineInput> Lines { get; set; } = [];

    /// <summary>True posts immediately (must balance); false saves a draft.</summary>
    public bool Post { get; set; } = true;

    /// <summary>Client retry key (also accepted as the Idempotency-Key HTTP header).</summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>For draft updates: fails with a conflict if the voucher changed since this version.</summary>
    public long? ExpectedVersion { get; set; }
}

public sealed record VoucherLineDto(Guid Id, int LineNo, Guid LedgerId, string LedgerName, DrCr Side, decimal Amount, string? Narration);

public sealed record VoucherDto(
    Guid Id,
    Guid VoucherTypeId,
    string VoucherTypeName,
    VoucherBaseType BaseType,
    DateOnly Date,
    string FinancialYear,
    string? Number,
    VoucherStatus Status,
    string? Narration,
    string? Reference,
    DateOnly? ReferenceDate,
    Guid? ReversalOfVoucherId,
    string? ReversalOfNumber,
    Guid? ReversedByVoucherId,
    string? ReversedByNumber,
    decimal Total,
    IReadOnlyList<VoucherLineDto> Lines,
    Guid CreatedByUserId,
    Guid? CreatedOnDeviceId,
    DateTime CreatedAtUtc,
    DateTime? PostedAtUtc,
    long Version);

public sealed record VoucherListItem(
    Guid Id,
    DateOnly Date,
    string? Number,
    string VoucherTypeName,
    VoucherBaseType BaseType,
    VoucherStatus Status,
    string Particulars,
    decimal Total,
    string? Narration,
    bool IsReversal,
    long Version);

public sealed class VoucherQuery
{
    public DateOnly? From { get; set; }

    public DateOnly? To { get; set; }

    public Guid? VoucherTypeId { get; set; }

    public VoucherStatus? Status { get; set; }

    /// <summary>Matches voucher number, reference or narration.</summary>
    public string? Search { get; set; }

    public int Page { get; set; } = 1;

    public int PageSize { get; set; } = Paging.DefaultPageSize;
}

public sealed class ReverseVoucherRequest
{
    public DateOnly Date { get; set; }

    public string? Narration { get; set; }

    /// <summary>Optional client-generated id of the reversal voucher (idempotent retries).</summary>
    public Guid? ReversalId { get; set; }
}
