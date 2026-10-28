# AM-IRA-01 — Financial Advisor request access

Access Management candidate source is pinned to PR #20 head `a9157cfbd9de459bc7278b1909e15da5f0d050d6` and exact tree `a8c42abf8d4ff79e16f06e2997b7c4f0e18db22b`. This Application candidate does not advance the reconciled tracker or claim acceptance before exact-head review, physical PostgreSQL execution and retained-profile LOCAL browser UAT.

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

Zero-warning Release builds passed for the owner service, official owner integration-test project, embedded owner source and UAT Gateway. Focused requester/administrator component tests, Gateway route checks, TypeScript, ESLint, production frontend build and manifest blob verification passed. The PostgreSQL integration source covers tenant isolation, authorization positives/negatives, submit and decision races, stale requester role/status, expiry, capability removal, administrator authority loss, grant cleanup and append-only audit evidence.

Physical PostgreSQL execution is pending because this execution host has no PostgreSQL or Docker runtime. The two browser specifications are source-complete, but execution is pending because this host has no installed Playwright browser. Complete retained-profile LOCAL UAT remains an operator gate. Development GitHub Actions are disabled for this candidate.
