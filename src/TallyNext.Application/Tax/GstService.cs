using Microsoft.EntityFrameworkCore;
using TallyNext.Application.Abstractions;
using TallyNext.Application.Common;
using TallyNext.Application.Companies;
using TallyNext.Application.Security;
using TallyNext.Application.Sync;
using TallyNext.Contracts.Companies;
using TallyNext.Domain.Accounts;
using TallyNext.Domain.Common;
using TallyNext.Domain.Companies;
using TallyNext.Domain.Inventory;
using TallyNext.Domain.Tax;
using TallyNext.Domain.Vouchers;

namespace TallyNext.Application.Tax;

/// <summary>Company GST configuration and the duty ledgers invoices post tax to.</summary>
public sealed class GstService(IAccountingDbContext db, IAccessGuard guard, IAuditTrail audit)
{
    public async Task<CompanyDto> SetRegistrationAsync(Guid companyId, SetGstRegistrationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await guard.RequireAsync(companyId, Permission.CompanyManage, cancellationToken);
        await using var scope = await db.BeginCompanyWriteAsync(companyId, cancellationToken);
        var company = await db.Companies.SingleOrDefaultAsync(c => c.Id == companyId && !c.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("company");

        var before = SnapshotMapper.ToSnapshot(company);
        company.SetGstRegistration(request.Registration);
        audit.Record(companyId, "company.gst_registration_changed", "company", companyId, $"GST registration set to {request.Registration}", before, SnapshotMapper.ToSnapshot(company));
        if (request.CreateTaxLedgers && request.Registration != GstRegistrationType.NotApplicable)
        {
            await AddTaxLedgersAsync(companyId, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return CompanyService.ToDto(company);
    }

    /// <summary>
    /// Ensures the CGST, SGST, UTGST, IGST, Cess and Round Off ledgers exist. A ledger already named like one of
    /// them under Duties &amp; Taxes is adopted (its tax type is set) instead of creating a duplicate.
    /// </summary>
    public async Task EnsureTaxLedgersAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        await guard.RequireAsync(companyId, Permission.MasterEdit, cancellationToken);
        await using var scope = await db.BeginCompanyWriteAsync(companyId, cancellationToken);
        await AddTaxLedgersAsync(companyId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await scope.CommitAsync(cancellationToken);
    }

    /// <summary>Duty ledger for each GST head and the round-off ledger of a company (null where missing).</summary>
    public static async Task<(InvoiceTaxLedgers Taxes, Guid? RoundOff)> LoadTaxLedgersAsync(IAccountingDbContext db, Guid companyId, CancellationToken cancellationToken)
    {
        var ledgers = await db.Ledgers.AsNoTracking()
            .Where(l => l.CompanyId == companyId && !l.IsDeleted && (l.GstTaxType != GstTaxType.None || l.SystemKey == DefaultTaxLedgers.RoundOffKey))
            .Select(l => new { l.Id, l.GstTaxType, l.SystemKey })
            .ToListAsync(cancellationToken);
        Guid? Of(GstTaxType type) => ledgers.Where(l => l.GstTaxType == type).OrderBy(l => l.Id).Select(l => (Guid?)l.Id).FirstOrDefault();
        return (
            new InvoiceTaxLedgers(Of(GstTaxType.Cgst), Of(GstTaxType.Sgst), Of(GstTaxType.Utgst), Of(GstTaxType.Igst), Of(GstTaxType.Cess)),
            ledgers.Where(l => l.SystemKey == DefaultTaxLedgers.RoundOffKey).Select(l => (Guid?)l.Id).FirstOrDefault());
    }

    private async Task AddTaxLedgersAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var groups = await db.AccountGroups.Where(g => g.CompanyId == companyId && !g.IsDeleted).ToListAsync(cancellationToken);
        var duties = groups.FirstOrDefault(g => g.SystemKey == SystemGroupKeys.DutiesAndTaxes)
            ?? throw new ConflictException("gst.duties_group_missing", "The Duties & Taxes group is missing.");
        var indirect = groups.FirstOrDefault(g => g.SystemKey == SystemGroupKeys.IndirectExpenses)
            ?? throw new ConflictException("gst.indirect_group_missing", "The Indirect Expenses group is missing.");
        var hierarchy = new GroupHierarchy(groups);
        var ledgers = await db.Ledgers.Where(l => l.CompanyId == companyId && !l.IsDeleted).ToListAsync(cancellationToken);

        // Adopt existing ledgers by name before creating anything.
        var names = new Dictionary<string, GstTaxType>(StringComparer.Ordinal)
        {
            ["CGST"] = GstTaxType.Cgst, ["SGST"] = GstTaxType.Sgst, ["UTGST"] = GstTaxType.Utgst, ["IGST"] = GstTaxType.Igst, ["GST CESS"] = GstTaxType.Cess,
        };
        foreach (var ledger in ledgers.Where(l => l.GstTaxType == GstTaxType.None && names.ContainsKey(l.NormalizedName)))
        {
            var type = names[ledger.NormalizedName];
            if (ledgers.Any(l => l.GstTaxType == type) || !hierarchy.HasAncestorWithKey(ledger.GroupId, SystemGroupKeys.DutiesAndTaxes))
            {
                continue;
            }

            var before = SnapshotMapper.ToSnapshot(ledger);
            ledger.Update(hierarchy.Get(ledger.GroupId), ledger.Name, ledger.OpeningBalance, ledger.OpeningSide, ledger.GetDetails() with { GstTaxType = type, GstRates = [] });
            audit.Record(companyId, "ledger.updated", "ledger", ledger.Id, $"Ledger '{ledger.Name}' set as {type} ledger", before, SnapshotMapper.ToSnapshot(ledger));
        }

        foreach (var created in DefaultTaxLedgers.Build(duties, indirect, ledgers))
        {
            if (ledgers.Any(l => l.NormalizedName == created.NormalizedName))
            {
                throw new ConflictException("gst.ledger_name_taken", $"A ledger named '{created.Name}' already exists outside Duties & Taxes. Rename it first.");
            }

            db.Ledgers.Add(created);
            audit.Record(companyId, "ledger.created", "ledger", created.Id, $"Ledger '{created.Name}' created for GST", after: SnapshotMapper.ToSnapshot(created));
        }
    }
}
