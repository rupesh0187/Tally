# TallyNext — Architecture

TallyNext is an offline-first, double-entry accounting product: a Windows desktop application that works
without a network, a browser application for online use, and a central server that both synchronize with.

```
┌────────────────────────────────────────┐      ┌──────────────────────────────────────┐
│ Desktop (MAUI Blazor Hybrid)           │      │ Web (Blazor Web App, interactive SSR) │
│  TallyNext.UI screens                  │      │  TallyNext.UI screens                 │
│  Application use cases (in process)    │      │  Application use cases (in process)   │
│  LocalDbContext → SQLite + outbox      │      │  ServerDbContext → PostgreSQL         │
└───────────────┬────────────────────────┘      └───────────────┬──────────────────────┘
                │ HTTPS: /api/sync/push, /api/sync/pull          │ (same database)
                ▼                                                ▼
        ┌─────────────────────────────────────────────────────────────┐
        │ TallyNext.Api (ASP.NET Core): auth, devices, sync, REST API │
        │ ServerDbContext → PostgreSQL: data, change log, audit chain │
        └─────────────────────────────────────────────────────────────┘
```

## Projects

| Project | Responsibility | Depends on |
|---|---|---|
| `TallyNext.Domain` | Entities and invariants: money, company, groups, ledgers, voucher types, vouchers, audit entry, role matrix. No EF Core, no UI, no ASP.NET. | — |
| `TallyNext.Contracts` | Wire DTOs, report DTOs, sync protocol and entity snapshots, sync JSON settings. | Domain (enums only) |
| `TallyNext.Application` | Use cases (`CompanyService`, `ChartOfAccountsService`, `VoucherService`, `ReportService`, `AuditService`, `MembershipService`), authorization (`IAccessGuard`), audit trail, numbering, snapshot mapping. | Domain, Contracts, EF Core abstractions |
| `TallyNext.Infrastructure` | Provider-neutral EF model (`AccountingModel`), save pipeline (stamping, dependency ordering, audit chain sealing), snapshot materialization. | Application |
| `TallyNext.Infrastructure.Server` | `ServerDbContext` (PostgreSQL + Identity), PostgreSQL migrations, server change log, sync processing, devices, tokens. | Infrastructure |
| `TallyNext.Infrastructure.Local` | `LocalDbContext` (SQLite), SQLite migrations, transactional outbox, sync client/engine, offline identity, protected secrets. No MAUI dependency, so it is tested headlessly. | Infrastructure |
| `TallyNext.UI` | Razor component library: every screen, shared by Desktop and Web. | Application |
| `TallyNext.Api` | REST + sync API, JWT bearer auth, rate limiting, OpenAPI/Swagger, Serilog. | Infrastructure.Server |
| `TallyNext.Web` | Blazor Web App, cookie auth, hosts `TallyNext.UI` against the server database. | Infrastructure.Server, UI |
| `TallyNext.Desktop.Core` | Desktop composition without MAUI: use-case runner, sign-in adapter, sync panel, start-up, `AddTallyNextDesktop`. Tested headlessly. | UI, Infrastructure.Local |
| `TallyNext.Desktop` | Thin .NET MAUI Blazor Hybrid shell hosting `TallyNext.UI` against `Infrastructure.Local`. | Desktop.Core |

Dependencies point inwards only (Domain ← Application ← Infrastructure ← hosts). There are no cycles.

### One UI, two hosts

`TallyNext.UI` contains every screen. Screens never hold a database context: they call use cases through
`IUseCaseRunner`, which creates a fresh DI scope per operation (Blazor circuits and the desktop app outlive any unit
of work). Each host supplies the small set of host-specific services:

| Service | Web (`TallyNext.Web`) | Desktop (`TallyNext.Desktop.Core`) |
|---|---|---|
| `IUseCaseRunner` | sets the circuit user on the scope, re-checks the account is active | uses the device session user |
| `IHostFeatures` | member management on, sign-out page | offline mode, stops sync on sign-out |
| `ISyncPanel` | review of device conflicts recorded on the server | real sync engine status, "sync now", conflict resolution |
| `IDesktopSignIn` | — (static sign-in page with cookies) | online-first, offline-fallback sign-in |
| Lifetime of `AppState`, `LedgerCache` | per circuit (scoped) | per application (singleton) |

Keyboard navigation in voucher entry is client-side (`data-next` + a window-level key handler in
`wwwroot/tallynext.js`), so keystrokes typed immediately after Enter never land in the previous field even on a
high-latency web connection.

### Why the application layer uses EF Core abstractions

Use cases are written once against `IAccountingDbContext` (DbSets + `BeginCompanyWriteAsync` + `SaveChangesAsync`)
and execute unchanged against PostgreSQL on the server and SQLite on the device. Repositories would have
duplicated every query per provider without adding safety; provider differences are confined to
infrastructure (locking, change log vs. outbox, migrations). Domain remains persistence-ignorant.

## Modular monolith

The server is one deployable (API) plus the web front end sharing one database. Module boundaries are the
application services (companies, chart of accounts, vouchers and invoices, inventory, GST, reports, audit, sync). Nothing is distributed;
a module could later be extracted because it only reaches other modules through their services.

