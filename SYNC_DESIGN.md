# TallyNext — Synchronization design

Synchronization replicates company data between offline devices (SQLite) and the central server
(PostgreSQL). It is state-based with server-assigned versions, a server change log, a device outbox, and
explicit conflict handling. Protocol constants live in `Contracts/Sync/SyncProtocol.cs`.

## Principles

1. **Local first.** Every desktop save commits to SQLite first. The outbox entry is written in the same
   transaction, so a saved change can never be lost before upload. The UI shows "pending upload" until
   the server accepts it; a local save never claims to be synchronized.
2. **The server decides order, not clocks.** Each accepted change gets the next per-company sequence
   number, allocated under the company write lock. That number is the row's `Version` and the
   download cursor. Client timestamps are informational only.
3. **No silent overwrites of financial data.** Posted vouchers are immutable everywhere; edits to other
   rows carry the base version they were made from, and a mismatch is a conflict that a person resolves.
4. **Idempotent everything.** Every upload carries a `ChangeId`; the server stores the result per
   (device, change id) and replays it for retries. Voucher ids are UUIDv7 generated on the device.

## Data replicated

| Entity | Direction | Notes |
|---|---|---|
| Company | ↑↓ | creation from a device allowed (creator becomes Owner on the server) |
| AccountGroup, Ledger, VoucherType | ↑↓ | conflicts on concurrent edits; tombstones for deletes |
| Voucher (+ lines) | ↑↓ | posted vouchers immutable; numbered in the creating device's series |
| CompanyMembership | ↓ only | authored by the server; enforces offline authorization |
| Audit | — | each store keeps its own chain; the server audits every accepted/rejected upload |

## Server side

* `CompanySyncStates(CompanyId, LastSequence)` — the per-company lock row and sequence counter.
* `SyncChanges(CompanyId, Sequence, EntityType, EntityId, Operation, Version, PayloadJson, OriginDeviceId, OriginChangeId, ...)`
  — written by `ServerDbContext.SaveChangesAsync` in the same transaction as **every** change to a
  replicated entity, whether it came from the web UI, the REST API or a device. Payload = full snapshot.
* Because sequences are allocated under a row lock that is held to commit, a reader can never observe
  sequence N+1 before N — cursors never skip a change (a known pitfall of identity columns).
* `ProcessedChanges(DeviceId, ChangeId, PayloadHash, Outcome, ResultJson)` — idempotency ledger. A reused
  change id with a different payload hash is rejected (`sync.change_id_reused`).
* `SyncConflicts` — every conflict with client payload, server payload, versions, device, user; reviewed by
  users with `SyncConflictResolve`.

### Push (`POST /api/sync/push`)
For each change, in the device's `LocalSequence` order, in its own transaction:
1. Protocol/schema version check (unsupported → HTTP 426, nothing applied).
2. Device must belong to the caller and not be revoked; the user must be active.
3. Duplicate check by (device, change id) → return stored result.
4. Company access: membership + permission for the entity type (`MasterEdit`, `VoucherCreate`, ...).
   A company created on the device is allowed for any authenticated user (who becomes Owner).
5. Take the company write lock; load the current row.
6. Concurrency: `BaseVersion` must equal the current `Version` (0 = row must not exist).
   Otherwise → `Conflict` (+ conflict record + current server snapshot in the response).
   Tombstoned rows reject further edits (no resurrection).
7. Validate with the **same application rules** as online saves (balance, ledgers belong to the
   company, voucher type rules, period lock, unique names, device series ownership for numbers).
   Posted vouchers may only arrive as new rows; any change to an existing posted voucher → `Rejected`.
8. Apply, audit (`source = Sync`, with device and change id), write change log, commit, record result.
   Missing references (a ledger uploaded later in a rejected batch) → `Deferred` (retried later).

### Pull (`GET /api/sync/pull?companyId&cursor&limit`)
Returns change-log rows with `Sequence > cursor`, ordered, bounded (`MaxPullBatchSize`), only for
companies where the caller is an active member. Initial sync is a pull from cursor 0 (full replay in
causal order — parents always precede children because the save pipeline orders sequences by
dependency). Change-log compaction (snapshot + truncate) is a planned extension; until then the log is
retained in full.

## Device side

