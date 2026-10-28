# AM-IRA-01 — Financial Advisor request access

Access Management candidate source is pinned to PR #20 head `6f92e916914e7f89975b81adb8955e70bb094e2c` and exact tree `2d394eaa75a4de97273758dbd5c10d9b1ffa5d21`. This Application candidate does not advance the reconciled tracker or claim Production acceptance; the evidence below is bounded exact-head LOCAL merge-gate evidence.

## Delivered boundary

This candidate adds one bounded Access Management journey: an authenticated Financial Advisor may request the visible, restricted `financial-advisor.assigned-customer-financial-review` target; a current Organization Administrator may review, approve or reject it.

The target is resolved by Access Management. The requester body contains only a 32-character idempotency operation ID. Tenant, requester, role, permission and authority come from the current trusted session and owner database. The browser cannot submit a different actor, tenant, permission, role or raw grant.

Approval attaches only the target's existing Financial Advisor subject eligibility:

- resource-scoped financial-profile read;
- resource-scoped supporting-document read.

These are the two owner permissions required by the existing assigned-customer review journey. This is one static business target, not an arbitrary permission bundle. Approval does not assign a customer or resource, create Consent, add administrator authority, or authorize Platform Administrator functionality. Customer assignment, exact resource grants, current customer Consent and downstream owner checks remain mandatory.

## Lifecycle and UX

Access Management persists `Submitted`, `Rejected` and `Fulfilled`. The administrator surface renders a persisted `Submitted` request as `Under review`; it does not write a redundant review state. The append-only event history records `Submitted`, then either `Rejected` or `Approved` plus `Fulfilled`.

The Financial Advisor workspace keeps the target visible while restricted. It shows Request access only when the canonical target is currently requestable, prevents repeat clicks while submitting, and renders submitted/under-review, rejected and fulfilled status. The Organization Settings navigation adds Access Requests with requester identity, business target, age, safe status, a precise grant explanation, and confirmed Approve/Reject actions. Neither surface displays credentials or internal grant payloads.

## Integrity

Requests retain tenant, requester, target, role/subject snapshot, timestamps, decision administrator and terminal state. Submission has operation replay plus one-open-request protection under a transaction lock. Decisions lock the request, reject self-review, and are safe under repeat or competing actions.

Approval rechecks administrator authority, requester activity/role/version, expiry, canonical target configuration and current grant state under the policy mutation lock. Disabled/requester-role-changed/expired/capability-removed requests reject without a grant. Role change or disablement removes request-originated subject permissions; policy reads also bind them to the original fulfilled request and Financial Advisor role.

Submission and decisions enqueue audit evidence. A new grant also advances policy and subject versions and enqueues the normal authorization-policy and tenant-user-access events. Access-request history is protected by an append-only database trigger.

## Explicit exclusions

No generalized workflow engine, arbitrary target list, multi-request bundle, Platform Administrator review, Consent decision, customer/resource assignment, AI decision or new notification channel is introduced. System Expert describes and navigates the journey only; it has no authorization decision path.

## Candidate verification

Zero-warning Release builds passed for the owner service and migrations, official owner integration-test project, embedded owner source, Database Migrator and UAT Gateway. Focused Access Management tests passed 18/18 and the isolated PostgreSQL access-request suite passed 9/9, including tenant isolation, authorization positives/negatives, submission and decision races, stale requester role/status, expiry, capability removal, administrator authority loss, grant cleanup, least-privilege runtime grants and append-only event enforcement. All 46 embedded source files matched their pinned Git blobs.

Focused Customer Web React checks passed 28/28. Full Customer Web ESLint and the changed browser specification reported zero errors and zero warnings; TypeScript, the production frontend build and bundle/lazy-isolation gate passed. The changed mock browser journeys passed 3/3. On a disposable LOCAL profile, readiness and authenticated topology were 13/13, the real Advisor-to-Administrator approval/rejection and independent assignment/resource/Consent journeys passed 5/5, and the existing persona regression passed 8/8. The fulfilled request physically contained one Submitted, one Approved and one Fulfilled event, no Rejected event, and exactly the two bounded subject permissions; the rejected request contained one Submitted and one Rejected event and no subject permission. Development GitHub Actions remained disabled.
