using Microsoft.EntityFrameworkCore;
using TallyNext.Application.Abstractions;
using TallyNext.Application.Common;
using TallyNext.Contracts.Vouchers;
using TallyNext.Domain.Common;
using TallyNext.Domain.Vouchers;

namespace TallyNext.Application.Bills;

/// <summary>
/// Bill-wise rules for vouchers entered by a user (desktop, browser or API): defaults for lines without an explicit
/// breakup, and the check that every settlement refers to a bill that exists. Uploads from devices are not re-checked
/// for existence - a device may legitimately settle a bill the server has not received yet; an unmatched reference
/// shows in the outstanding reports instead of blocking synchronization.
/// </summary>
public static class BillAllocationRules
{
    /// <summary>
    /// Returns <paramref name="lines"/> with the bill-wise breakup of every line posted to a bill-wise ledger:
    /// the given allocations (amounts parsed, empty amount = remainder, due dates defaulted from the credit period),
    /// or a default when none were given.
    /// </summary>
    /// <param name="inputs">Requested allocations per line (same order as <paramref name="lines"/>), null when none.</param>
    /// <param name="defaultBillName">Name of a default new bill (e.g. the supplier's invoice number); null = voucher number.</param>
    public static async Task<List<VoucherLineSpec>> ApplyAsync(
        IAccountingDbContext db,
        Guid companyId,
        VoucherBaseType type,
        DateOnly date,
        IReadOnlyList<VoucherLineSpec> lines,
        IReadOnlyList<IReadOnlyList<BillAllocationInput>?> inputs,
        string? defaultBillName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(inputs);
        var ledgerIds = lines.Select(l => l.LedgerId).Distinct().ToList();
        var ledgers = await db.Ledgers.AsNoTracking()
            .Where(l => l.CompanyId == companyId && ledgerIds.Contains(l.Id))
            .Select(l => new { l.Id, l.Name, l.IsBillWise, l.CreditPeriodDays })
            .ToDictionaryAsync(l => l.Id, cancellationToken);

        var result = new List<VoucherLineSpec>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            var spec = lines[i];
            var requested = i < inputs.Count ? inputs[i] : null;
            if (!ledgers.TryGetValue(spec.LedgerId, out var ledger))
            {
                // Unknown ledgers are reported by the posting rules.
                result.Add(spec);
                continue;
            }

            if (!ledger.IsBillWise)
            {
                if (requested is { Count: > 0 })
                {
                    throw new ValidationFailedException("bill.ledger_not_billwise", $"Line {i + 1}: '{ledger.Name}' does not maintain bill-wise details.");
                }

                result.Add(spec);
                continue;
            }

            DateOnly? defaultDue = ledger.CreditPeriodDays is { } days ? date.AddDays(days) : null;
            List<BillAllocationSpec> bills;
            if (requested is not { Count: > 0 })
            {
                bills = type is VoucherBaseType.Contra or VoucherBaseType.Payment or VoucherBaseType.Receipt
                    ? [new BillAllocationSpec(BillAllocationKind.OnAccount, null, null, spec.Amount)]
                    : [new BillAllocationSpec(BillAllocationKind.NewRef, defaultBillName, defaultDue, spec.Amount)];
            }
            else
            {
                bills = Parse(i, spec.Amount, requested, defaultDue);
            }

            result.Add(spec with { Bills = bills });
        }

        return result;
    }

    /// <summary>
    /// Every settlement (Agst Ref) must name a bill of the same ledger: an opening bill, a bill created by a posted
    /// voucher, or one created earlier in the same voucher. Reversals mirror their original and are not re-checked.
    /// </summary>
    public static async Task EnsureReferencesExistAsync(IAccountingDbContext db, Voucher voucher, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(voucher);
        if (voucher.IsReversal)
        {
            return;
        }

        var references = voucher.BillAllocations.Where(a => a.Kind == BillAllocationKind.AgstRef && a.NormalizedBillName is not null).ToList();
        if (references.Count == 0)
        {
            return;
        }

        var known = voucher.BillAllocations
            .Where(a => a.Kind is BillAllocationKind.NewRef or BillAllocationKind.Advance && a.NormalizedBillName is not null)
            .Select(a => (a.LedgerId, a.NormalizedBillName!))
            .ToHashSet();
        var ledgerIds = references.Select(a => a.LedgerId).Distinct().ToList();
        var names = references.Select(a => a.NormalizedBillName!).Distinct().ToList();
        var posted = await db.VoucherBillAllocations.AsNoTracking()
            .Where(a => a.CompanyId == voucher.CompanyId && a.IsPosted && a.VoucherId != voucher.Id
                        && ledgerIds.Contains(a.LedgerId) && a.NormalizedBillName != null && names.Contains(a.NormalizedBillName))
            .Select(a => new { a.LedgerId, Name = a.NormalizedBillName! })
            .Distinct()
            .ToListAsync(cancellationToken);
        known.UnionWith(posted.Select(p => (p.LedgerId, p.Name)));

        var openingBills = await db.Ledgers.AsNoTracking()
            .Where(l => l.CompanyId == voucher.CompanyId && ledgerIds.Contains(l.Id))
            .Select(l => new { l.Id, l.OpeningBills })
            .ToListAsync(cancellationToken);
        known.UnionWith(openingBills.SelectMany(l => l.OpeningBills.Select(b => (l.Id, BillNames.Normalize(b.Name)))));

        var missing = references.FirstOrDefault(a => !known.Contains((a.LedgerId, a.NormalizedBillName!)));
        if (missing is not null)
        {
            throw new ValidationFailedException("bill.not_found", $"Line {missing.LineNo}: there is no bill '{missing.BillName}' for this ledger.");
        }
    }

    private static List<BillAllocationSpec> Parse(int lineIndex, Money lineAmount, IReadOnlyList<BillAllocationInput> requested, DateOnly? defaultDue)
    {
        var field = $"Lines[{lineIndex}].Bills";
        if (requested.Count(b => b.Amount is null) > 1)
        {
            throw new ValidationFailedException("bill.amount_required", $"Line {lineIndex + 1}: only one bill allocation may take the remaining amount.");
        }

        var given = requested.Where(b => b.Amount is not null).Select((b, j) => Amounts.Parse(b.Amount!.Value, $"{field}[{j}].Amount")).ToList();
        var remainder = lineAmount - Money.Sum(given);
        var bills = new List<BillAllocationSpec>(requested.Count);
        foreach (var b in requested)
        {
            var amount = b.Amount is { } a ? Amounts.Parse(a, $"{field}.Amount") : remainder;
            var due = b.Kind is BillAllocationKind.NewRef or BillAllocationKind.Advance ? b.DueDate ?? defaultDue : b.DueDate;
            bills.Add(new BillAllocationSpec(b.Kind, b.BillName, due, amount));
        }

        return bills;
    }
}
