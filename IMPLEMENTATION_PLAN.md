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
| 10 | Sales/Purchase with GST, inventory, bill-wise, P&L, Balance Sheet | ✅ | GST, inventory, item invoices, stock and GST summaries, closing stock in TB/P&L/BS, bill-wise allocations and outstanding with ageing |
| 11 | Hardening, backups, diagnostics, performance, packaging | ⏳ | |

## Verified test matrix (last run)

| Suite | Tests | SQLite server mode | PostgreSQL server mode |
|---|---|---|---|
| UnitTests | 140 | ✅ | ✅ (device-side only) |
| IntegrationTests | 19 | ✅ | ✅ |
| SyncTests | 29 | ✅ (1 PostgreSQL-only test skipped) | ✅ |

## Milestone 10 — what GST and inventory cover

* **GST**: company registration (not registered / regular / composition; GSTIN and State required); CGST, SGST,
  UTGST, IGST, Cess and Round Off ledgers created on request; effective-dated rate history (GST %, cess %, HSN/SAC,
  taxability) on stock items and on income/expense ledgers used for service lines; place of supply (same State →
  CGST+SGST, Union Territory without legislature → CGST+UTGST, otherwise IGST; SEZ and overseas parties always
  inter-State); exports and SEZ supplies are treated as zero-rated under LUT (no tax charged); inward supplies from
  unregistered or composition suppliers carry no input tax; tax per line and per head rounded half away from zero
  (CGST+SGST can differ from IGST by a paisa — tested); optional invoice round-off to whole rupees.
* **Inventory**: units (0–3 decimals), godowns (Main Location predefined), stock items with opening stock,
  weighted-average (perpetual moving average) or FIFO valuation; item invoices for Sales, Purchase, Credit Note
  (goods in) and Debit Note (goods out) with stock and service lines; purchases come in at taxable value, returns
  and sales go out at cost; negative stock is allowed and flagged (offline devices cannot be prevented from selling
  the same last unit).
* **Statements**: the trial balance shows "Opening stock"; P&L shows opening and closing stock in the trading
  account; the balance sheet shows closing stock under Stock-in-Hand; stock at books beginning is part of the
  opening balances (unmatched opening stock appears as a difference in opening balances, as for any ledger).
* **Integrity**: the posting rules (server and device) re-derive every invoice: line value = qty × rate − discount,
  tax from the line's rate and supply type, and the accounting lines from the item lines. A balanced but
  under-taxed upload is rejected (`invoice.tax_mismatch`, tested through the sync API).
* **Reports**: Stock Summary (quantity and value, by godown) and GST Summary (outward/inward by rate and supply
  type, net of notes and reversals; an aid, not a return). Cross-provider tests prove identical results on
  PostgreSQL and SQLite.

## Milestone 10 — bill-wise

* Ledgers can maintain bill-wise details, with a credit period and opening bills (the bill-wise breakup of the
  opening balance; the rest is on account).
* Every line posted to a bill-wise ledger can be broken up into **New Ref** (creates a bill; an empty name becomes the
  voucher number when posted), **Agst Ref** (settles a bill), **Advance** and **On Account**; the parts must add up to
  the line (checked by the domain on every save, post and sync upload).
* Defaults when no breakup is given: on account for Contra/Payment/Receipt; otherwise a new bill named after the
  voucher number (the supplier's bill number for purchases, when entered), due after the party's credit period.
  Invoices allocate the whole invoice total (after round-off) to the party's bill.
* A bill is identified by (ledger, case-insensitive name): references made on different devices meet without a
  shared counter. Reversals settle exactly what the original allocated.
* User input is checked: a settlement must name an existing bill of the same party. Device uploads are not
  re-checked for existence (a device may settle a bill the server has not received yet); such a reference appears
  as an "unmatched" negative bill in the outstanding report instead of blocking synchronization (tested).
* Reports: Receivables and Payables as at any date — pending bills with original amount, due date, overdue days,
  ageing (not due, 0–30, 31–60, 61–90, over 90 days) and on-account amounts; per-party totals equal the ledger
  balances (older vouchers without a breakup show as on account). Pending-bill lookup for entry screens.
* UI: bill-wise breakup on voucher lines (with "match oldest bills" for receipts and payments), party bill reference
  on item invoices, opening bills on the ledger form, bills on the voucher view, Receivables/Payables pages (Alt+O).

## Next concrete steps

1. **Desktop shell**: install the MAUI Windows workload (elevated: `dotnet workload install maui-windows`), build
   `src/TallyNext.Desktop`, and run the manual smoke test: first online sign-in, offline voucher entry, reconnect.
2. **Bill-wise follow-ups**: interest on overdue bills, payment reminders/statements of account, bill-wise
   breakup of more than one party line on invoices, cost centres.
3. **GST follow-ups** (each needs review against current law before release): reverse charge, exports and SEZ
   supplies with payment of IGST, TDS/TCS under GST, HSN summary, GSTR-1/GSTR-3B layouts, e-invoice (IRN) and
   e-way bill integration, GSTIN verification with the portal.
4. **Inventory follow-ups**: stock journal / transfers between godowns, physical stock adjustment, batches and
   expiry, price lists and default rates, reorder levels; valuation snapshots per period if profiling shows the
   on-request replay is too slow for large item histories.
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
* Invoices: the server verifies tax arithmetic, supply type consistency and the accounting derivation, but accepts
  the GST rate written on each line (an offline device may legitimately hold an older rate schedule; the invoice
  is the document that was issued). Whether tax should have been charged at all is decided where the invoice is
  made (company registration, party type, zero-rating); an all-zero-tax invoice is accepted.
* Inventory values are recomputed from all posted item lines on every report request (exact and simple; linear in
  the item's history). Purchase returns and reversals of purchases go out at the current cost, not the original
  purchase value.
* Bill-wise: settlements of unknown bills uploaded from devices are accepted and shown as unmatched (correct them
  with a journal against the right bill). New bills with the same name on the same party merge into one bill — by
  design, so that offline devices never conflict over bill numbers; the voucher-number default makes collisions
  rare. Over-settlement is allowed and shows as a negative pending amount.
* Companies created before inventory existed get their default unit and Main Location with an explicit
  "Create default unit and Main Location" action (stock items page) — nothing is created behind the user's back.
