using Microsoft.EntityFrameworkCore;
using TallyNext.Domain.Accounts;
using TallyNext.Domain.Audit;
using TallyNext.Domain.Companies;
using TallyNext.Domain.Vouchers;

namespace TallyNext.Application.Abstractions;

/// <summary>
/// The accounting store used by every use case. Implemented by the server database (PostgreSQL) and by
/// the device database (SQLite); the same application code runs against both.
/// </summary>
/// <remarks>
/// The application layer deliberately depends on EF Core abstractions (not on a provider): queries are
/// expressed once in LINQ and translated by each provider. Provider-specific behaviour (locking, change
/// log vs. outbox, audit sealing) lives in the infrastructure implementations.
/// </remarks>
public interface IAccountingDbContext
{
    DbSet<Company> Companies { get; }

    DbSet<CompanyMembership> CompanyMemberships { get; }

    DbSet<AccountGroup> AccountGroups { get; }

    DbSet<Ledger> Ledgers { get; }

    DbSet<VoucherType> VoucherTypes { get; }

    DbSet<Voucher> Vouchers { get; }

    DbSet<VoucherLine> VoucherLines { get; }

    DbSet<VoucherNumberSeries> VoucherNumberSeries { get; }

    DbSet<AuditEntry> AuditEntries { get; }

    /// <summary>
    /// Starts a transaction that holds the company's write lock until it is committed or disposed.
    /// All writes for a company (including voucher-number allocation, version assignment and the audit
    /// chain) are serialized by this lock, which is what makes numbering and versioning race-free.
    /// Load the data you validate against <em>after</em> acquiring the scope.
    /// </summary>
    Task<ICompanyWriteScope> BeginCompanyWriteAsync(Guid companyId, CancellationToken cancellationToken = default);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public interface ICompanyWriteScope : IAsyncDisposable
{
    Guid CompanyId { get; }

    Task CommitAsync(CancellationToken cancellationToken = default);
}
