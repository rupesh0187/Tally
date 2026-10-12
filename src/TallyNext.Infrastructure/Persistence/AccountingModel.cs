using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TallyNext.Domain.Accounts;
using TallyNext.Domain.Audit;
using TallyNext.Domain.Common;
using TallyNext.Domain.Companies;
using TallyNext.Domain.Vouchers;

namespace TallyNext.Infrastructure.Persistence;

/// <summary>
/// The accounting schema shared by the server (PostgreSQL) and device (SQLite) databases.
/// Only provider-neutral features are used here: money is INTEGER minor units, dates are DATE/TEXT,
/// partial indexes use <c>"IsDeleted" = FALSE</c> which both SQLite (3.23+) and PostgreSQL accept.
/// Each provider has its own context and its own migrations; see DATABASE_DESIGN.md.
/// </summary>
public static class AccountingModel
{
    public const string NotDeletedFilter = "\"IsDeleted\" = FALSE";

    public static void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ConfigureCompany(modelBuilder.Entity<Company>());
        ConfigureMembership(modelBuilder.Entity<CompanyMembership>());
        ConfigureGroup(modelBuilder.Entity<AccountGroup>());
        ConfigureLedger(modelBuilder.Entity<Ledger>());
        ConfigureVoucherType(modelBuilder.Entity<VoucherType>());
        ConfigureVoucher(modelBuilder.Entity<Voucher>());
        ConfigureVoucherLine(modelBuilder.Entity<VoucherLine>());
        ConfigureSeries(modelBuilder.Entity<VoucherNumberSeries>());
        ConfigureAudit(modelBuilder.Entity<AuditEntry>());
        ConfigureAuditHead(modelBuilder.Entity<AuditChainHead>());
    }

    /// <summary>
    /// Every DateTime (and DateTime?) is stored and read back as UTC; SQLite would otherwise return Kind=Unspecified,
    /// which PostgreSQL's timestamptz rejects when such a value is replicated to the server.
    /// </summary>
    public static void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    private static void ConfigureSyncColumns<T>(EntityTypeBuilder<T> b)
        where T : class, ISyncEntity
    {
        b.Property(e => e.Version).IsConcurrencyToken();
    }

    private static void ConfigureCompany(EntityTypeBuilder<Company> b)
    {
        b.ToTable("Companies");
        b.HasKey(c => c.Id);
        b.Property(c => c.Id).ValueGeneratedNever();
        ConfigureSyncColumns(b);
        b.Property(c => c.Name).HasMaxLength(NameRules.MaxLength).IsRequired();
        b.Property(c => c.MailingName).HasMaxLength(200);
        b.Property(c => c.AddressLine1).HasMaxLength(200);
        b.Property(c => c.AddressLine2).HasMaxLength(200);
        b.Property(c => c.City).HasMaxLength(100);
        b.Property(c => c.StateCode).HasMaxLength(2);
        b.Property(c => c.Pincode).HasMaxLength(10);
        b.Property(c => c.Country).HasMaxLength(60).IsRequired();
        b.Property(c => c.Gstin).HasMaxLength(15);
        b.Property(c => c.Pan).HasMaxLength(10);
        b.Property(c => c.Email).HasMaxLength(254);
        b.Property(c => c.Phone).HasMaxLength(30);
        b.Property(c => c.CurrencyCode).HasMaxLength(3).IsRequired();
        b.Property(c => c.CurrencySymbol).HasMaxLength(5).IsRequired();
    }

    private static void ConfigureMembership(EntityTypeBuilder<CompanyMembership> b)
    {
        b.ToTable("CompanyMemberships");
        b.HasKey(m => m.Id);
        b.Property(m => m.Id).ValueGeneratedNever();
        ConfigureSyncColumns(b);
        b.Property(m => m.UserEmail).HasMaxLength(254).IsRequired();
        b.Property(m => m.UserDisplayName).HasMaxLength(200);
        b.HasIndex(m => new { m.CompanyId, m.UserId }).IsUnique();
        b.HasIndex(m => m.UserId);
        b.HasOne<Company>().WithMany().HasForeignKey(m => m.CompanyId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureGroup(EntityTypeBuilder<AccountGroup> b)
    {
        b.ToTable("AccountGroups");
        b.HasKey(g => g.Id);
        b.Property(g => g.Id).ValueGeneratedNever();
        ConfigureSyncColumns(b);
        b.Property(g => g.Name).HasMaxLength(NameRules.MaxLength).IsRequired();
        b.Property(g => g.NormalizedName).HasMaxLength(NameRules.MaxLength).IsRequired();
        b.Property(g => g.SystemKey).HasMaxLength(50);
        b.HasIndex(g => new { g.CompanyId, g.NormalizedName }).IsUnique().HasFilter(NotDeletedFilter);
        b.HasIndex(g => new { g.CompanyId, g.ParentGroupId });
        b.HasOne<Company>().WithMany().HasForeignKey(g => g.CompanyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AccountGroup>().WithMany().HasForeignKey(g => g.ParentGroupId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureLedger(EntityTypeBuilder<Ledger> b)
    {
        b.ToTable("Ledgers");
        b.HasKey(l => l.Id);
        b.Property(l => l.Id).ValueGeneratedNever();
        ConfigureSyncColumns(b);
        b.Property(l => l.Name).HasMaxLength(NameRules.MaxLength).IsRequired();
        b.Property(l => l.NormalizedName).HasMaxLength(NameRules.MaxLength).IsRequired();
        b.Property(l => l.Alias).HasMaxLength(NameRules.MaxLength);
        b.Property(l => l.SystemKey).HasMaxLength(50);
        b.Property(l => l.Gstin).HasMaxLength(15);
        b.Property(l => l.StateCode).HasMaxLength(2);
        b.Property(l => l.Pan).HasMaxLength(10);
        b.Property(l => l.AddressLine1).HasMaxLength(200);
        b.Property(l => l.AddressLine2).HasMaxLength(200);
        b.Property(l => l.City).HasMaxLength(100);
        b.Property(l => l.Pincode).HasMaxLength(10);
        b.Property(l => l.Email).HasMaxLength(254);
        b.Property(l => l.Phone).HasMaxLength(30);
        b.HasIndex(l => new { l.CompanyId, l.NormalizedName }).IsUnique().HasFilter(NotDeletedFilter);
        b.HasIndex(l => new { l.CompanyId, l.GroupId });
        b.HasOne<Company>().WithMany().HasForeignKey(l => l.CompanyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AccountGroup>().WithMany().HasForeignKey(l => l.GroupId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureVoucherType(EntityTypeBuilder<VoucherType> b)
    {
        b.ToTable("VoucherTypes");
        b.HasKey(t => t.Id);
        b.Property(t => t.Id).ValueGeneratedNever();
        ConfigureSyncColumns(b);
        b.Property(t => t.Name).HasMaxLength(NameRules.MaxLength).IsRequired();
        b.Property(t => t.NormalizedName).HasMaxLength(NameRules.MaxLength).IsRequired();
        b.Property(t => t.Abbreviation).HasMaxLength(10).IsRequired();
        b.HasIndex(t => new { t.CompanyId, t.NormalizedName }).IsUnique().HasFilter(NotDeletedFilter);
        b.HasOne<Company>().WithMany().HasForeignKey(t => t.CompanyId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureVoucher(EntityTypeBuilder<Voucher> b)
    {
        b.ToTable("Vouchers");
        b.HasKey(v => v.Id);
        b.Property(v => v.Id).ValueGeneratedNever();
        ConfigureSyncColumns(b);
        b.Property(v => v.SeriesCode).HasMaxLength(8);
        b.Property(v => v.Number).HasMaxLength(40);
        b.Property(v => v.Narration).HasMaxLength(1000);
        b.Property(v => v.Reference).HasMaxLength(100);
        b.Property(v => v.IdempotencyKey).HasMaxLength(100);

        b.HasMany(v => v.Lines)
            .WithOne()
            .HasForeignKey(l => l.VoucherId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Navigation(v => v.Lines).UsePropertyAccessMode(PropertyAccessMode.Field).HasField("_lines");

        // Registers and Day Book: company + date range, stable order by id.
        b.HasIndex(v => new { v.CompanyId, v.Date, v.Id });

        // Numbering backstop: a number can exist once per company, type, financial year and series.
        b.HasIndex(v => new { v.CompanyId, v.VoucherTypeId, v.FinancialYearStart, v.SeriesCode, v.SequenceNumber })
            .IsUnique()
            .HasFilter("\"SequenceNumber\" IS NOT NULL");

        // API retry keys.
        b.HasIndex(v => new { v.CompanyId, v.IdempotencyKey }).IsUnique().HasFilter("\"IdempotencyKey\" IS NOT NULL");

        // A voucher can be reversed at most once, even by two offline devices.
        b.HasIndex(v => v.ReversalOfVoucherId).IsUnique().HasFilter("\"ReversalOfVoucherId\" IS NOT NULL");

        b.HasOne<Company>().WithMany().HasForeignKey(v => v.CompanyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<VoucherType>().WithMany().HasForeignKey(v => v.VoucherTypeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Voucher>().WithMany().HasForeignKey(v => v.ReversalOfVoucherId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureVoucherLine(EntityTypeBuilder<VoucherLine> b)
    {
        b.ToTable("VoucherLines");
        b.HasKey(l => l.Id);
        b.Property(l => l.Id).ValueGeneratedNever();
        b.Property(l => l.Narration).HasMaxLength(500);

        // Ledger report / trial balance: (company, ledger, date) answers every per-ledger aggregate.
        b.HasIndex(l => new { l.CompanyId, l.LedgerId, l.VoucherDate });
        b.HasIndex(l => new { l.VoucherId, l.LineNo }).IsUnique();
        b.HasOne<Ledger>().WithMany().HasForeignKey(l => l.LedgerId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureSeries(EntityTypeBuilder<VoucherNumberSeries> b)
    {
        b.ToTable("VoucherNumberSeries");
        b.HasKey(s => s.Id);
        b.Property(s => s.Id).ValueGeneratedNever();
        b.Property(s => s.SeriesCode).HasMaxLength(8).IsRequired();
        b.HasIndex(s => new { s.CompanyId, s.VoucherTypeId, s.FinancialYearStart, s.SeriesCode }).IsUnique();
        b.HasOne<VoucherType>().WithMany().HasForeignKey(s => s.VoucherTypeId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureAudit(EntityTypeBuilder<AuditEntry> b)
    {
        b.ToTable("AuditEntries");
        b.HasKey(a => a.Id);
        b.Property(a => a.Id).ValueGeneratedNever();
        b.Property<Guid>(AuditChainHead.ChainKeyProperty);
        b.Property(a => a.Action).HasMaxLength(100).IsRequired();
        b.Property(a => a.EntityType).HasMaxLength(50).IsRequired();
        b.Property(a => a.Summary).HasMaxLength(500);

        // Stored as plain text (never jsonb): the hash covers the exact characters.
        b.Property(a => a.ChangesJson);
        b.Property(a => a.CorrelationId).HasMaxLength(100);
        b.Property(a => a.PreviousHash).HasMaxLength(64).IsRequired();
        b.Property(a => a.Hash).HasMaxLength(64).IsRequired();
        b.HasIndex(AuditChainHead.ChainKeyProperty, nameof(AuditEntry.ChainSequence)).IsUnique();
        b.HasIndex(a => new { a.CompanyId, a.EntityType, a.EntityId });
        b.HasIndex(a => new { a.CompanyId, a.OccurredAtUtc });
    }

    private static void ConfigureAuditHead(EntityTypeBuilder<AuditChainHead> b)
    {
        b.ToTable("AuditChainHeads");
        b.HasKey(h => h.ChainKey);
        b.Property(h => h.ChainKey).ValueGeneratedNever();
        b.Property(h => h.LastHash).HasMaxLength(64).IsRequired();
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Utc ? v : DateTime.SpecifyKind(v, DateTimeKind.Utc),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
}

/// <summary>Tip of one audit hash chain (per company; <see cref="Guid.Empty"/> for events outside a company).</summary>
public sealed class AuditChainHead
{
    public const string ChainKeyProperty = "ChainKey";

    public Guid ChainKey { get; set; }

    public long LastSequence { get; set; }

    public string LastHash { get; set; } = string.Empty;
}
