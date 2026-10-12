using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using TallyNext.Application.Abstractions;
using TallyNext.Application.Sync;
using TallyNext.Contracts.Sync;
using TallyNext.Domain.Accounts;
using TallyNext.Domain.Audit;
using TallyNext.Domain.Companies;
using TallyNext.Domain.Vouchers;
using TallyNext.Infrastructure.Persistence;

namespace TallyNext.Infrastructure.Server.Persistence;

/// <summary>Origin of the changes being saved in the current scope (set while applying a device upload).</summary>
public sealed class SyncOriginContext
{
    public Guid? DeviceId { get; set; }

    public Guid? ChangeId { get; set; }
}

/// <summary>
/// The central database (PostgreSQL in every deployed environment). Every save of a replicated entity, from any
/// entry point, assigns the next per-company version under the company write lock and appends the full snapshot
/// to the change log in the same transaction - which is what devices download.
/// </summary>
public sealed class ServerDbContext : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>, IAccountingDbContext
{
    private readonly IExecutionContext? _context;
    private readonly SyncOriginContext? _origin;
    private readonly TimeProvider _clock;
    private readonly HashSet<Guid> _lockedCompanies = [];

    public ServerDbContext(DbContextOptions<ServerDbContext> options, IExecutionContext? context = null, SyncOriginContext? origin = null, TimeProvider? clock = null)
        : base(options)
    {
        _context = context;
        _origin = origin;
        _clock = clock ?? TimeProvider.System;
    }

    public DbSet<Company> Companies => Set<Company>();

    public DbSet<CompanyMembership> CompanyMemberships => Set<CompanyMembership>();

    public DbSet<AccountGroup> AccountGroups => Set<AccountGroup>();

    public DbSet<Ledger> Ledgers => Set<Ledger>();

    public DbSet<VoucherType> VoucherTypes => Set<VoucherType>();

    public DbSet<Voucher> Vouchers => Set<Voucher>();

    public DbSet<VoucherLine> VoucherLines => Set<VoucherLine>();

    public DbSet<VoucherNumberSeries> VoucherNumberSeries => Set<VoucherNumberSeries>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    public DbSet<Device> Devices => Set<Device>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<CompanySyncState> CompanySyncStates => Set<CompanySyncState>();

    public DbSet<SyncChangeRecord> SyncChanges => Set<SyncChangeRecord>();

    public DbSet<ProcessedChange> ProcessedChanges => Set<ProcessedChange>();

    public DbSet<SyncConflictRecord> SyncConflicts => Set<SyncConflictRecord>();

    public DbSet<DeviceSeriesCounter> DeviceSeriesCounters => Set<DeviceSeriesCounter>();

    public async Task<ICompanyWriteScope> BeginCompanyWriteAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        IDbContextTransaction? owned = null;
        if (Database.CurrentTransaction is null)
        {
            owned = await Database.BeginTransactionAsync(cancellationToken);
        }

        try
        {
            await LockCompanyAsync(companyId, cancellationToken);
        }
        catch
        {
            if (owned is not null)
            {
                await owned.DisposeAsync();
                _lockedCompanies.Clear();
            }

            throw;
        }

        return new ServerWriteScope(this, companyId, owned);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        throw new InvalidOperationException("Use SaveChangesAsync: saves must run the change-log and audit pipeline.");

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var changes = SavePipeline.CollectSyncChanges(this);
        var hasAudit = ChangeTracker.Entries<AuditEntry>().Any(e => e.State == EntityState.Added);
        if (changes.Count == 0 && !hasAudit)
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        var owned = Database.CurrentTransaction is null ? await Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            SavePipeline.Stamp(changes, now, _context?.UserId);
            await AppendChangeLogAsync(changes, now, cancellationToken);
            await SavePipeline.SealAuditEntriesAsync(this, cancellationToken);
            var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
            if (owned is not null)
            {
                await owned.CommitAsync(cancellationToken);
            }

