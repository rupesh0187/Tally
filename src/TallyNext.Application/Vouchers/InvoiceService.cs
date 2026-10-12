using Microsoft.EntityFrameworkCore;
using TallyNext.Application.Abstractions;
using TallyNext.Application.Common;
using TallyNext.Application.Security;
using TallyNext.Application.Sync;
using TallyNext.Application.Tax;
using TallyNext.Contracts.Vouchers;
using TallyNext.Domain.Accounts;
using TallyNext.Domain.Common;
using TallyNext.Domain.Companies;
using TallyNext.Domain.Inventory;
using TallyNext.Domain.Tax;
using TallyNext.Domain.Vouchers;

namespace TallyNext.Application.Vouchers;

/// <summary>
/// Sales, purchase, credit-note and debit-note invoices with item/service lines. Place of supply, GST and the
/// double-entry postings are derived here; the user enters party, items, quantities and rates.
/// </summary>
/// <remarks>
/// GST is charged when the company is a regular taxpayer and, for purchases, when the supplier is a regular
/// taxpayer. Supplies to overseas and SEZ customers are treated as zero-rated under LUT/bond (no tax on the invoice);
/// exports with payment of IGST are not yet supported. Reverse charge is not yet supported.
/// </remarks>
public sealed class InvoiceService(
    IAccountingDbContext db,
    IAccessGuard guard,
    IAuditTrail audit,
    IExecutionContext context,
    VoucherPostingRules rules,
    VoucherNumberAllocator allocator,
    VoucherService vouchers,
    TimeProvider clock)
{
    private sealed record Prepared(Company Company, VoucherType Type, Ledger Party, string? PlaceOfSupply, SupplyNature Nature, bool ChargeTax, InvoiceDraft Draft);

    public async Task<InvoicePreview> PreviewAsync(Guid companyId, InvoiceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await guard.RequireAsync(companyId, Permission.VoucherCreate, cancellationToken);
        var prepared = await PrepareAsync(companyId, request, cancellationToken);
        var draft = prepared.Draft;
        return new InvoicePreview(
            prepared.Nature,
            prepared.PlaceOfSupply,
            prepared.ChargeTax,
            draft.ItemLines.Select(l => new InvoicePreviewLine(l.Taxable.ToDecimal(), l.Rate.IgstPercent, l.Rate.HsnSac, l.Tax.Total.ToDecimal())).ToList(),
            draft.Taxable.ToDecimal(),
            draft.Cgst.ToDecimal(),
            draft.StateTax.ToDecimal(),
            draft.Igst.ToDecimal(),
            draft.Cess.ToDecimal(),
            draft.RoundOff.ToDecimal(),
            draft.GrandTotal.ToDecimal());
    }

    public async Task<VoucherDto> CreateAsync(Guid companyId, InvoiceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await guard.RequireAsync(companyId, Permission.VoucherCreate, cancellationToken);
        var userId = context.RequireUserId();
        var key = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? null : request.IdempotencyKey.Trim();

        await using var scope = await db.BeginCompanyWriteAsync(companyId, cancellationToken);
        var replay = await FindReplayAsync(companyId, request.Id, key, cancellationToken);
        if (replay is not null)
        {
            return await vouchers.GetAsync(companyId, replay.Value, cancellationToken);
        }

        var prepared = await PrepareAsync(companyId, request, cancellationToken);
        var voucher = Voucher.CreateDraft(
            request.Id ?? Ids.New(),
            prepared.Company,
            prepared.Type,
            request.Date,
            new VoucherHeader(request.Narration, request.Reference, request.ReferenceDate),
            prepared.Draft.AccountingLines,
            userId,
            context.DeviceId,
            key);
        voucher.SetInvoiceDetails(prepared.Party.Id, prepared.PlaceOfSupply, prepared.Nature, prepared.Draft.ItemLines);
        await rules.ValidateAsync(voucher, forPosting: request.Post, cancellationToken);
        if (request.Post)
        {
            await PostAsync(scope, prepared.Company, voucher, userId, cancellationToken);
        }

        db.Vouchers.Add(voucher);
        audit.Record(companyId, request.Post ? "voucher.posted" : "voucher.draft_saved", "voucher", voucher.Id, Describe(prepared, voucher), after: SnapshotMapper.ToSnapshot(voucher));
        await db.SaveChangesAsync(cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return await vouchers.GetAsync(companyId, voucher.Id, cancellationToken);
    }

    public async Task<VoucherDto> UpdateDraftAsync(Guid companyId, Guid voucherId, InvoiceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await guard.RequireAsync(companyId, Permission.VoucherCreate, cancellationToken);
        var userId = context.RequireUserId();

        await using var scope = await db.BeginCompanyWriteAsync(companyId, cancellationToken);
        var voucher = await db.Vouchers.Include(v => v.Lines).Include(v => v.ItemLines)
            .SingleOrDefaultAsync(v => v.CompanyId == companyId && v.Id == voucherId && !v.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("voucher");
        if (request.ExpectedVersion is { } expected && expected != voucher.Version)
        {
            throw new ConflictException("voucher.version_conflict", "The voucher was changed by someone else. Reload and try again.", voucher.Version);
        }

        if (request.VoucherTypeId != voucher.VoucherTypeId)
        {
            throw new ValidationFailedException("voucher.type_immutable", "The voucher type of a saved voucher cannot be changed.");
        }

        var prepared = await PrepareAsync(companyId, request, cancellationToken);
        var before = SnapshotMapper.ToSnapshot(voucher);
        voucher.UpdateDraft(prepared.Company, request.Date, new VoucherHeader(request.Narration, request.Reference, request.ReferenceDate), prepared.Draft.AccountingLines);
        voucher.SetInvoiceDetails(prepared.Party.Id, prepared.PlaceOfSupply, prepared.Nature, prepared.Draft.ItemLines);
        await rules.ValidateAsync(voucher, forPosting: request.Post, cancellationToken);
        if (request.Post)
        {
            await PostAsync(scope, prepared.Company, voucher, userId, cancellationToken);
        }

        audit.Record(companyId, request.Post ? "voucher.posted" : "voucher.draft_updated", "voucher", voucher.Id, Describe(prepared, voucher), before, SnapshotMapper.ToSnapshot(voucher));
        await db.SaveChangesAsync(cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return await vouchers.GetAsync(companyId, voucher.Id, cancellationToken);
    }

    private async Task<Prepared> PrepareAsync(Guid companyId, InvoiceRequest request, CancellationToken cancellationToken)
    {
        var company = await db.Companies.AsNoTracking().SingleOrDefaultAsync(c => c.Id == companyId && !c.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("company");
        var type = await db.VoucherTypes.AsNoTracking().SingleOrDefaultAsync(t => t.CompanyId == companyId && t.Id == request.VoucherTypeId && !t.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("voucher_type");
        if (!InvoiceBuilder.IsInvoiceType(type.BaseType))
        {
            throw new ValidationFailedException("invoice.type_invalid", "Invoices are Sales, Purchase, Credit Note or Debit Note vouchers.");
        }

        var party = await db.Ledgers.AsNoTracking().SingleOrDefaultAsync(l => l.CompanyId == companyId && l.Id == request.PartyLedgerId && !l.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("party_ledger");
        if (request.Lines.Count == 0)
        {
            throw new ValidationFailedException("invoice.no_lines", "Add at least one item or service line.");
        }

        // Place of supply: outward supplies default to the customer's State, inward supplies to our own State.
        var outward = type.BaseType is VoucherBaseType.Sales or VoucherBaseType.CreditNote;
        var supplierState = outward ? company.StateCode : party.StateCode ?? company.StateCode;
        var placeOfSupply = string.IsNullOrWhiteSpace(request.PlaceOfSupplyStateCode)
            ? (outward ? party.StateCode ?? company.StateCode : company.StateCode)
            : request.PlaceOfSupplyStateCode.Trim();
        var zeroRated = outward && party.GstRegistrationType is GstRegistrationType.Overseas or GstRegistrationType.SpecialEconomicZone;
        var chargeTax = company.ChargesGst && !zeroRated && (outward || party.GstRegistrationType == GstRegistrationType.Regular);
        var nature = chargeTax || company.GstRegistration != GstRegistrationType.NotApplicable
            ? PlaceOfSupply.Determine(supplierState, placeOfSupply, party.GstRegistrationType)
            : SupplyNature.IntraState;

        var itemIds = request.Lines.Where(l => l.StockItemId is not null).Select(l => l.StockItemId!.Value).Distinct().ToList();
        var items = await db.StockItems.AsNoTracking().Where(i => i.CompanyId == companyId && itemIds.Contains(i.Id) && !i.IsDeleted).ToDictionaryAsync(i => i.Id, cancellationToken);
        var unitIds = items.Values.Select(i => i.UnitId).Distinct().ToList();
        var units = await db.Units.AsNoTracking().Where(u => unitIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, cancellationToken);
        var ledgerIds = request.Lines.Where(l => l.LedgerId is not null).Select(l => l.LedgerId!.Value).Distinct().ToList();
        var ledgers = await db.Ledgers.AsNoTracking().Where(l => l.CompanyId == companyId && ledgerIds.Contains(l.Id) && !l.IsDeleted).ToDictionaryAsync(l => l.Id, cancellationToken);
        var defaultLedger = await DefaultItemLedgerAsync(companyId, type.BaseType, cancellationToken);
        var mainGodown = await db.Godowns.AsNoTracking()
            .Where(g => g.CompanyId == companyId && !g.IsDeleted)
            .OrderByDescending(g => g.IsSystem).ThenBy(g => g.NormalizedName)
            .Select(g => (Guid?)g.Id)
            .FirstOrDefaultAsync(cancellationToken);

        var inputs = new List<InvoiceLineInput>(request.Lines.Count);
        var lineNo = 0;
        foreach (var line in request.Lines)
        {
            lineNo++;
            var field = $"Lines[{lineNo - 1}]";
            Guid ledgerId;
            if (line.LedgerId is { } explicitLedger)
            {
                if (!ledgers.ContainsKey(explicitLedger))
                {
                    throw new ValidationFailedException("invoice.line_ledger_invalid", $"Line {lineNo}: the ledger was not found.");
                }

                ledgerId = explicitLedger;
            }
            else
            {
                ledgerId = defaultLedger ?? throw new ValidationFailedException(
                    "invoice.default_ledger_missing",
                    $"Create a ledger under {(outward ? "Sales" : "Purchase")} Accounts, or choose the ledger on line {lineNo}.");
            }

            GstRateEntry? rate;
            long quantity;
            if (line.Kind == InvoiceLineKind.Stock)
            {
                if (line.StockItemId is not { } itemId || !items.TryGetValue(itemId, out var item))
                {
                    throw new ValidationFailedException("invoice.item_not_found", $"Line {lineNo}: choose a stock item.");
                }

                quantity = Parse(() => StockMath.ToQuantityMilli(line.Quantity, units.GetValueOrDefault(item.UnitId)?.DecimalPlaces ?? 0), $"{field}.Quantity");
                rate = item.GstRateOn(request.Date);
                if (rate is null && chargeTax)
                {
                    throw new ValidationFailedException("gst.rate_missing", $"Line {lineNo}: '{item.Name}' has no GST rate on {request.Date:dd-MMM-yyyy}. Set it on the stock item.");
                }
            }
            else
            {
                quantity = line.Quantity <= 0 ? 0 : Parse(() => StockMath.ToQuantityMilli(line.Quantity, 3), $"{field}.Quantity");
                var ledger = ledgers.GetValueOrDefault(ledgerId);
                rate = ledger?.GstRateOn(request.Date);
                if (rate is null && chargeTax)
                {
                    throw new ValidationFailedException("gst.rate_missing", $"Line {lineNo}: set the GST rate on the ledger used for this service line.");
                }
            }

            var rateScaled = Parse(() => StockMath.ToRateScaled(line.Rate), $"{field}.Rate");
            var discount = Parse(() => GstRateEntry.ToMilliPercent(line.DiscountPercent, "discount"), $"{field}.DiscountPercent");
            var amount = line.Kind == InvoiceLineKind.Service && line.Amount is { } a ? Amounts.Parse(a, $"{field}.Amount") : (Money?)null;
            inputs.Add(new InvoiceLineInput(
                null,
                line.Kind,
                line.StockItemId,
                line.Kind == InvoiceLineKind.Stock ? line.GodownId ?? mainGodown : null,
                ledgerId,
                line.Description,
                quantity,
                rateScaled,
                discount,
                amount,
                rate ?? new GstRateEntry(request.Date, Taxability.NonGst, 0, 0, null)));
        }

        var (taxLedgers, roundOffLedger) = await GstService.LoadTaxLedgersAsync(db, companyId, cancellationToken);
        var draft = InvoiceBuilder.Build(type.BaseType, party.Id, nature, chargeTax, inputs, taxLedgers, request.RoundOff ? roundOffLedger : null);
        if (request.RoundOff && roundOffLedger is null && draft.GrandTotal.Minor % Money.MinorUnitsPerMajor != 0)
        {
            throw new ValidationFailedException("invoice.round_off_ledger_missing", "Create the Round Off ledger (Company settings > GST) or turn rounding off.");
        }

        return new Prepared(company, type, party, placeOfSupply, nature, chargeTax, draft);
    }

    private async Task<Guid?> DefaultItemLedgerAsync(Guid companyId, VoucherBaseType type, CancellationToken cancellationToken)
    {
        var key = type is VoucherBaseType.Sales or VoucherBaseType.CreditNote ? SystemGroupKeys.SalesAccounts : SystemGroupKeys.PurchaseAccounts;
        var groups = await db.AccountGroups.AsNoTracking().Where(g => g.CompanyId == companyId && !g.IsDeleted).ToListAsync(cancellationToken);
        var hierarchy = new GroupHierarchy(groups);
        var groupIds = groups.Where(g => hierarchy.HasAncestorWithKey(g.Id, key)).Select(g => g.Id).ToList();
        return await db.Ledgers.AsNoTracking()
            .Where(l => l.CompanyId == companyId && !l.IsDeleted && groupIds.Contains(l.GroupId))
            .OrderBy(l => l.NormalizedName)
            .Select(l => (Guid?)l.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task PostAsync(ICompanyWriteScope scope, Company company, Voucher voucher, Guid userId, CancellationToken cancellationToken)
    {
        company.EnsureDateIsOpen(voucher.Date);
        voucher.EnsurePostable();
        var number = await allocator.AllocateAsync(scope, voucher, cancellationToken);
        voucher.Post(company, number, userId, clock.GetUtcNow().UtcDateTime);
    }

    private async Task<Guid?> FindReplayAsync(Guid companyId, Guid? id, string? key, CancellationToken cancellationToken)
    {
        if (id is { } voucherId)
        {
            var owner = await db.Vouchers.AsNoTracking().Where(v => v.Id == voucherId).Select(v => (Guid?)v.CompanyId).SingleOrDefaultAsync(cancellationToken);
            if (owner is not null)
            {
                return owner == companyId ? voucherId : throw new ConflictException("voucher.id_conflict", "A voucher with this identifier already exists.");
            }
        }

        return key is null
            ? null
            : await db.Vouchers.AsNoTracking().Where(v => v.CompanyId == companyId && v.IdempotencyKey == key).Select(v => (Guid?)v.Id).SingleOrDefaultAsync(cancellationToken);
    }

    private static T Parse<T>(Func<T> parse, string field)
    {
        try
        {
            return parse();
        }
        catch (DomainException ex)
        {
            throw new ValidationFailedException(ex.Code, ex.Message, new Dictionary<string, string[]> { [field] = [ex.Message] });
        }
    }

    private static string Describe(Prepared prepared, Voucher voucher) =>
        $"{prepared.Type.Name} {voucher.Number ?? "(draft)"} to {prepared.Party.Name} for {Amounts.ToDecimal(voucher.TotalMinor):0.00} (tax {prepared.Draft.TotalTax.ToDecimal():0.00})";
}
