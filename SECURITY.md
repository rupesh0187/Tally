# TallyNext — Security design

## Threat model (summary)

| Asset | Threats | Primary controls |
|---|---|---|
| Accounting data of a company | cross-company access (IDOR), tampering, silent edits | company-scoped authorization on every use case, 404 for non-members, immutable posted vouchers, hash-chained audit |
| User credentials | theft, brute force, offline cracking | ASP.NET Core Identity hashing (PBKDF2), lockout, rate limiting, no plaintext storage anywhere, DPAPI-protected offline verifier |
| Tokens | theft/replay | short-lived JWT (15 min), rotating refresh tokens bound to a device, reuse detection revokes the token family, device revocation |
| Desktop database | stolen laptop | OS account protection; full-disk encryption (BitLocker) required by policy; secrets in the DB are DPAPI-encrypted (see "Data at rest") |
| Audit trail | deletion/alteration | append-only API, per-company SHA-256 hash chain, verification endpoint |

## Authentication

* **Accounts**: ASP.NET Core Identity (`AppUser : IdentityUser<Guid>`), password hashing with Identity's
  PBKDF2 hasher, lockout after repeated failures, unique emails, minimum length 10.
* **API clients / desktop**: `POST /api/auth/login` returns a short-lived JWT access token (15 minutes,
  HS256, issuer/audience validated, `sub`, `email`, `device_id` claims) and an opaque refresh token
  (256-bit random; only its SHA-256 hash is stored). Refresh rotates the token; presenting an already-used
  refresh token revokes the whole family (theft detection).
* **Browser**: the Web app uses Identity's secure cookie (`HttpOnly`, `Secure`, `SameSite=Lax`,
  antiforgery on forms). Tokens are never handed to browser JavaScript.
* **Every authenticated request** re-checks that the user is active and (for device tokens) that the
  device is not revoked (`OnTokenValidated`), so disabling a user or revoking a device takes effect at the
  next request even if an access token has not expired.
* Authentication endpoints are rate limited (fixed window per client IP).

## Authorization

* Roles are company-scoped (`CompanyMembership`): Owner, Accountant, DataEntry, Viewer. The permission
  matrix is defined once in `Domain/Companies/CompanyRole.cs` (`RolePermissions`).
* Every application use case calls `IAccessGuard.RequireAsync(companyId, permission)` before touching
  company data. Non-members receive **404** (identifiers cannot be probed); members without the
  permission receive 403.
* Every query filters by `CompanyId`; entity lookups are by (CompanyId, Id). Cross-company references
  (e.g. a voucher line pointing at another company's ledger) are rejected by validation.
* Input models are dedicated DTOs (no binding to entities → no mass assignment). All SQL is parameterized
  (EF LINQ or interpolated `ExecuteSqlAsync`, which parameterizes).

## Offline authentication and its limits

* A user can sign in offline only on a device where they previously signed in **online**. At that point
  the device stores: user id, email, a PBKDF2-SHA256 verifier of the password (600 000 iterations, random
  16-byte salt) — never the password — encrypted with Windows DPAPI (current-user scope), the user's
  memberships (replicated) and an *offline-allowed-until* timestamp.
* Offline sign-in is refused after the grace period (default **7 days** since the last online check,
  configurable on the server), after 5 failed attempts (15-minute local lockout), or when the user/device
  was marked revoked during a previous online contact.
* **Revocation window (accepted risk)**: a device that stays offline cannot learn that a user or the device
  was revoked. Such a user can keep working offline until the grace period expires, but **no change made
  after revocation is accepted by the server** (uploads are re-authorized at upload time), and the device
  locks the user out at its next online contact. Owners who need a shorter window should lower the grace
  period.
* Offline authorization uses the same `RolePermissions` matrix over memberships replicated from the
  server; devices cannot grant themselves roles because memberships are download-only.

## Secrets

* No secret is committed. `appsettings.json` contains placeholders only; the development connection string
  points at the local Docker PostgreSQL with a development-only password.
* Production requires `Jwt__SigningKey` (≥ 32 bytes) and `ConnectionStrings__Server` from environment
  variables or a secret store; the API refuses to start without them. Development generates an
  ephemeral signing key if none is configured (tokens become invalid on restart).
* The desktop holds no server secrets. Its refresh token is stored DPAPI-encrypted.

## Transport and hosting

* HTTPS redirection and HSTS in production; the desktop refuses non-HTTPS server URLs except `localhost`.
* Safe error responses: RFC 7807 problem details with stable error codes; no stack traces or SQL outside
  Development.
* Logging (Serilog) never records passwords, tokens, request bodies, headers or query strings.
  Exception messages from Identity are mapped to generic messages.

## Audit trail

* Append-only `AuditEntries` written in the same transaction as the change (explicit, not via change
  interception, so business meaning is captured). Raw SQL is not used for business writes.
* Each store keeps a per-company SHA-256 hash chain (`AuditHasher`); `GET /api/companies/{id}/audit/verify`
  recomputes it and pinpoints the first broken record. The database role used by the application should
  be denied `UPDATE`/`DELETE` on `AuditEntries` in production (see deployment notes).
* Recorded: logins (success/failure), authorization failures on sync, company/master changes with
  field-level diffs, voucher posting/reversal/cancellation, sync acceptance/rejection/conflicts,
  membership changes. Passwords and tokens are never recorded.
* Indian requirement note: the Companies (Accounts) Rules require an edit log (audit trail) that cannot be
  disabled and is retained for the statutory period (currently 8 years). TallyNext's audit trail is always
  on; retention and backup policy must be configured by the operator. Compliance has **not** been
  independently verified.

## Web sessions

* Every use case executed from the browser re-checks that the account is still active before it runs, and the
  circuit's authentication state is revalidated every minute; a disabled user cannot make further changes from an
  already open tab.
* Sign-in and sign-out pages are rendered statically with antiforgery protection; return URLs are restricted to
  same-site relative paths (no open redirects).

## Known limitations

* **Device identity is not attested.** Device ids are random but not secret. An authenticated user who learns
  another device's id could present it at sign-in and upload vouchers numbered in that device's series (only into
  companies where the user already has permission). Planned: a per-device secret issued at registration and
  required for token refresh.
* **Desktop data is not encrypted by the application** (see "Data at rest").
* **Offline revocation window** (see "Offline authentication and its limits").
* No independent penetration test or compliance review has been performed.

## Data at rest

* Server: rely on PostgreSQL storage encryption / disk encryption and database roles; backups encrypted.
* Desktop: SQLite file under `%LOCALAPPDATA%\TallyNext`. Credential verifiers and refresh tokens are
  DPAPI-encrypted. The accounting data itself is not encrypted by the application in this version —
  BitLocker (or equivalent) is required for laptops. SQLCipher is a candidate future enhancement.

## Reporting vulnerabilities

Report suspected vulnerabilities privately to the maintainers; do not open public issues.