            return result;
        }
        finally
        {
            if (owned is not null)
            {
                await owned.DisposeAsync();
                _lockedCompanies.Clear();
            }
        }
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        AccountingModel.ConfigureConventions(configurationBuilder);

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        AccountingModel.Configure(builder);

        builder.Entity<AppUser>(b =>
        {
            b.Property(u => u.DisplayName).HasMaxLength(200);
        });

        builder.Entity<Device>(b =>
        {
            b.ToTable("Devices");
            b.HasKey(d => d.Id);
            b.Property(d => d.Id).ValueGeneratedNever();
            b.Property(d => d.Name).HasMaxLength(100).IsRequired();
            b.Property(d => d.Platform).HasMaxLength(50).IsRequired();
            b.Property(d => d.SeriesCode).HasMaxLength(8).IsRequired();
            b.HasIndex(d => d.SeriesCode).IsUnique();
            b.HasIndex(d => d.RegisteredByUserId);
        });

        builder.Entity<RefreshToken>(b =>
        {
            b.ToTable("RefreshTokens");
            b.HasKey(t => t.Id);
            b.Property(t => t.Id).ValueGeneratedNever();
            b.Property(t => t.TokenHash).HasMaxLength(64).IsRequired();
            b.Property(t => t.RevokedReason).HasMaxLength(100);
            b.HasIndex(t => t.TokenHash).IsUnique();
            b.HasIndex(t => new { t.UserId, t.DeviceId });
            b.HasIndex(t => t.FamilyId);
        });

        builder.Entity<CompanySyncState>(b =>
        {
            b.ToTable("CompanySyncStates");
            b.HasKey(s => s.CompanyId);
            b.Property(s => s.CompanyId).ValueGeneratedNever();
        });

        builder.Entity<SyncChangeRecord>(b =>
        {
            b.ToTable("SyncChanges");
            b.HasKey(c => new { c.CompanyId, c.Sequence });
            b.Property(c => c.EntityType).HasMaxLength(50).IsRequired();
            b.Property(c => c.PayloadJson).IsRequired();
            b.HasIndex(c => new { c.CompanyId, c.EntityType, c.EntityId });
        });

        builder.Entity<ProcessedChange>(b =>
        {
            b.ToTable("ProcessedChanges");
            b.HasKey(p => new { p.DeviceId, p.ChangeId });
            b.Property(p => p.PayloadHash).HasMaxLength(64).IsRequired();
            b.Property(p => p.ResultJson).IsRequired();
        });

        builder.Entity<SyncConflictRecord>(b =>
        {
            b.ToTable("SyncConflicts");
            b.HasKey(c => c.Id);
            b.Property(c => c.Id).ValueGeneratedNever();
            b.Property(c => c.EntityType).HasMaxLength(50).IsRequired();
            b.Property(c => c.Reason).HasMaxLength(200).IsRequired();
            b.Property(c => c.ResolutionNote).HasMaxLength(1000);
            b.HasIndex(c => new { c.CompanyId, c.Status });
        });

        builder.Entity<DeviceSeriesCounter>(b =>
        {
            b.ToTable("DeviceSeriesCounters");
            b.HasKey(c => c.Id);
            b.Property(c => c.Id).ValueGeneratedNever();
        });
    }

    /// <summary>
    /// Takes the company write lock (row lock on PostgreSQL held until commit; SQLite write transactions are already
    /// exclusive). The lock row is created on first use with INSERT .. ON CONFLICT DO NOTHING.
    /// </summary>
    internal async Task LockCompanyAsync(Guid companyId, CancellationToken cancellationToken)
    {
        if (_lockedCompanies.Contains(companyId))
        {
            return;
        }

        if (Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("The company write lock can only be taken inside a transaction.");
        }

        await Database.ExecuteSqlAsync(
            $"INSERT INTO \"CompanySyncStates\" (\"CompanyId\", \"LastSequence\") VALUES ({companyId}, {0L}) ON CONFLICT DO NOTHING",
            cancellationToken);
        await Database.ExecuteSqlAsync(
            $"UPDATE \"CompanySyncStates\" SET \"LastSequence\" = \"LastSequence\" WHERE \"CompanyId\" = {companyId}",
            cancellationToken);
        _lockedCompanies.Add(companyId);
    }

    private async Task AppendChangeLogAsync(IReadOnlyList<PendingSyncChange> changes, DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (changes.Count == 0)
        {
            return;
        }

        foreach (var companyId in changes.Select(c => c.Entity.CompanyId).Distinct().Order())
        {
            await LockCompanyAsync(companyId, cancellationToken);
            var last = await CompanySyncStates.AsNoTracking()
                .Where(s => s.CompanyId == companyId)
                .Select(s => s.LastSequence)
                .SingleAsync(cancellationToken);

            foreach (var change in changes.Where(c => c.Entity.CompanyId == companyId))
            {
                var sequence = ++last;
                SavePipeline.SetVersion(change.Entry, sequence);
                SyncChanges.Add(new SyncChangeRecord
                {
                    CompanyId = companyId,
                    Sequence = sequence,
                    EntityType = SnapshotMapper.EntityTypeOf(change.Entity),
                    EntityId = change.Entity.Id,
                    Operation = change.IsDeletion ? SyncOperation.Delete : SyncOperation.Upsert,
                    Version = sequence,
                    PayloadJson = SyncJson.Serialize(SnapshotMapper.ToSnapshot(change.Entity)),
                    OriginDeviceId = _origin?.DeviceId,
                    OriginChangeId = _origin?.ChangeId,
                    UserId = _context?.UserId,
                    ChangedAtUtc = nowUtc,
                });
            }

            await Database.ExecuteSqlAsync(
                $"UPDATE \"CompanySyncStates\" SET \"LastSequence\" = {last} WHERE \"CompanyId\" = {companyId}",
                cancellationToken);
        }
    }

    private sealed class ServerWriteScope(ServerDbContext owner, Guid companyId, IDbContextTransaction? transaction) : ICompanyWriteScope
    {
        public Guid CompanyId { get; } = companyId;

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                owner._lockedCompanies.Clear();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
                owner._lockedCompanies.Clear();
            }
        }
    }
}
