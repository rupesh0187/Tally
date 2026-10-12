# TallyNext — Database design

Two physical databases share one logical accounting schema (`TallyNext.Infrastructure/Persistence/AccountingModel.cs`):

| | Server | Device |
|---|---|---|
| Engine | PostgreSQL 16+ (tested target: 18) | SQLite 3.4x (bundled with Microsoft.Data.Sqlite) |
| Context | `ServerDbContext` (+ ASP.NET Core Identity) | `LocalDbContext` |
| Migrations | `src/TallyNext.Infrastructure.Server/Persistence/Migrations` | `src/TallyNext.Infrastructure.Local/Persistence/Migrations` |
| Extra tables | Identity, Devices, RefreshTokens, CompanySyncStates, SyncChanges, ProcessedChanges, SyncConflicts | OutboxEntries, SyncCursors, LocalConflicts, DeviceRegistration, LocalUsers, ProtectedSecrets |

## Migration strategy

* Each context targets **exactly one provider** and owns its own migration set. A PostgreSQL migration is
  never applied to SQLite or vice-versa; the model only uses features both understand, but the generated
  DDL differs and is reviewed per provider.
* Generating migrations (repo-local `dotnet-ef` tool, see `dotnet-tools.json`):
  ```powershell
  dotnet ef migrations add <Name> --project src/TallyNext.Infrastructure.Local  --startup-project src/TallyNext.Infrastructure.Local  --context LocalDbContext  --output-dir Persistence/Migrations
  dotnet ef migrations add <Name> --project src/TallyNext.Infrastructure.Server --startup-project src/TallyNext.Infrastructure.Server --context ServerDbContext --output-dir Persistence/Migrations
  ```
* Device: migrations are applied automatically at application start (`LocalDatabaseInitializer`) — a
  desktop has no DBA. Before applying a migration the application should copy the database file
  (planned in Milestone 11; see IMPLEMENTATION_PLAN.md).
* Server: migrations are applied by an explicit deployment step (`dotnet ef database update` or the
  generated idempotent script `dotnet ef migrations script --idempotent`), or at startup only when
  `Database:MigrateOnStartup=true` (enabled in Development).
* Schema compatibility with devices is governed by the sync `SchemaVersion`, not by migration names.

## Identifiers

UUIDv7 (`Guid.CreateVersion7()`) for every aggregate — generated offline, globally unique, time-ordered.
Memberships use a deterministic name-based UUID (v8, SHA-256 of company+user) so that a device and the
server that independently record "user U owns company C" create the same row.

## Money

* Stored as **64-bit integer minor units** (`...Minor` columns, paise for INR) on both providers:
  exact, and `SUM` is exact and fast in SQLite (which has no exact decimal type) and PostgreSQL.
* In memory: `Money` (long minor units) and `decimal` at the edges; never `float`/`double`.
* Supported base currencies all have two minor digits (INR, USD, EUR, GBP, AED, SGD, AUD, CAD, NPR, LKR,
  BDT). Three-decimal currencies are rejected until per-currency scale is implemented.
