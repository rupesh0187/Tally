# TallyNext

Offline-first, double-entry accounting for Windows desktops and the web, with automatic two-way
synchronization. Keyboard-driven workflows inspired by classic Indian accounting software â€” with an original
name, user interface and implementation.

* **Desktop** (.NET MAUI Blazor Hybrid): works fully offline on a local SQLite database; changes are queued in a
  transactional outbox and synchronized when the server is reachable.
* **Web** (Blazor Web App): works directly on the central PostgreSQL database.
* **API** (ASP.NET Core): authentication, REST endpoints, device synchronization, OpenAPI/Swagger.

> Status: foundation, GST and inventory complete and verified by 171 automated tests (on SQLite and on PostgreSQL); see
> [Status](#status) and [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) for what is and is not done.

## Quick start (Windows, PowerShell)

Prerequisites: .NET SDK 10.0.100+, Docker Desktop (for the development PostgreSQL). The desktop app additionally
needs the MAUI Windows workload (see [Desktop application](#desktop-application)).

```powershell
cd D:\TallyNext
./scripts/setup.ps1 -StartDatabase    # checks prerequisites, restores tools/packages, starts PostgreSQL on localhost:5433
./scripts/dev.ps1                      # builds, runs the API (http://localhost:5080) and the web app (http://localhost:5090)
```

Individually:

| Task | Command |
|---|---|
| Start PostgreSQL (Docker, port 5433) | `docker compose up -d` |
| Run the API | `dotnet run --project src/TallyNext.Api --launch-profile http` â†’ http://localhost:5080/swagger |
| Run the web app | `dotnet run --project src/TallyNext.Web --launch-profile http` â†’ http://localhost:5090 |
| Build everything except the MAUI shell | `dotnet build TallyNext.Core.slnf` |
| Run all tests (server side on SQLite) | `./scripts/test.ps1` |
| Run all tests against PostgreSQL | `./scripts/test.ps1 -Postgres` |
| Run one test project | `dotnet test --project tests/TallyNext.UnitTests` |
| Build the desktop app (needs MAUI workload) | `dotnet build src/TallyNext.Desktop -f net10.0-windows10.0.19041.0` |
| Run the desktop app | `dotnet run --project src/TallyNext.Desktop -f net10.0-windows10.0.19041.0` |
| Add a server (PostgreSQL) migration | `dotnet ef migrations add <Name> --project src/TallyNext.Infrastructure.Server --startup-project src/TallyNext.Infrastructure.Server --context ServerDbContext --output-dir Persistence/Migrations` |
| Add a device (SQLite) migration | `dotnet ef migrations add <Name> --project src/TallyNext.Infrastructure.Local --startup-project src/TallyNext.Infrastructure.Local --context LocalDbContext --output-dir Persistence/Migrations` |

`dotnet ef` is a repo-local tool (`dotnet tool restore`).

### Development sign-in

In the `Development` environment the API creates one user and a company filled with clearly synthetic data. The
e-mail and the development-only password are in `src/TallyNext.Api/appsettings.Development.json` (`Seed` section).
They exist only for local development; change them there if you like. Nothing is seeded in other environments.

### Desktop application

The desktop shell (`src/TallyNext.Desktop`) needs the .NET MAUI Windows workload. On the machine this project was
created on, the workload manifest was present but its .NET 10 packs were not, so building fails with
`NETSDK1147`. Install it from an **elevated** PowerShell:

```powershell
dotnet workload install maui-windows
```

(or Visual Studio Installer â†’ Modify â†’ ".NET Multi-platform App UI development"). All desktop behaviour â€” local
database, outbox, sync engine, offline sign-in, screens â€” lives in projects that build and are tested without the
workload (`TallyNext.Infrastructure.Local`, `TallyNext.Desktop.Core`, `TallyNext.UI`).

First run: enter the server address (e.g. `http://localhost:5080` in development; HTTPS is required for anything
other than localhost), e-mail and password. The device registers itself, receives its voucher-number series and
downloads your companies. From then on it works offline; sign-in offline is allowed for 7 days after the last
online contact (configurable on the server, `Auth:OfflineGraceDays`). Data lives in
`%LOCALAPPDATA%\TallyNext\tallynext.db`, logs in `%LOCALAPPDATA%\TallyNext\logs`.

## Keyboard

| Keys | Action |
|---|---|
| F4 / F5 / F6 / F7 / F8 / F9 | Contra / Payment / Receipt / Journal / Sales / Purchase voucher |
| Alt+F6 / Alt+F5 | Credit note / Debit note |
| Ctrl+H | Switch a Sales/Purchase/Credit/Debit note between accounting voucher and item invoice |
| Enter | Next field in voucher entry (ledger â†’ amount â†’ next line â†’ narration) |
| Ctrl+S or Ctrl+Enter | Save (post) the voucher or form |
| Alt+D / Alt+L / Alt+T / Alt+P / Alt+B | Day Book / Ledgers / Trial Balance / Profit & Loss / Balance Sheet |
| Alt+V / Alt+S / Alt+C | Voucher register / Synchronization / Select company |
| Alt+I / Alt+K | Stock items / Stock Summary |
| Esc / F1 | Close list or go back / keyboard help |

Some hosts reserve Ctrl+S (e.g. a browser's "save page"); Ctrl+Enter always works.

## Repository layout

```
src/
  TallyNext.Domain                 money, company, chart of accounts, vouchers, audit entry (no dependencies)
  TallyNext.Contracts              DTOs, sync protocol and snapshots
  TallyNext.Application            use cases, authorization, reports, audit trail
  TallyNext.Infrastructure         shared EF Core model, save pipeline, audit hash chain
  TallyNext.Infrastructure.Server  PostgreSQL context + migrations, Identity, tokens, sync server
  TallyNext.Infrastructure.Local   SQLite context + migrations, outbox, sync engine, offline sign-in
  TallyNext.UI                     shared Razor screens (desktop and web)
  TallyNext.Desktop.Core           desktop composition (MAUI-free, tested)
  TallyNext.Desktop                .NET MAUI Blazor Hybrid shell (Windows)
  TallyNext.Api                    REST + sync API, JWT, Swagger
  TallyNext.Web                    Blazor Web App, cookie sign-in
tests/
  TallyNext.UnitTests              domain + application against real SQLite
  TallyNext.IntegrationTests       HTTP API: auth, isolation, roles, vouchers, invoices, inventory, audit
  TallyNext.SyncTests              multi-device sync, conflicts, offline auth, cross-provider reports, desktop composition
scripts/                           setup.ps1, dev.ps1, test.ps1
```

Design: [ARCHITECTURE.md](ARCHITECTURE.md) Â· [DATABASE_DESIGN.md](DATABASE_DESIGN.md) Â·
[SYNC_DESIGN.md](SYNC_DESIGN.md) Â· [SECURITY.md](SECURITY.md) Â· [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md)

## Configuration

| Setting | Where | Notes |
|---|---|---|
| `ConnectionStrings__Server` | API, Web | PostgreSQL. Required; development value in `appsettings.Development.json` |
| `Jwt__SigningKey` | API | â‰¥ 32 bytes. Required outside Development (an ephemeral key is generated in Development) |
| `Database__MigrateOnStartup` | API, Web | `true` in Development; run migrations explicitly in production |
| `Auth__AllowSelfRegistration` | API | `true` in Development only |
| `Auth__OfflineGraceDays` | API | Offline sign-in window for desktops (default 7) |
| `OpenApi__Expose` | API | Swagger outside Development (default off) |

## Status

| Area | State | Verified by |
|---|---|---|
| Money (exact, integer paise), GSTIN/PAN validation, financial years | âœ… | unit tests |
| Companies, groups (default chart), ledgers, voucher types | âœ… | unit + API tests |
| Contra, Payment, Receipt, Journal (+ accounting-mode Sales, Purchase, Credit/Debit Note) | âœ… | unit + API tests |
| Draft â†’ post, cancel draft, reversal (once), period lock, idempotent saves | âœ… | unit + API tests |
| Voucher numbering: server series + per-device offline series, concurrency-safe | âœ… | 25 parallel saves on PostgreSQL |
| Day Book, Ledger, Trial Balance, P&L, Balance Sheet, Cash & Bank | âœ… | reconciliation + cross-provider tests |
| Hash-chained audit trail, written in the same transaction | âœ… | tamper-detection tests |
| Identity, JWT + rotating refresh tokens, lockout, rate limiting, device revocation | âœ… | API tests |
| Company isolation and role permissions | âœ… | API tests |
| Offline-first desktop storage, outbox, sync push/pull, conflicts, recovery | âœ… | multi-device sync tests (SQLite and PostgreSQL server) |
| Shared UI screens, web app | âœ… | exercised in a browser against PostgreSQL |
| Desktop MAUI shell | ðŸŸ¡ written; not compiled here (MAUI workload missing) | composition test of everything below the shell |
| GST: registration, effective-dated rates, place of supply (CGST+SGST, CGST+UTGST, IGST; SEZ/exports zero-rated), per-line rounding, round-off, GST Summary | âœ… | unit, API, sync and cross-provider tests |
| Inventory: units, godowns, stock items, item invoices (sales, purchase, credit/debit notes), weighted average and FIFO, Stock Summary, closing stock in TB/P&L/Balance Sheet | âœ… | unit, API, sync and cross-provider tests; exercised in a browser |
| Bill-wise, cost centres, TDS, GST returns and e-invoicing | â³ | â€” |
| Backups, packaging/installer, performance benchmarks | â³ | â€” |

Nothing here has been reviewed for statutory (GST, Companies Act audit-trail) compliance. GST rates are not
looked up from any official source; they are entered by the user. The GST Summary is an aid for preparing returns,
not a return.
