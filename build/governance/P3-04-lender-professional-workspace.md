# P3-04 — Lender Professional workspace

## Implemented journey

An authenticated Lender Professional can open only applications explicitly assigned to that
actor by the Application & Case owner, review the bounded required-document checklist, mark a
present document reviewed and record one lender-owned decision:

- Approved
- Declined
- More information required

The decision requires every required document to be present and reviewed. Customer-reviewed
financial summaries and calculations are supporting evidence only; Monergy does not infer
underwriting, eligibility, pricing, creditworthiness or a credit score.

## Authority and ownership

Access Management Default Access Pack 2.8 adds only
`application-case.lender-application.review` to the Lender Professional role. That tenant-scoped
capability is admission to the workspace, not an application assignment. Application & Case is
the sole owner of the current lender/application binding, required-document state and recorded
decision. The browser cannot post a tenant, actor or assignee.

Every read and mutation re-evaluates the current Access Management role and capability and the
current explicit assignment. Changed role, disabled actor, changed policy/subject version,
removed assignment, stale application version and cross-tenant access fail closed. Customer,
Financial Profile, Evidence and Consent authority are not implied by the lender capability.

## Integrity and persistence

- Mutations require a bounded actor-scoped operation identifier and exact expected version.
- Operation replay returns its prior result; conflicting reuse and concurrent stale writes fail.
- Document-review and decision events plus operation receipts are append-only.
- Only bounded document review columns and lender-decision projection columns are mutable by
  the runtime role; application identity and assignment are owner controlled.
- Tenant binding and Application & Case schema version 3 are checked exactly at runtime.
- LOCAL onboarding creates illustrative T001/T002 assigned applications without changing
  retained production claims.

## Deferred

Upstream application assignment, lender-provider submission, provider verification, pricing,
credit scoring, automated underwriting, arbitrary decision workflows and production acceptance
are not part of P3-04. Physical PostgreSQL, retained LOCAL and installed-browser acceptance remain
explicit evidence gates after source merge.
