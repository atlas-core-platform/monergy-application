# T1-53 — Customer account agreements

Base Application `499a68ff91d6fd2488465c2b87edd6fbb08cb3f8` (PR102); Expert
`6e0e296e78c82027f610c33115f0f0516ae19bbd` (PR54). Architecture
`90934b51b2b203795acf95da2252a2cf1a1d1f4c`. Branch
`work/t1-53-customer-account-agreements`.

## Planned scope

Master §§5/39/41, MON-ID-004 and AR-003 COV-002/006 require registration
agreements and customer control. This is the next bounded identity prerequisite:
an **existing owned customer** can review published, versioned terms, privacy and
processing-consent documents and record three explicit affirmative choices.
It does not deliver public signup, account creation, activation enforcement, KYC,
account closure or a new role/ownership/provider/delegated-consent authority.

`Account agreements` is protected at `/settings/agreements` under Your account.
Operator configuration supplies exactly three tenant-specific plain-text
documents, each with version/title/body/UTF-8 SHA256 and a UTC publication time.
The owner derives the revision from the full canonical publication. A missing or
future publication exposes no acceptance action; invalid or hash-mismatched
configuration fails startup. No legal content or acceptance is fabricated.
`-AccountAgreementFile` feeds the existing protected LOCAL UAT configuration.

GET/POST `/local/v1/me/account-agreements` derives the tenant/session/actor and
exact currently owned customer through the existing identity authority. Origin,
strict JSON and bounded mutation controls apply. All three choices, an operation
UUID and the exact current revision are required; callers cannot set identity,
policy content, role or acceptance timestamp. Current membership/ownership are
rechecked after dependencies and writes. A denial after commit does not pretend
that the write rolled back. Existing ownership/profile versions and Consent
grants do not change.

The additive C&I migration creates immutable publication snapshots and acceptance
rows in each tenant's database. A transaction checks exact ownership, serializes
per customer, binds operation/actor/customer/revision, and inserts the snapshot
and three choices atomically. Exact replay returns the original receipt;
conflicting operation reuse or a second operation on the already accepted
revision fails409. Runtime can insert/read, never update/delete/truncate these
records. Acceptance time is normalized to PostgreSQL microsecond precision so
the fresh readback matches the returned receipt exactly. A hash-checked migration
overlay and bootstrap/runtime grants are included.

The browser verifies customer binding, exact text hashes, current revision and
receipt/readback identity/time. Documents render as plain text, checkboxes begin
unchecked and every choice is required. It does not auto-retry a mutation after
uncertainty: explicit reload/review is required. API/customer/session changes or
unmount suppress late content and save responses even if abort is ignored.

## Verification

**103 backend/session checks pass**, including19 new catalog/owner/HTTP cases,
profile/email/self-session and protocol regressions. Actual loopback HTTP tests
cover authentication, strict scope payloads, no-store, cross-origin denial and
explicit acceptance; persistence is an explicit dependency double. Publication
cases cover missing/other-tenant/future documents, exact content hashes, version/
tenant changes, isolated snapshots and malformed configuration. Owner cases cover
missing customer, tenant/session mismatch, stale revision, missing explicit
choices, post-write ownership loss/customer switch and receipt precision.

**39 UI checks pass**, including15 account-agreement cases plus profile/email,
workspace and App regressions. They cover exact published text, no default
consent, explicit payload, current receipts/readback, unpublished content,
foreign/hash/duplicate/receipt rejection, uncertain-save reload, anonymous gate,
and late read/save suppression across customer/API changes.

TypeScript and all changed frontend source/test lint pass. Vite and zero-warning
Gateway/Database Migrator builds pass; owner/services compile with the tests.
Shell syntax, JSON membership/count consistency, migration source hash and diff
checks pass. SDK10.0.401 matches its pin; Node24.19.0 is available. No dependency,
lockfile or development GitHub Actions change/run.

## Carry-forward acceptance and counts

Physical PostgreSQL snapshot/transaction/replay/concurrency/rollback/grant checks,
PowerShell/Compose execution, published legal content and integrated browser-to-
database UAT are **NOT RUN**. No SMTP, live scanner, production/legal or business
acceptance is inferred. Account activation gating and public registration remain
separate planned work; this page does not block or activate existing access.

The approved original29-deliverable classification remains0 complete,17 partial
and12 pending business journeys. After paired merge,53 numbered units comprise
52 implementation units and1 governance closure (T1-04). This is not53 completed
requirements. The original151 IDs/81 features and126 Phase1-applicable membership
remain unchanged. Current source evidence is in `phase1-deliverable-status.json`;
the pre-T1-52 counting snapshot is historical. Exact final App source/tree and
mandatory Expert synchronization are recorded in the paired Expert receipt.
