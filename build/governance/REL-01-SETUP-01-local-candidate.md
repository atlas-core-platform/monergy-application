# REL-01 / SETUP-01 — LOCAL onboarding candidate

Status: implemented candidate; physical acceptance pending. Production accepted: **no**.
Authority: AR-002 → AM-08 → UI-05 → CP-01 and the user's continuation on 2026-10-10.

## What changes

A trusted Tenant Admin first reviews organization details and starter access before the protected workspace opens. Setup records the authenticated actor, tenant, current policy revision, required acknowledgements, and durable receipt. Successful activation requires all six CP provisioning receipts and a valid setup receipt. Failure leaves the setup screen actionable; it never fabricates readiness or mounts the protected shell.

Resource Access gains named customer assignments and explicit review/removal. Advanced exact grants retain their existing contract. Role changes remove both customer assignments and individual grants. No customer Consent can be supplied by Tenant Admin authority alone.

| Boundary                 | Owner and behavior                                                                                                                              |
| ------------------------ | ----------------------------------------------------------------------------------------------------------------------------------------------- |
| Trusted identity/session | Existing C&I local reference authentication and live AM membership; no new login architecture                                                   |
| Customer identity        | C&I tenant database stores canonical customer IDs, names, owner actors, evidence and versions; runtime has read-only customer access            |
| Advisor assignment       | AM tenant database; eligible business role, admin-only mutation, optimistic version, subject invalidation, atomic audit/outbox                  |
| Business access          | Current AM capability plus verified self-ownership, or current relationship plus fresh bound Consent; no fallback to legacy authorization       |
| Consent                  | Separate tenant database, verified customer-owner grant/revoke APIs, workload-only evaluations, purpose/capability/expiry/owner-version binding |
| Document/report access   | Owning service still verifies persisted resource-to-customer ownership; an AM decision does not prove resource ownership                        |
| Setup/lifecycle          | CP files on a dedicated Docker volume; file locks, immutable provisioning receipts, atomic setup writes and idempotent replay                   |

Consent writes commit grant/revocation, version, immutable request receipt, audit and outbox in one transaction. Missing, denied, expired, stale, mismatched or unavailable owner decisions fail closed. AM does not cache an Allow. Customer self-access still requires the business capability; Tenant Admin and groups do not grant financial access.

## Scope and compatibility

- Everything newly enabled is restricted to Development LOCAL/CI. This does not advance any historic delivery gate or authorize Production.
- `-EnableOnboarding` is explicit and requires a separate named profile. Retained default profiles are never reset or retrofitted into the eight-database profile.
- AM-08 pack **1.0** is unchanged. Existing policies are never silently upgraded. UAT uses an explicitly created disposable role to exercise document/report reads excluded from starter roles.
- New C&I and Consent migrations are hash-bound in `onboarding-scope-lock.json`. The existing AM-05 migration overlay includes these exact paths; historical accepted migration counts are unchanged.
- CP's six LOCAL adapters verify actual owner results after the existing idempotent bootstrap: placement, database ownership, migration histories, initial admin principal, AM pack/admin authority, and least-privilege runtime bindings. These readback proofs are not cloud provisioning or an exactly-once external workflow claim.
- Organization metadata is operator-created LOCAL fixture data. Setup acknowledges it; this candidate does not introduce a general organization editor.
- Consent management is a bounded customer-owner API used by acceptance checks. A Production customer Consent portal, legal policy and canonical Audit delivery of the Consent outbox are not implemented or claimed.
- Positive supported routes cover the existing customer boundary map, including Evidence metadata/version reads, Search and Reporting. Business storage uses the owners' existing reference adapters. AI, unimplemented workers and unsupported routes stay blocked. Boundary probes explicitly report `operationExecuted=false`.
- CP activation is setup lifecycle state, not a replacement for live business authorization or a Production suspension enforcement design.

## Run a separate local profile

After checking out this application candidate (its UAT manifest pins the matching AM candidate), run from the repository root in PowerShell:

```powershell
.\build\uat\Invoke-MonergyUat.ps1 -Action Start -Profile onboarding -Port 4180 -EnableOnboarding
.\build\uat\Invoke-MonergyUat.ps1 -Action Credentials -Profile onboarding -Tenant T001 -Actor A900
```

Open `http://localhost:4180`. Use the displayed local credential privately. Complete the organization/access review, then use Users and Resource Access. This command uses a new profile; it does not modify the retained `default` profile. Do not run disposable fixture scripts against retained data.

```powershell
.\build\uat\Invoke-MonergyUat.ps1 -Action Status -Profile onboarding
.\build\uat\Invoke-MonergyUat.ps1 -Action Stop -Profile onboarding
```

Customer-owner API routes, through the UAT gateway:

- `GET /consent-api/local/v1/consents/customers/{customerId}`
- `POST /consent-api/local/v1/consents/customers/{customerId}/grants`
- `DELETE /consent-api/local/v1/consents/customers/{customerId}/grants/{grantId}`

They require the existing trusted tenant/session headers and verified owner actor. Grant requests bind request ID, expected Consent version, customer, advisor, supported capability IDs, `customer-advice` purpose and an expiry within 90 days. Removal requires request ID and expected version. The internal evaluation token is never sent to the browser.

## Verification and merge gates

Local checks completed during implementation: AM unit suite 61 tests; application owner-boundary/Consent nonphysical suite 43 tests; architecture/setup suite 20 tests; frontend suite 34 tests; 25 selected Chromium scenarios covering authentication isolation, Midnight regressions and onboarding. Screenshots were visually reviewed at desktop and compact widths. These are not physical Docker evidence or a complete manual accessibility assessment.

Formatting uses Prettier plus Roslyn folder whitespace formatting. Full MSBuild-backed `dotnet format` is blocked by this execution environment's named-pipe permissions; hosted formatting remains a required gate. Pinned SDK analyzer builds are separately checked. Docker/PostgreSQL are unavailable here, so new persistence and process assertions are committed, not represented as passed.

Run the following manually on the candidate branches; automatic Actions remain main-push only:

1. AM **Access Management candidate verification** — new relationship branch. Includes three added physical relationship/rollback checks and existing tenant, delivery and HTTP regressions.
2. Application **Impact-aware continuous verification** — integrated candidate, choose `impacted`.
3. Application **Tenant Access owner integration** (workflow `access-owner-integration.yml`) — integrated candidate.
4. Application **Local Docker UAT acceptance** — integrated candidate. Creates an isolated onboarding `ci` profile, runs existing owner/process checks plus `verify-onboarding.py`, then real browser administration/setup/assignment and isolated UI regression scenarios.

The integrated script tests Consent transaction rollback, customer self-access, assignment plus Consent, role cleanup, expiry/revocation without AM event delivery, owner-version changes, outage, tenant/resource tampering, durable setup replay and a full stack recreation retaining named volumes. Credential-free results go to `.artifacts/onboarding-acceptance.json`; a failed run is not acceptance. The workflow's Reset applies only to its disposable CI profile.

Review exact candidate SHAs and all required hosted results before prerequisite/integration merges. This record does not merge or accept AR-002, AM-08, UI-05 or CP-01, and does not reopen accepted Midnight PR #40.
