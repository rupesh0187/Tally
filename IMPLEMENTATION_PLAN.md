# TallyNext — Implementation plan and status

Legend: ✅ done and verified by automated tests · 🟡 partially done · ⏳ not started · ⚠️ blocked

| # | Milestone | Status | Notes |
|---|---|---|---|
| 1 | Environment, repository, solution, documents | ✅ | .NET 10.0.303 SDK; central package management; `TallyNext.Core.slnf` builds without the MAUI workload |
| 2 | Domain: money, company, groups, ledgers, accounting validation | ✅ | 40+ domain tests |
| 3 | SQLite + PostgreSQL persistence, provider-specific migrations, isolation, seed | ✅ | migrations generated for each provider; PostgreSQL migration applied and verified in Docker (PostgreSQL 17) |
| 4 | Identity, roles, authentication, authorization, transactional audit | ✅ | JWT + refresh rotation/reuse detection, lockout, rate limits, device revocation, hash-chained audit |
| 5 | Contra, Payment, Receipt, Journal vouchers | ✅ | plus accounting-mode Sales/Purchase/Credit/Debit Note rules |
| 6 | Day Book, Ledger, Trial Balance + reconciliation tests | ✅ | plus P&L, Balance Sheet, Cash & Bank; identical results on SQLite and PostgreSQL |
| 7 | Desktop screens and offline voucher entry | 🟡 | shared screens + desktop composition done and tested; MAUI shell written but not compiled here (workload missing ⚠️) |
| 8 | Sync protocol, outbox, upload, download, idempotency, conflicts, recovery | ✅ | 21 sync/desktop tests incl. lost responses, restarts, revocation, double reversal, tombstones |
| 9 | Web application; desktop ↔ online visibility | ✅ | Blazor Web App exercised in a browser; cross-channel visibility covered by sync tests |
| 10 | Sales/Purchase with GST, inventory, bill-wise, P&L, Balance Sheet | 🟡 | P&L and Balance Sheet done; GST, inventory, bill-wise not started |
| 11 | Hardening, backups, diagnostics, performance, packaging | ⏳ | |

## Verified test matrix (last run)

| Suite | Tests | SQLite server mode | PostgreSQL server mode |
|---|---|---|---|
| UnitTests | 72 | ✅ | ✅ (device-side only) |
| IntegrationTests | 17 | ✅ | ✅ |
| SyncTests | 21 | ✅ (1 PostgreSQL-only test skipped) | ✅ |

## Next concrete steps

1. **Desktop shell**: install the MAUI Windows workload (elevated: `dotnet workload install maui-windows`), build
   `src/TallyNext.Desktop`, and run the manual smoke test: first online sign-in, offline voucher entry, reconnect.
2. **GST** (Milestone 10): tax ledgers with rates and effective dates; place-of-supply engine (intra-state CGST+SGST
   or CGST+UTGST, inter-state IGST, SEZ/export cases); invoice rounding rules; tests per scenario. No compliance
   claims without review against current notifications.
3. **Inventory**: stock items, units, godowns, inventory lines on Sales/Purchase; weighted-average and FIFO
   valuation as rebuildable projections; closing stock in P&L and Balance Sheet.
4. **Bill-wise**: allocations (new ref / against ref / advance / on account), outstanding receivables/payables.
5. **Hardening**: SQLite backup before migrations and on demand (online backup API), restore; PostgreSQL backup
   runbook; `pg_stat_statements` review and benchmark suite (synthetic 1M-line company); export (CSV/PDF);
   MSIX packaging and auto-update; device-attestation secret; SQLCipher evaluation; change-log compaction.

## Known gaps and decisions to revisit

* Each user's outbox entries are uploaded only while that user is signed in on the device (a device shared by
  several users uploads each user's work under that user's identity).
* Device ids are not secrets; a malicious authenticated user could claim another device's id. Mitigation planned:
  per-device secret issued at registration (see SECURITY.md).
* Change log is retained in full; initial download replays it. Compaction + snapshot download is planned.
* Ledger/group/voucher-type uniqueness on the device is enforced by the application, not by indexes (see
  SYNC_DESIGN.md "Conflict window").
* The web UI moves focus server-side in one case (adding a balancing line); on high-latency links a very fast typist
  could type into the previous field. The desktop runs in-process and is not affected.