* Input with more than two decimals is **rejected**, not rounded. Computed values (tax) are rounded with
  `Money.Round` (half away from zero by default, banker's rounding available).
* Single amounts are limited to ±10^15 minor units; arithmetic is `checked`.

## Timestamps and dates

* Business dates (`Voucher.Date`, books beginning, lock date) are `DateOnly` → `date` / `TEXT yyyy-MM-dd`.
* Technical timestamps are UTC `DateTime` → `timestamptz` / `TEXT`; a value converter forces
  `Kind=Utc` on read (SQLite returns Unspecified, which PostgreSQL rejects). Audit timestamps are
  truncated to microseconds so the hash chain verifies after a PostgreSQL round trip.

## Tables (accounting)

| Table | Key columns | Notes / indexes |
|---|---|---|
| `Companies` | Id | Version (concurrency token), IsDeleted, books beginning, FY start month, lock date |
| `CompanyMemberships` | Id (deterministic) | unique (CompanyId, UserId); index UserId |
| `AccountGroups` | Id | unique (CompanyId, NormalizedName) WHERE NOT IsDeleted; (CompanyId, ParentGroupId) |
| `Ledgers` | Id | unique (CompanyId, NormalizedName) WHERE NOT IsDeleted; (CompanyId, GroupId); OpeningBalanceMinor + OpeningSide |
| `VoucherTypes` | Id | unique (CompanyId, NormalizedName) WHERE NOT IsDeleted |
| `Vouchers` | Id | (CompanyId, Date, Id); **unique** (CompanyId, VoucherTypeId, FinancialYearStart, SeriesCode, SequenceNumber) WHERE SequenceNumber IS NOT NULL; **unique** (CompanyId, IdempotencyKey); **unique** ReversalOfVoucherId |
| `VoucherLines` | Id | (CompanyId, LedgerId, VoucherDate) for reports; unique (VoucherId, LineNo); AmountMinor > 0 + Side |
| `VoucherNumberSeries` | Id | unique (CompanyId, VoucherTypeId, FinancialYearStart, SeriesCode); store-local, not replicated |
| `VoucherItemLines` | Id | invoice lines: kind (stock/service), item, godown, ledger, `QuantityMilli` (qty × 1000), `RateScaled` (rate × 10,000), discount (milli-%), `TaxableMinor`, stock direction, taxability, GST/cess rate (milli-%), HSN/SAC, CGST/SGST-UTGST/IGST/cess minor; (CompanyId, StockItemId, VoucherDate) for valuation; (CompanyId, VoucherDate) for the GST summary; unique (VoucherId, LineNo) |
| `VoucherBillAllocations` | Id | bill-wise breakup of voucher lines: line no., allocation no., ledger and side (from the line), kind (New Ref / Agst Ref / Advance / On Account), bill name + normalized name, due date, `AmountMinor`, voucher date, posted flag; (CompanyId, LedgerId, NormalizedBillName) for outstanding; unique (VoucherId, LineNo, AllocationNo) |
| `Units` | Id | symbol, name, decimal places (0–3); unique (CompanyId, NormalizedSymbol) WHERE NOT IsDeleted |
| `Godowns` | Id | unique (CompanyId, NormalizedName) WHERE NOT IsDeleted; one `IsSystem` "Main Location" per company |
| `StockItems` | Id | unit, valuation method, opening quantity/value/godown, `GstRatesJson`; unique (CompanyId, NormalizedName) WHERE NOT IsDeleted |
| `AuditEntries` | Id | unique (ChainKey, ChainSequence); (CompanyId, EntityType, EntityId); (CompanyId, OccurredAtUtc); `ChangesJson` is `text`, never `jsonb` |
| `AuditChainHeads` | ChainKey | last sequence + hash per chain |

Partial-index filters are written as `"IsDeleted" = FALSE`, valid in PostgreSQL and SQLite ≥ 3.23.

Milestone 10 added (migration `InventoryAndGst` on both providers): `Companies.GstRegistration`;
`Ledgers.GstTaxType` and `Ledgers.GstRatesJson`; `Vouchers.PartyLedgerId`, `PlaceOfSupplyStateCode` and
`SupplyNature`; and the four tables above. GST rate histories (a handful of effective-dated entries, always read
with their owner) are stored as JSON **text** in one column on both providers; rates are integers in thousandths
of a percent (18 % = 18000) so they are exact everywhere. Quantities and rates are scaled integers for the same
reason. Stock values are not stored: they are recomputed from opening stock and posted item lines.

The `BillWise` migration (both providers) added `VoucherBillAllocations` and `Ledgers.OpeningBillsJson` (opening
bills as JSON text, like GST rates). Bills have no table of their own: a bill is the set of allocations (and opening
bill) with the same ledger and normalized name, so outstanding amounts are always recomputed from postings.

**Device-side difference:** in `LocalDbContext` the name (incl. unit symbol, godown and stock item names), voucher-number, idempotency-key and reversal indexes are
declared **non-unique** (same columns and filters). The rules are still enforced by the application services for
local saves; the reason is the sync "conflict window" (see SYNC_DESIGN.md). The server keeps them unique.

## Deletion policy

Replicated rows are never physically deleted (the save pipeline throws if one is). Deletion is a tombstone
(`IsDeleted = true`) that replicates like any other change and prevents resurrection. Posted vouchers
cannot be deleted at all; drafts are cancelled. Ledgers/groups can be tombstoned only when unused.

## Performance notes

* All list endpoints are paged (`Paging.MaxPageSize = 500`).
* Reports use one grouped conditional-sum query per report over the covering index; ledger report page
  N computes its starting balance with one `SUM` over the preceding rows.
* Planned (Milestone 11): `pg_stat_statements` review, Dapper for the trial balance query if profiling
  shows EF overhead, benchmark suite with synthetic 1M-line datasets.
