using Microsoft.EntityFrameworkCore;
using TallyNext.Application.Abstractions;
using TallyNext.Application.Common;
using TallyNext.Application.Security;
using TallyNext.Application.Sync;
using TallyNext.Contracts;
using TallyNext.Contracts.Vouchers;
using TallyNext.Domain.Common;
using TallyNext.Domain.Companies;
using TallyNext.Domain.Vouchers;

namespace TallyNext.Application.Vouchers;

/// <summary>Voucher entry workflows: draft, post, cancel draft, reverse.</summary>
public sealed class VoucherService(
    IAccountingDbContext db,
    IAccessGuard guard,
    IAuditTrail audit,
    IExecutionContext context,
    VoucherPostingRules rules,
    VoucherNumberAllocator allocator,
    TimeProvider clock)
{
    public async Task<IReadOnlyList<VoucherTypeDto>> ListTypesAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        await guard.RequireAsync(companyId, Permission.VoucherView, cancellationToken);
        return await db.VoucherTypes.AsNoTracking()
            .Where(t => t.CompanyId == companyId && !t.IsDeleted)
            .OrderBy(t => t.BaseType).ThenBy(t => t.Name)
            .Select(t => new VoucherTypeDto(t.Id, t.Name, t.BaseType, t.Abbreviation, t.IsSystem, t.IsActive))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Saves a new voucher as a draft or posts it immediately. Retrying with the same voucher id or the same
    /// idempotency key returns the voucher created by the first attempt instead of creating a duplicate.
    /// </summary>
    public async Task<VoucherDto> CreateAsync(Guid companyId, VoucherInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        await guard.RequireAsync(companyId, Permission.VoucherCreate, cancellationToken);
        var userId = context.RequireUserId();
        var lines = ParseLines(input.Lines);
        var idempotencyKey = string.IsNullOrWhiteSpace(input.IdempotencyKey) ? null : input.IdempotencyKey.Trim();

        await using var scope = await db.BeginCompanyWriteAsync(companyId, cancellationToken);
        var replay = await FindReplayAsync(companyId, input.Id, idempotencyKey, cancellationToken);
        if (replay is not null)
        {
            return await GetAsync(companyId, replay.Value, cancellationToken);
        }

        var company = await LoadCompanyAsync(companyId, cancellationToken);
        var type = await db.VoucherTypes.SingleOrDefaultAsync(t => t.CompanyId == companyId && t.Id == input.VoucherTypeId && !t.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("voucher_type");

        var voucher = Voucher.CreateDraft(
            input.Id ?? Ids.New(),
            company,
            type,
            input.Date,
            new VoucherHeader(input.Narration, input.Reference, input.ReferenceDate),
            lines,
            userId,
            context.DeviceId,
            idempotencyKey);

        await rules.ValidateAsync(voucher, forPosting: input.Post, cancellationToken);
        if (input.Post)
        {
            await PostInternalAsync(scope, company, voucher, userId, cancellationToken);
        }

        db.Vouchers.Add(voucher);
        audit.Record(
            companyId,
            input.Post ? "voucher.posted" : "voucher.draft_saved",
            "voucher",
            voucher.Id,
            Describe(type, voucher),
            after: SnapshotMapper.ToSnapshot(voucher));
        await db.SaveChangesAsync(cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return await GetAsync(companyId, voucher.Id, cancellationToken);
    }

    /// <summary>Replaces the content of a draft; optionally posts it in the same step.</summary>
    public async Task<VoucherDto> UpdateDraftAsync(Guid companyId, Guid voucherId, VoucherInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        await guard.RequireAsync(companyId, Permission.VoucherCreate, cancellationToken);
        var userId = context.RequireUserId();
        var lines = ParseLines(input.Lines);

        await using var scope = await db.BeginCompanyWriteAsync(companyId, cancellationToken);
        var company = await LoadCompanyAsync(companyId, cancellationToken);
        var voucher = await LoadVoucherAsync(companyId, voucherId, cancellationToken);
        if (input.ExpectedVersion is { } expected && expected != voucher.Version)
        {
            throw new ConflictException("voucher.version_conflict", "The voucher was changed by someone else. Reload and try again.", voucher.Version);
        }

        if (input.VoucherTypeId != Guid.Empty && input.VoucherTypeId != voucher.VoucherTypeId)
        {
            throw new ValidationFailedException("voucher.type_immutable", "The voucher type of a saved voucher cannot be changed.");
        }

        var before = SnapshotMapper.ToSnapshot(voucher);
        voucher.UpdateDraft(company, input.Date, new VoucherHeader(input.Narration, input.Reference, input.ReferenceDate), lines);
        await rules.ValidateAsync(voucher, forPosting: input.Post, cancellationToken);
        if (input.Post)
        {
            await PostInternalAsync(scope, company, voucher, userId, cancellationToken);
        }

        var type = await db.VoucherTypes.AsNoTracking().SingleAsync(t => t.Id == voucher.VoucherTypeId, cancellationToken);
        audit.Record(companyId, input.Post ? "voucher.posted" : "voucher.draft_updated", "voucher", voucher.Id, Describe(type, voucher), before, SnapshotMapper.ToSnapshot(voucher));
        await db.SaveChangesAsync(cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return await GetAsync(companyId, voucher.Id, cancellationToken);
    }

    public async Task<VoucherDto> PostAsync(Guid companyId, Guid voucherId, CancellationToken cancellationToken = default)
    {
        await guard.RequireAsync(companyId, Permission.VoucherCreate, cancellationToken);
        var userId = context.RequireUserId();

        await using var scope = await db.BeginCompanyWriteAsync(companyId, cancellationToken);
        var company = await LoadCompanyAsync(companyId, cancellationToken);
        var voucher = await LoadVoucherAsync(companyId, voucherId, cancellationToken);
        var before = SnapshotMapper.ToSnapshot(voucher);
        await rules.ValidateAsync(voucher, forPosting: true, cancellationToken);
        await PostInternalAsync(scope, company, voucher, userId, cancellationToken);

        var type = await db.VoucherTypes.AsNoTracking().SingleAsync(t => t.Id == voucher.VoucherTypeId, cancellationToken);
        audit.Record(companyId, "voucher.posted", "voucher", voucher.Id, Describe(type, voucher), before, SnapshotMapper.ToSnapshot(voucher));
        await db.SaveChangesAsync(cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return await GetAsync(companyId, voucher.Id, cancellationToken);
    }

    public async Task<VoucherDto> CancelDraftAsync(Guid companyId, Guid voucherId, CancellationToken cancellationToken = default)
    {
        await guard.RequireAsync(companyId, Permission.VoucherCreate, cancellationToken);
        await using var scope = await db.BeginCompanyWriteAsync(companyId, cancellationToken);
        var voucher = await LoadVoucherAsync(companyId, voucherId, cancellationToken);
        var before = SnapshotMapper.ToSnapshot(voucher);
        voucher.CancelDraft();
        audit.Record(companyId, "voucher.draft_cancelled", "voucher", voucher.Id, "Draft voucher cancelled", before, SnapshotMapper.ToSnapshot(voucher));
        await db.SaveChangesAsync(cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return await GetAsync(companyId, voucher.Id, cancellationToken);
    }

    /// <summary>
    /// Corrects a posted voucher by posting its mirror image on <see cref="ReverseVoucherRequest.Date"/>.
    /// The original stays untouched; a voucher can be reversed at most once (enforced by a unique index).
    /// </summary>
    public async Task<VoucherDto> ReverseAsync(Guid companyId, Guid voucherId, ReverseVoucherRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await guard.RequireAsync(companyId, Permission.VoucherReverse, cancellationToken);
        var userId = context.RequireUserId();

        await using var scope = await db.BeginCompanyWriteAsync(companyId, cancellationToken);
        if (request.ReversalId is { } reversalId
            && await db.Vouchers.AsNoTracking().AnyAsync(v => v.Id == reversalId && v.CompanyId == companyId && v.ReversalOfVoucherId == voucherId, cancellationToken))
        {
            return await GetAsync(companyId, reversalId, cancellationToken);
        }

        var company = await LoadCompanyAsync(companyId, cancellationToken);
        var original = await LoadVoucherAsync(companyId, voucherId, cancellationToken);
        if (await db.Vouchers.AsNoTracking().AnyAsync(v => v.CompanyId == companyId && v.ReversalOfVoucherId == voucherId && !v.IsDeleted, cancellationToken))
        {
            throw new ConflictException("voucher.already_reversed", "This voucher has already been reversed.");
        }

        var reversal = Voucher.CreateReversal(request.ReversalId ?? Ids.New(), company, original, request.Date, request.Narration, userId, context.DeviceId);
        await rules.ValidateAsync(reversal, forPosting: true, cancellationToken);
        await PostInternalAsync(scope, company, reversal, userId, cancellationToken);
        db.Vouchers.Add(reversal);

        audit.Record(companyId, "voucher.reversed", "voucher", original.Id, $"Voucher {original.Number} reversed by {reversal.Number}");
        audit.Record(companyId, "voucher.posted", "voucher", reversal.Id, $"Reversal {reversal.Number} of {original.Number}", after: SnapshotMapper.ToSnapshot(reversal));
        await db.SaveChangesAsync(cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return await GetAsync(companyId, reversal.Id, cancellationToken);
    }

    public async Task<VoucherDto> GetAsync(Guid companyId, Guid voucherId, CancellationToken cancellationToken = default)
    {
        await guard.RequireAsync(companyId, Permission.VoucherView, cancellationToken);
        var voucher = await db.Vouchers.AsNoTracking()
            .Include(v => v.Lines)
            .SingleOrDefaultAsync(v => v.CompanyId == companyId && v.Id == voucherId && !v.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("voucher");

        var ledgerIds = voucher.Lines.Select(l => l.LedgerId).Distinct().ToList();
        var ledgerNames = await db.Ledgers.AsNoTracking()
            .Where(l => l.CompanyId == companyId && ledgerIds.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, l => l.Name, cancellationToken);
        var type = await db.VoucherTypes.AsNoTracking().SingleAsync(t => t.Id == voucher.VoucherTypeId, cancellationToken);
        var reversedBy = await db.Vouchers.AsNoTracking()
            .Where(v => v.CompanyId == companyId && v.ReversalOfVoucherId == voucherId && !v.IsDeleted)
            .Select(v => new { v.Id, v.Number })
            .FirstOrDefaultAsync(cancellationToken);
        string? reversalOfNumber = null;
        if (voucher.ReversalOfVoucherId is { } originalId)
        {
            reversalOfNumber = await db.Vouchers.AsNoTracking().Where(v => v.Id == originalId).Select(v => v.Number).FirstOrDefaultAsync(cancellationToken);
        }

        return new VoucherDto(
            voucher.Id,
            voucher.VoucherTypeId,
            type.Name,
            voucher.BaseType,
            voucher.Date,
            new FinancialYear(voucher.FinancialYearStart).Label,
            voucher.Number,
            voucher.Status,
            voucher.Narration,
            voucher.Reference,
            voucher.ReferenceDate,
            voucher.ReversalOfVoucherId,
            reversalOfNumber,
            reversedBy?.Id,
            reversedBy?.Number,
            Amounts.ToDecimal(voucher.TotalMinor),
            voucher.Lines.OrderBy(l => l.LineNo)
                .Select(l => new VoucherLineDto(l.Id, l.LineNo, l.LedgerId, ledgerNames.GetValueOrDefault(l.LedgerId, "(unknown ledger)"), l.Side, Amounts.ToDecimal(l.AmountMinor), l.Narration))
                .ToList(),
            voucher.CreatedByUserId,
            voucher.CreatedOnDeviceId,
            voucher.CreatedAtUtc,
            voucher.PostedAtUtc,
            voucher.Version);
    }

    public async Task<PagedResult<VoucherListItem>> ListAsync(Guid companyId, VoucherQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await guard.RequireAsync(companyId, Permission.VoucherView, cancellationToken);
        var (page, pageSize) = Paging.Normalize(query.Page, query.PageSize);

        var vouchers = db.Vouchers.AsNoTracking().Where(v => v.CompanyId == companyId && !v.IsDeleted);
        if (query.From is { } from)
        {
            vouchers = vouchers.Where(v => v.Date >= from);
        }

        if (query.To is { } to)
        {
            vouchers = vouchers.Where(v => v.Date <= to);
        }

        if (query.VoucherTypeId is { } typeId)
        {
            vouchers = vouchers.Where(v => v.VoucherTypeId == typeId);
        }

        if (query.Status is { } status)
        {
            vouchers = vouchers.Where(v => v.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            vouchers = vouchers.Where(v =>
                (v.Number != null && v.Number.Contains(term))
                || (v.Reference != null && v.Reference.Contains(term))
                || (v.Narration != null && v.Narration.Contains(term)));
        }

        var total = await vouchers.LongCountAsync(cancellationToken);
        var rows = await vouchers
            .OrderByDescending(v => v.Date).ThenByDescending(v => v.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(v => new { v.Id, v.Date, v.Number, v.VoucherTypeId, v.BaseType, v.Status, v.TotalMinor, v.Narration, v.ReversalOfVoucherId, v.Version })
            .ToListAsync(cancellationToken);

        var particulars = await VoucherReadHelpers.FirstLineParticularsAsync(db, companyId, rows.Select(r => r.Id).ToList(), cancellationToken);
        var typeNames = await VoucherReadHelpers.VoucherTypeNamesAsync(db, companyId, cancellationToken);
        var items = rows.Select(r => new VoucherListItem(
                r.Id, r.Date, r.Number, typeNames.GetValueOrDefault(r.VoucherTypeId, "?"), r.BaseType, r.Status,
                particulars.TryGetValue(r.Id, out var p) ? p.Name : string.Empty, Amounts.ToDecimal(r.TotalMinor), r.Narration,
                r.ReversalOfVoucherId is not null, r.Version))
            .ToList();
        return new PagedResult<VoucherListItem>(items, page, pageSize, total);
    }

    private async Task PostInternalAsync(ICompanyWriteScope scope, Company company, Voucher voucher, Guid userId, CancellationToken cancellationToken)
    {
        // Validate everything that can fail before consuming a number, so failed posts leave no gaps.
        company.EnsureDateIsOpen(voucher.Date);
        voucher.EnsurePostable();
        var number = await allocator.AllocateAsync(scope, voucher, cancellationToken);
        voucher.Post(company, number, userId, clock.GetUtcNow().UtcDateTime);
    }

    private async Task<Guid?> FindReplayAsync(Guid companyId, Guid? id, string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (id is { } voucherId)
        {
            var existing = await db.Vouchers.AsNoTracking().Where(v => v.Id == voucherId).Select(v => new { v.CompanyId }).SingleOrDefaultAsync(cancellationToken);
            if (existing is not null)
            {
                return existing.CompanyId == companyId
                    ? voucherId
                    : throw new ConflictException("voucher.id_conflict", "A voucher with this identifier already exists.");
            }
        }

        if (idempotencyKey is not null)
        {
            var existingId = await db.Vouchers.AsNoTracking()
                .Where(v => v.CompanyId == companyId && v.IdempotencyKey == idempotencyKey)
                .Select(v => (Guid?)v.Id)
                .SingleOrDefaultAsync(cancellationToken);
            if (existingId is not null)
            {
                return existingId;
            }
        }

        return null;
    }

    private async Task<Company> LoadCompanyAsync(Guid companyId, CancellationToken cancellationToken) =>
        await db.Companies.AsNoTracking().SingleOrDefaultAsync(c => c.Id == companyId && !c.IsDeleted, cancellationToken)
        ?? throw new NotFoundException("company");

    private async Task<Voucher> LoadVoucherAsync(Guid companyId, Guid voucherId, CancellationToken cancellationToken) =>
        await db.Vouchers.Include(v => v.Lines).SingleOrDefaultAsync(v => v.CompanyId == companyId && v.Id == voucherId && !v.IsDeleted, cancellationToken)
        ?? throw new NotFoundException("voucher");

    private static List<VoucherLineSpec> ParseLines(IReadOnlyList<VoucherLineInput>? lines)
    {
        if (lines is null || lines.Count == 0)
        {
            throw new ValidationFailedException("voucher.no_lines", "Enter at least one line.");
        }

        return lines.Select((l, i) => new VoucherLineSpec(l.LedgerId, l.Side, Amounts.Parse(l.Amount, $"Lines[{i}].Amount"), l.Narration)).ToList();
    }

    private static string Describe(VoucherType type, Voucher voucher) =>
        $"{type.Name} {voucher.Number ?? "(draft)"} dated {voucher.Date:yyyy-MM-dd} for {Amounts.ToDecimal(voucher.TotalMinor):0.00}";
}
