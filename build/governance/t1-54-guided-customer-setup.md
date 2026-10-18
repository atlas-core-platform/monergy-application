# T1-54 — Guided existing-customer setup

Base Application `4b3c681aa7889086bb6a8155f9bfe710a051e85f` (PR103), Expert
`eae73e9744528dcc0beb767fd42aee0e63a71005` (PR55), Architecture
`90934b51b2b203795acf95da2252a2cf1a1d1f4c`. Branch
`work/t1-54-guided-customer-setup`.

## Implemented boundary

Master §§5/39/41, MON-ID-001–005 and AR-003 COV-002/006 motivate the
registration journey. This bounded increment guides existing authenticated owned
customers through prerequisites and gates their LOCAL financial-workspace ingress
when explicitly enabled. It does not create a customer, complete public signup,
implement KYC, or establish a universal domain-wide activation policy.

C&I derives readiness on every read from current exact tenant/actor/customer
records: nonfuture email verification, a saved nonempty display-name profile,
and acceptance of the current available published account-agreement revision.
Receipt revision, operation identity and publication/acceptance times are checked.
Current session authority is checked before and after browser reads; ownership is
rechecked after dependencies. A new agreement revision makes readiness false
until fresh explicit acceptance. No browser completion flag, new activation
record, schema/migration, ownership/profile version or permission change exists.

`GET /local/v1/me/workspace-setup` derives identity from the session and is
same-origin/no-store. A separate workload-authenticated internal endpoint may
select actor for the Gateway. The existing owner response includes a required
readiness field; missing or malformed fields fail closed in the client.
Product context may read setup identity and exposes no financial capabilities
while pending. Normal financial product endpoints enforce readiness through the
owner client. The alternate raw financial-contract proxy ingress uses the same
admission check. Private service/background ports are not universally gated;
normal downstream authorization remains required.

The UI intercepts financial deep links before mounting the financial shell,
shows server-derived steps and reuses profile/agreement components. Explicit
agreement receipt and fresh owner readback precede a context refresh. Profile,
sharing controls, sessions and sign-out remain available while setup is pending.
Context scope, readiness logic and capability consistency are validated before
browser authorization. Session/customer changes reset setup view state.

## Configuration and verification

`-RequireCustomerSetup` on the LOCAL UAT launcher writes
`Monergy.CustomerWorkspaceSetup.Required=true` for C&I, requiring both
`-EnableOnboarding` and `-EnableEmailAccess`. Default is false. Required setup
also refuses owner initialization without email verification configuration.
Operator-published approved legal text is required; nothing seeds it. Detailed
usage: `build/uat/Customer-Workspace-Setup.md`.

**176 backend/session checks pass**, including25 new setup/admission cases and
agreement/profile/email/self-session/sharing/protocol/tenant regressions. Actual
loopback HTTP verifies browser authentication/origin/cache controls, distinct
workload credentials and denied/allowed raw-contract downstream execution.
Prerequisite, stale publication, invalid receipt, ownership and malformed owner
response cases use explicit repository/HTTP dependency doubles, not physical SQL.

**47 UI checks pass**, including9 guided setup/context cases and existing
agreement/profile/email/App regressions. They verify blocked financial deep links,
no financial data requests while pending, profile refresh without premature
admission, explicit acceptance/readback then fresh admission, retained controls,
refresh failure and malformed context rejection. TypeScript, changed source/test
lint and Vite pass. C&I and Gateway builds pass with zero warnings/errors. No
package/lockfile or development GitHub Actions change/run.

Physical PostgreSQL persistence/replay/concurrency/grants, actual SMTP delivery,
PowerShell/Compose and integrated browser-to-owner UAT are **NOT RUN**. Live
Docker/ClamAV, broader document coverage, legal/business/production acceptance,
public registration/account creation/basic KYC and complete Settings remain open.
.NET SDK10.0.401 matches the pin; available Node24.19.0 does not prove Node24.21.

The original29 deliverables remain0 complete,17 partial,12 pending journeys;
registration remains Pending because its requested public journey is incomplete.
After paired merge,54 numbered units comprise53 implementation units and1
governance closure (T1-04). Original151 requirement IDs,81 features and126
Phase1-applicable memberships are unchanged. Exact final Application source/tree
and synchronized compilation are recorded in the paired Expert receipt.
