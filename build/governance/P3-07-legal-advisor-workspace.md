# P3-07 — Legal Advisor / Advocate workspace

## Accepted source boundary

The Legal Advisor workspace is a bounded Application & Case journey. Access Management Default Access Pack 2.11 grants only `application-case.legal-review.manage` to the `legal-advisor` role. The capability is tenant-scoped; usable customer work additionally requires an exact owner-recorded assignment, an active same-tenant customer, current Evidence read Consent, and unchanged actor/customer authority at mutation time.

Application & Case owns the assignment, inspection receipts, outcome revisions and immutable source-version snapshots. Evidence continues to own document content and versions. Consent owns customer sharing. Customer & Identity owns the customer and session. Access Management owns the role and capability. The Legal Advisor does not receive identity/KYC ownership, Consent authority, financial-profile access or financial-advice authority.

## Workflow and records

- A legal advisor lists only matters explicitly assigned to the authenticated actor.
- Each matter names canonical Evidence document references and exact assigned versions; no arbitrary document lookup exists.
- Every document must be inspected at its unchanged assigned version before an outcome may be recorded.
- Outcomes are `No issue identified`, `Action required`, or `Unable to conclude`, with human-authored findings.
- Every revision snapshots policy, subject, customer and Consent versions plus all exact Evidence document versions.
- Operation identifiers make browser/network retries idempotent; reuse with different intent fails closed.
- Customers see only the latest safe professional outcome, findings, author, time and source count—not credentials, document contents or unrelated assignments.
- PostgreSQL schema 6 gives runtime only the required state/version and inspection-column updates plus append-only revision/event/operation inserts.

System Expert may explain these boundaries and navigation. It does not inspect hidden documents, infer a legal conclusion, or approve an outcome.

## Validation boundary

Release builds, focused owner and UI tests, TypeScript, ESLint, production bundle, Gateway route checks, AM pack tests and exact embedded-source verification are required before merge. Physical PostgreSQL schema 6, retained LOCAL onboarding, installed-browser persona journeys, readiness and authenticated topology remain explicit acceptance gates on a capable host.
