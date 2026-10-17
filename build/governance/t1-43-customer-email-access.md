# T1-43 — Customer email-code entry for an existing organization workspace

Application base: `4ced1c28f62a73fd7759e623b55253b210f0cbf8`. Requirements: MON-ID-002 partial, MON-ID-001 activation prerequisite only, MON-ID-006/009/011, MON-SEC-006/007, MON-AUD-001/003; AR-003 COV-002/003; M2-WS02-E01-F01/F02 partial. Standing 17 October end-to-end implementation directive authorizes this bounded development unit and autonomous merge; no release or production identity-provider selection is made.

## Functional outcome

An already imported identity with exactly one active, verified customer ownership can request a six-digit email code and open its existing financial workspace after verification. The UI presents this entry method only when the C&I owner enables it. The existing organization/reference-key method remains available; administrator entry never offers the customer email method. Product context and capabilities are verified before the private shell appears.

C&I owns the challenge, durable encrypted delivery queue, confirmation, session and audit records. Existing Access Management owns current membership and roles; no role, membership, customer or ownership is created by a code request. No public self-registration, arbitrary directory lookup or ability to choose an actor is introduced. Full registration/profile/terms/settings, SMS, password/PIN/MFA and production provider acceptance remain open.

## Security and persistence

- Cryptographically random 256-bit challenge identifiers and uniformly generated six-digit codes. The database stores a challenge hash and keyed HMAC verifier, never a raw code or session credential.
- AES-GCM encrypted delivery payload binds the tenant and challenge hash; separate derived encryption/verifier keys use an operator-supplied 32-byte secret. Payloads are cleared after delivery, consumption, attempt exhaustion or expiry cleanup.
- Fixed five-minute expiry, five failed attempts, one-minute resend cooldown and five requests per recipient/hour. Request/confirmation routes share a bounded per-process 120/minute limiter. These are development-profile controls, not a production capacity/DDoS assertion.
- Request response shape is identical for unknown, unowned, ambiguous and rate-suppressed recipients. SMTP runs asynchronously, so mail delivery latency is not exposed as account enumeration. No address/code/credential is written to application logs or browser storage.
- A single conditional owner UPDATE consumes the delivered challenge and increments attempts; competing confirmations cannot both consume it. Resend replaces the previous challenge. Owner/database binding is checked for every repository operation; one C&I database per tenant is retained.
- Membership is checked before and after session issuance, with customer ownership/version checks around issuance. Sessions created for a failed/cancelled owner verification are revoked. Request, delivery, code and issued/denied-session events are C&I-owned append-only runtime audit records; this does not claim completion of the platform-wide audit matrix.
- SMTP requires authenticated STARTTLS using a configurable transport. No vendor is selected, no email delivery is simulated in the product and no code-retrieval endpoint is exposed. Disabled/incomplete configuration cannot present a working email entry path.

## Run in an existing local onboarding profile

The migration is additive: `services/customer-identity/migrations/0004_customer_email_access.sql`. Local bootstrap applies it and grants runtime SELECT/INSERT/UPDATE for challenge state, and SELECT/INSERT only for email audit events. The new hash-checked T1-43 overlay preserves historical migration cohorts and scope locks. The service and endpoints remain inside the existing Development LOCAL/CI tenant-integration boundary.

Set operator environment variables `MONERGY_EMAIL_SMTP_HOST`, `MONERGY_EMAIL_SMTP_USER`, `MONERGY_EMAIL_SMTP_PASSWORD`, `MONERGY_EMAIL_FROM`, and optionally `MONERGY_EMAIL_SMTP_PORT` (default 587). Then use:

```powershell
./build/uat/Invoke-MonergyUat.ps1 -Action Start -Profile onboarding -EnableEmailAccess
```

The named profile must already have onboarding enabled; for a new profile also pass `-EnableOnboarding`. Each Prepare/Start that enables email access requires the delivery environment. Credentials are written only to the existing protected local profile configuration, never to repository files or console output. The profile creates and retains a separate random email-access secret. Use an owned, imported customer address; an arbitrary imported member without verified customer ownership cannot receive an access code or create a customer through this endpoint. Delivery assumes an actual configured mail server; this continuation does not send messages to real recipients.

## Verification receipt

Final focused native run: **30 backend tests passed** (22 email-access cases and 8 existing protocol/session cases), including real loopback HTTP routing/model binding/origin/rate-limit checks and parallel competing confirmations against the test adapter. **17 customer component checks passed** in the final sequential run (10 new email-entry checks, 6 administration access regressions and the existing customer home check). The initial concurrent administration import run timed out; the final affected set passed with one worker. TypeScript and lint for every changed source/test passed. Customer Vite build, UAT Gateway build and Database Migrator build passed; both .NET builds reported zero warnings/errors. Migration scope hash matches the SQL source. No new package dependency.

Focused verification is recorded at the final candidate revision before publication. Native protocol and HTTP tests use an explicit in-memory test adapter and captured sender; production DI registers only the PostgreSQL repository and actual SMTP transport. The tests do **not** establish execution of the PostgreSQL queries, SMTP delivery to a live server or product acceptance. Live owner-backed PostgreSQL UAT remains NOT RUN because the retained workspace cannot create PostgreSQL's required non-root process. PowerShell orchestration is source reviewed; PowerShell is unavailable here. No development GitHub Actions.

The paired System Expert deliverable must include this exact source candidate/merge, ownership/security/data/API/UI relationships, native evidence and remaining registration/provider/UAT limits before the unit is reported complete.
