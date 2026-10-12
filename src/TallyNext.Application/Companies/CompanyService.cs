using Microsoft.EntityFrameworkCore;
using TallyNext.Application.Abstractions;
using TallyNext.Application.Common;
using TallyNext.Application.Security;
using TallyNext.Application.Sync;
using TallyNext.Contracts.Companies;
using TallyNext.Domain.Accounts;
using TallyNext.Domain.Common;
using TallyNext.Domain.Companies;

namespace TallyNext.Application.Companies;

public sealed class CompanyService(
    IAccountingDbContext db,
    IAccessGuard guard,
    IAuditTrail audit,
    IExecutionContext context,
    TimeProvider clock)
{
    public static IReadOnlyList<StateDto> States { get; } =
        IndianStates.All.Select(s => new StateDto(s.Code, s.Name)).ToList();

    public static IReadOnlyList<CurrencyDto> SupportedCurrencies { get; } =
        Currencies.Supported.Select(c => new CurrencyDto(c.Code, c.Symbol, c.Name)).ToList();

    /// <summary>Companies the current user is an active member of.</summary>
    public async Task<IReadOnlyList<CompanySummaryDto>> ListMineAsync(CancellationToken cancellationToken = default)
    {
        var userId = context.RequireUserId();
        return await (
                from m in db.CompanyMemberships.AsNoTracking()
                join c in db.Companies.AsNoTracking() on m.CompanyId equals c.Id
                where m.UserId == userId && m.IsActive && !m.IsDeleted && !c.IsDeleted
                orderby c.Name
                select new CompanySummaryDto(c.Id, c.Name, c.StateCode, c.Gstin, c.CurrencyCode, m.Role, c.BooksBeginningDate, c.FinancialYearStartMonth))
            .ToListAsync(cancellationToken);
    }

    public async Task<CompanyDto> GetAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        await guard.RequireAsync(companyId, Permission.CompanyView, cancellationToken);
        var company = await db.Companies.AsNoTracking().SingleOrDefaultAsync(c => c.Id == companyId && !c.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("company");
        return ToDto(company);
    }

    /// <summary>
    /// Creates a company with the default chart of accounts and voucher types, and makes the creator its Owner.
    /// Re-sending the same <see cref="CreateCompanyRequest.Id"/> returns the existing company (idempotent).
    /// </summary>
    public async Task<CompanyDto> CreateAsync(CreateCompanyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var userId = context.RequireUserId();
        var id = request.Id ?? Ids.New();
        var booksBeginning = request.BooksBeginningDate == default
            ? FinancialYear.Containing(DateOnly.FromDateTime(clock.GetLocalNow().DateTime), request.FinancialYearStartMonth is >= 1 and <= 12 ? request.FinancialYearStartMonth : 4).Start
            : request.BooksBeginningDate;

        var company = Company.Create(id, ToProfile(request), request.CurrencyCode, request.FinancialYearStartMonth, booksBeginning, userId);

        await using var scope = await db.BeginCompanyWriteAsync(id, cancellationToken);
        var existing = await db.Companies.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (existing is not null)
        {
            if (existing.CreatedByUserId != userId || existing.IsDeleted)
            {
                throw new ConflictException("company.id_conflict", "A company with this identifier already exists.");
            }

            return ToDto(existing);
        }

        db.Companies.Add(company);
        db.CompanyMemberships.Add(CompanyMembership.Create(id, userId, CompanyRole.Owner, context.UserEmail ?? string.Empty, context.UserDisplayName));
        var (groups, ledgers, voucherTypes) = DefaultChartOfAccounts.Build(id);
        db.AccountGroups.AddRange(groups);
        db.Ledgers.AddRange(ledgers);
        db.VoucherTypes.AddRange(voucherTypes);

        audit.Record(id, "company.created", "company", id, $"Company '{company.Name}' created", after: SnapshotMapper.ToSnapshot(company));
        await db.SaveChangesAsync(cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return ToDto(company);
    }

    public async Task<CompanyDto> UpdateAsync(Guid companyId, UpdateCompanyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await guard.RequireAsync(companyId, Permission.CompanyManage, cancellationToken);

        await using var scope = await db.BeginCompanyWriteAsync(companyId, cancellationToken);
        var company = await LoadForWriteAsync(companyId, request.ExpectedVersion, cancellationToken);
        var before = SnapshotMapper.ToSnapshot(company);
        company.UpdateProfile(ToProfile(request));
        audit.Record(companyId, "company.updated", "company", companyId, $"Company '{company.Name}' updated", before, SnapshotMapper.ToSnapshot(company));
        await db.SaveChangesAsync(cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return ToDto(company);
    }

    public async Task<CompanyDto> SetPeriodLockAsync(Guid companyId, SetPeriodLockRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await guard.RequireAsync(companyId, Permission.PeriodLock, cancellationToken);

        await using var scope = await db.BeginCompanyWriteAsync(companyId, cancellationToken);
        var company = await LoadForWriteAsync(companyId, null, cancellationToken);
        var before = SnapshotMapper.ToSnapshot(company);
        company.SetLockedThrough(request.LockedThroughDate);
        audit.Record(
            companyId,
            "company.period_lock_changed",
            "company",
            companyId,
            request.LockedThroughDate is { } d ? $"Books locked through {d:yyyy-MM-dd}" : "Period lock removed",
            before,
            SnapshotMapper.ToSnapshot(company));
        await db.SaveChangesAsync(cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return ToDto(company);
    }

    internal static CompanyDto ToDto(Company c) => new(
        c.Id, c.Name, c.MailingName, c.AddressLine1, c.AddressLine2, c.City, c.StateCode, c.Pincode, c.Country, c.Gstin, c.Pan,
        c.Email, c.Phone, c.CurrencyCode, c.CurrencySymbol, c.FinancialYearStartMonth, c.BooksBeginningDate, c.LockedThroughDate, c.Version);

    private static CompanyProfile ToProfile(CompanyProfileInput input) => new(
        input.Name, input.MailingName, input.AddressLine1, input.AddressLine2, input.City, input.StateCode, input.Pincode,
        string.IsNullOrWhiteSpace(input.Country) ? "India" : input.Country, input.Gstin, input.Pan, input.Email, input.Phone);

    private async Task<Company> LoadForWriteAsync(Guid companyId, long? expectedVersion, CancellationToken cancellationToken)
    {
        var company = await db.Companies.SingleOrDefaultAsync(c => c.Id == companyId && !c.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("company");
        if (expectedVersion is { } expected && expected != company.Version)
        {
            throw new ConflictException("company.version_conflict", "The company was changed by someone else. Reload and try again.", company.Version);
        }

        return company;
    }
}
