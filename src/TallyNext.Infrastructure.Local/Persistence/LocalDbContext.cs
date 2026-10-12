using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using TallyNext.Application.Abstractions;
using TallyNext.Application.Sync;
using TallyNext.Contracts.Sync;
using TallyNext.Domain.Accounts;
using TallyNext.Domain.Audit;
using TallyNext.Domain.Common;
using TallyNext.Domain.Companies;
using TallyNext.Domain.Vouchers;
using TallyNext.Infrastructure.Persistence;

namespace TallyNext.Infrastructure.Local.Persistence;

/// <summary>
/// The desktop database (SQLite). Every normal save also writes the outbox in the same transaction; changes
/// applied from the server are written inside <see cref="BeginRemoteApply"/>, which suppresses the outbox so
/// downloaded data is never echoed back (no sync loops).
/// </summary>
public sealed class LocalDbContext : DbContext, IAccountingDbContext
{
    private readonly IExecutionContext? _context;
    private readonly TimeProvider _clock;
    private int _remoteApplyDepth;

    public LocalDbContext(DbContextOptions<LocalDbContext> options, IExecutionContext? context = null, TimeProvider? clock = null)
        : base(options)
    {
        _context = context;
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

    public DbSet<OutboxEntry> OutboxEntries => Set<OutboxEntry>();

    public DbSet<SyncCursor> SyncCursors => Set<SyncCursor>();

    public DbSet<LocalConflict> LocalConflicts => Set<LocalConflict>();

    public DbSet<DeviceRegistration> DeviceRegistrations => Set<DeviceRegistration>();

    public DbSet<LocalUser> LocalUsers => Set<LocalUser>();

    public DbSet<ProtectedSecret> ProtectedSecrets => Set<ProtectedSecret>();

    public bool IsApplyingRemoteChanges => _remoteApplyDepth > 0;

    /// <summary>Within the returned scope, saves do not create outbox entries and keep the server's timestamps.</summary>
    public IDisposable BeginRemoteApply()
    {
        _remoteApplyDepth++;
        return new RemoteApplyScope(this);
    }

    /// <summary>
    /// SQLite has a single writer. <c>BeginTransaction</c> issues <c>BEGIN IMMEDIATE</c>, taking the write lock up
    /// front so that validation reads and the subsequent writes see a consistent database.
    /// </summary>
    public async Task<ICompanyWriteScope> BeginCompanyWriteAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        if (Database.CurrentTransaction is not null)
        {
            return new LocalWriteScope(companyId, null);
        }

        var transaction = await Database.BeginTransactionAsync(cancellationToken);
        return new LocalWriteScope(companyId, transaction);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        throw new InvalidOperationException("Use SaveChangesAsync: saves must run the outbox and audit pipeline.");

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
            if (!IsApplyingRemoteChanges)
            {
                var now = _clock.GetUtcNow().UtcDateTime;
                SavePipeline.Stamp(changes, now, _context?.UserId);
                await WriteOutboxAsync(changes, now, cancellationToken);
            }

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
            }
        }
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        AccountingModel.ConfigureConventions(configurationBuilder);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        AccountingModel.Configure(modelBuilder);

        // On the device these rules are enforced by the application services, not by unique indexes: between a
        // rejected upload and the user's decision, the device legitimately holds both its own row and the server's
        // (e.g. two ledgers named alike, or two reversals of one voucher). A unique index would make every later
        // download fail. The server's unique indexes remain the authority. See SYNC_DESIGN.md, "Conflict window".
        modelBuilder.Entity<AccountGroup>().HasIndex(g => new { g.CompanyId, g.NormalizedName }).IsUnique(false);
        modelBuilder.Entity<Ledger>().HasIndex(l => new { l.CompanyId, l.NormalizedName }).IsUnique(false);
        modelBuilder.Entity<VoucherType>().HasIndex(t => new { t.CompanyId, t.NormalizedName }).IsUnique(false);
        modelBuilder.Entity<Voucher>().HasIndex(v => new { v.CompanyId, v.VoucherTypeId, v.FinancialYearStart, v.SeriesCode, v.SequenceNumber }).IsUnique(false);
        modelBuilder.Entity<Voucher>().HasIndex(v => new { v.CompanyId, v.IdempotencyKey }).IsUnique(false);
        modelBuilder.Entity<Voucher>().HasIndex(v => v.ReversalOfVoucherId).IsUnique(false);

        modelBuilder.Entity<OutboxEntry>(b =>
        {
            b.ToTable("OutboxEntries");
            b.HasKey(e => e.LocalSequence);
            b.Property(e => e.LocalSequence).ValueGeneratedNever();
            b.Property(e => e.EntityType).HasMaxLength(50).IsRequired();
            b.Property(e => e.LastErrorCode).HasMaxLength(100);
            b.Property(e => e.LastError).HasMaxLength(1000);
            b.HasIndex(e => e.ChangeId).IsUnique();
            b.HasIndex(e => new { e.Status, e.LocalSequence });
            b.HasIndex(e => new { e.EntityId, e.Status });
        });

        modelBuilder.Entity<SyncCursor>(b =>
        {
            b.ToTable("SyncCursors");
            b.HasKey(c => c.CompanyId);
        });

        modelBuilder.Entity<LocalConflict>(b =>
        {
            b.ToTable("LocalConflicts");
            b.HasKey(c => c.Id);
            b.Property(c => c.EntityType).HasMaxLength(50).IsRequired();
            b.Property(c => c.Kind).HasMaxLength(30).IsRequired();
            b.Property(c => c.ErrorCode).HasMaxLength(100);
            b.Property(c => c.Message).HasMaxLength(1000);
            b.HasIndex(c => new { c.Status, c.CompanyId });
        });

        modelBuilder.Entity<DeviceRegistration>(b =>
        {
            b.ToTable("DeviceRegistration");
            b.HasKey(d => d.Id);
            b.Property(d => d.Id).ValueGeneratedNever();
            b.Property(d => d.DeviceName).HasMaxLength(100).IsRequired();
            b.Property(d => d.SeriesCode).HasMaxLength(8);
            b.Property(d => d.ServerUrl).HasMaxLength(500);
        });

        modelBuilder.Entity<LocalUser>(b =>
        {
            b.ToTable("LocalUsers");
            b.HasKey(u => u.UserId);
            b.Property(u => u.Email).HasMaxLength(254).IsRequired();
            b.Property(u => u.NormalizedEmail).HasMaxLength(254).IsRequired();
            b.Property(u => u.DisplayName).HasMaxLength(200);
            b.HasIndex(u => u.NormalizedEmail).IsUnique();
        });

        modelBuilder.Entity<ProtectedSecret>(b =>
        {
            b.ToTable("ProtectedSecrets");
            b.HasKey(s => s.Key);
            b.Property(s => s.Key).HasMaxLength(100);
        });
    }

    private async Task WriteOutboxAsync(IReadOnlyList<PendingSyncChange> changes, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var uploadable = changes.Where(c => SyncEntityTypes.Uploadable.Contains(SnapshotMapper.EntityTypeOf(c.Entity))).ToList();
        if (uploadable.Count == 0)
        {
            return;
        }

        var userId = _context?.UserId ?? throw new InvalidOperationException("A signed-in user is required to record local changes.");
        var trackedMax = OutboxEntries.Local.Select(e => e.LocalSequence).DefaultIfEmpty(0).Max();
        var storedMax = await OutboxEntries.MaxAsync(e => (long?)e.LocalSequence, cancellationToken) ?? 0;
        var next = Math.Max(trackedMax, storedMax);

        foreach (var change in uploadable)
        {
            var entityId = change.Entity.Id;
            var baseVersion = change.IsNew ? 0 : (long)change.Entry.OriginalValues[nameof(ISyncEntity.Version)]!;

            // An entry that has never been sent can be replaced: drop it and append the latest state at the end, so
            // the upload order still follows dependencies (e.g. a ledger moved into a group created afterwards).
            var unsent = await OutboxEntries
                .Where(e => e.EntityId == entityId && e.Status == OutboxStatus.Pending && e.Attempts == 0)
                .ToListAsync(cancellationToken);
            foreach (var stale in unsent)
            {
                baseVersion = stale.BaseVersion;
                OutboxEntries.Remove(stale);
            }

            OutboxEntries.Add(new OutboxEntry
            {
                LocalSequence = ++next,
                ChangeId = Ids.New(),
                CompanyId = change.Entity.CompanyId,
                EntityType = SnapshotMapper.EntityTypeOf(change.Entity),
                EntityId = entityId,
                Operation = change.IsDeletion ? SyncOperation.Delete : SyncOperation.Upsert,
                BaseVersion = baseVersion,
                SchemaVersion = SyncProtocol.SchemaVersion,
                PayloadJson = SyncJson.Serialize(SnapshotMapper.ToSnapshot(change.Entity)),
                UserId = userId,
                ClientModifiedAtUtc = nowUtc,
                CreatedAtUtc = nowUtc,
                Status = OutboxStatus.Pending,
            });
        }
    }

    private sealed class RemoteApplyScope(LocalDbContext owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                owner._remoteApplyDepth--;
            }
        }
    }

    private sealed class LocalWriteScope(Guid companyId, IDbContextTransaction? transaction) : ICompanyWriteScope
    {
        public Guid CompanyId { get; } = companyId;

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }
}