* `OutboxEntries` — `LocalSequence` (dependency-ordered), `ChangeId`, `BaseVersion`, payload, status
  (`Pending`, `InFlight`, `Accepted`, `Conflict`, `Rejected`, `Forbidden`, `Superseded`, `Discarded`).
  An entry that was never sent is replaced by a newer save of the same row (moved to the end so upload
  order still follows dependencies). Sent entries are immutable.
* `SyncCursors(CompanyId, Cursor, AccessRevoked)`.
* `LocalConflicts` — local copy of every conflict/rejection with both versions.

### Sync cycle (`SyncEngine.SyncOnceAsync`)
1. Push pending entries in order (batches ≤ 200). Entries are marked `InFlight` before sending; on restart
   `InFlight` returns to `Pending` (the server deduplicates).
   * `Accepted`/`Duplicate` → set the row's `Version` to the new server version (apply-mode save, no
     outbox), rebase later pending entries of the same row onto it.
   * `Conflict`/`Rejected`/`Forbidden` → entry parked, `LocalConflict` created; later entries of the
     same row are held back until the user decides.
   * `Deferred` → stays pending.
2. Pull each company until `HasMore = false`. Each page is applied in one SQLite transaction inside
   `BeginRemoteApply()` (no outbox → no echo loops) together with the cursor update. A remote change for
   a row that has unsent local changes is not applied over them; the push will surface the conflict.
3. Record the last successful sync time.

### Background service
`SyncBackgroundService` runs cycles on a timer, immediately on network-availability changes and on
manual request; failures back off exponentially (2 s → 5 min) with ±20 % jitter; one cycle at a time;
honours cancellation on shutdown. Status (`Offline`, `Syncing`, `UpToDate`, `PendingChanges`, `Error`),
pending count and last success time are observable by the UI.

## Conflict policies

| Situation | Outcome |
|---|---|
| Two devices edit the same ledger/group/company offline | Second upload → `Conflict`. User chooses *keep server* (local copy retained as `Discarded`) or *keep mine* (re-sent with the server version as base, marked `ResolvesConflictId`). |
| Same name created on two devices | Second → `Rejected` (`ledger.duplicate_name`); user renames and retries. |
| Edit to a posted voucher | Impossible in the UI; upload → `Rejected` (`voucher.posted_immutable`). |
| Two devices reverse the same voucher | Unique index on `ReversalOfVoucherId` → second `Rejected` (`voucher.already_reversed`); its local reversal remains visible for review, never silently dropped. |
| Edit/delete of a row deleted on the server | `Conflict` (tombstone wins until a person decides). |
| Upload by a revoked user / device | `Forbidden` / HTTP 401; local data and pending entries are kept, company hidden. |
| Device unknown schema/protocol | HTTP 426; nothing applied; the app asks for an update. |

## Conflict window

Between a rejected/conflicting upload and the user's decision, the device can legitimately hold two rows that the
server would never allow together — e.g. its own "HDFC Bank" ledger (rejected as a duplicate) and the server's
"HDFC Bank" ledger downloaded afterwards, or two reversals of the same voucher made on two offline devices. For that
reason the device database does **not** declare unique indexes for names, voucher numbers, idempotency keys or
reversals; the application services enforce those rules for local saves, and the server's unique indexes remain the
authority. A unique index on the device would make every later download fail and stall synchronization. Both rows
stay visible until a person resolves the conflict (Sync screen).

## Who uploads what

A device may be shared by several users. Each outbox entry records the user who made the change, and a sync cycle
uploads only the signed-in user's entries (under that user's token), so the server always authorizes a change as
the person who made it. Other users' entries — and later entries of the same row — wait.

## Voucher numbering offline

Numbers are unique per (company, voucher type, financial year, series). The server series is `W`
(displayed as a plain number); each device receives a globally unique series code (e.g. `D7K`) when it
is registered, so offline numbers (`D7K/15`) can never collide. The server verifies that a device only
uploads numbers in its own series.

## Recovery procedures

* **Crash during sync** — outbox and cursors are transactional; restart resumes. `InFlight` entries are
  retried and deduplicated server-side.
* **Long offline period** — no expiry on pending data. Offline *sign-in* expires after the user's grace
  period (default 7 days); data remains and syncs after an online sign-in.
* **Incompatible schema** — the server answers 426; the device keeps everything pending until updated.
* **Lost device database** — re-provision the device (new device id and series); pull from cursor 0.
* **Revoked access** — company hidden locally; unsynced entries stay in the outbox for an administrator
  to export/review (export tooling planned in Milestone 11).