## Core flows

### Saving a voucher (any front end)
1. `IAccessGuard.RequireAsync(company, VoucherCreate)` — membership + role check (404 for non-members).
2. `BeginCompanyWriteAsync(company)` — transaction + per-company write lock.
3. Load company, voucher type, ledgers *after* taking the lock; build the `Voucher` aggregate.
4. `VoucherPostingRules` — ledgers exist in this company, balance, per-type rules (Contra/Payment/...).
5. `VoucherNumberAllocator` — next number in this store's series (server `W`, device `Dxx`).
6. `IAuditTrail.Record` — audit entry added to the same unit of work.
7. `SaveChangesAsync` — server: assigns versions and writes the change log; device: writes the outbox;
   both: seal the audit hash chain. One commit.

### Company write lock
`BeginCompanyWriteAsync` serializes all writes of one company:
* PostgreSQL: `UPDATE "CompanySyncStates" SET "LastSequence" = "LastSequence" WHERE "CompanyId" = @c`
  takes a row lock held until commit (READ COMMITTED). Version numbers, voucher numbers and the audit
  chain are allocated under it, so they are gap-free per transaction and committed in order.
* SQLite: `BEGIN IMMEDIATE` (single writer).

Accounting write volume per company is low, so serializing per company costs little and removes whole
classes of race conditions (duplicate numbers, forked audit chains, out-of-order change sequences).

## Accounting sign convention

* Internally every amount on a line is positive with a side (Dr/Cr). A *signed* amount is
  `Debit − Credit`: positive = debit balance, negative = credit balance.
* Assets and expenses normally have debit balances; liabilities, capital and income credit balances.
* Reports never ask the reader to interpret a minus sign: balances are shown in separate Dr and Cr
  columns, each non-negative (`Amounts.DebitPart/CreditPart`).
* Income and expense ledgers are period accounts: their balance restarts at each financial-year start and
  the prior-year result is carried (computationally, without closing entries) into the system
  "Profit & Loss A/c" ledger. The balance sheet shows that ledger's own balance plus the accumulated net
  profit up to the report date as one "Profit & Loss A/c" item, split into "Opening balance" and
  "Current period". Income/expense ledgers never appear in balance-sheet sections → no double counting.
* If opening balances do not agree, the difference is reported as "Difference in opening balances" on the
  side that balances the statement (and as `OpeningDifference` in the trial balance), never hidden.

## Reports

Reports are computed from opening balances + posted lines on every request (no stored totals that could
drift). Ledger-level aggregates are one grouped conditional-sum query over the
`(CompanyId, LedgerId, VoucherDate)` index; group roll-ups are done in memory over per-ledger rows.
`VoucherLines.VoucherDate` and `IsPosted` are denormalized from the voucher (they can only change while
the voucher is a draft) to make that index covering.

Inventory is valued by `StockValuationService`: per item, the opening stock plus posted item lines in date order
are replayed through a weighted-average or FIFO cost flow (`Domain/Inventory/StockValuation.cs`). The trial
balance adds an "Opening stock" row (stock at the start of the financial year) and moves the stock gain of earlier
years into the Profit & Loss ledger's opening; the P&L trading account adds closing stock and subtracts opening
stock; the balance sheet adds closing stock under Stock-in-Hand. Stock at books beginning is part of the opening
balances. These adjustments keep every statement balanced (asserted by tests on both providers).

### Bill-wise

Lines posted to bill-wise ledgers carry a breakup (`VoucherBillAllocation`: New Ref, Agst Ref, Advance, On Account)
validated by the voucher aggregate (positive parts adding up to the line, names where required). Defaults and the
"settlement must name an existing bill" rule live in `BillAllocationRules`, applied to user input only.
`BillWiseService` computes pending bills by grouping opening bills and posted allocations by (ledger, normalized
name) and derives the on-account amount as ledger balance minus pending bills, so per-party totals always equal the
ledger balances.

### Invoices

`InvoiceService` turns an invoice request (party, place of supply, item/service lines) into a voucher:
`PlaceOfSupply` decides the supply type, `GstCalculator` computes tax per line and head, and `InvoiceBuilder`
derives the accounting lines (party, sales/purchase ledgers aggregated, tax ledgers by head, optional round-off).
The voucher stores both the item lines and the derived accounting lines; `VoucherPostingRules` re-derives the
accounting lines from the item lines on every save, post and sync upload, so the two can never disagree.

## Hosting and configuration

* API and Web read `ConnectionStrings:Server` (PostgreSQL). `Database:Provider=Sqlite` exists only for
  automated tests and is refused when `ASPNETCORE_ENVIRONMENT=Production`.
* Secrets (JWT signing key, connection strings) come from environment variables or user-secrets; nothing
  secret is committed. Development generates an ephemeral signing key when none is configured.
* Logging: Serilog, structured, with request logging that excludes bodies, headers and query strings.

See `DATABASE_DESIGN.md`, `SYNC_DESIGN.md` and `SECURITY.md` for details.
