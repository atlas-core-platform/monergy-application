# P2-11 — Phase 2 financial review summary

## Bounded journey

An authenticated Customer with current report-generation and calculation access can generate a Reporting-owned financial review that combines the existing supported Financial Profile facts with one exact, coherent set of these completed Financial Rules receipts:

- asset allocation;
- monthly net cash-flow rate;
- monthly EMI share; and
- monthly debt-service coverage.

The existing basic report remains available without this selection.

## Ownership and trust boundary

- The browser submits only the four canonical calculation identifiers and a report operation identifier. It cannot post financial values, percentages, multiples, rule metadata, provenance, or export content.
- Reporting re-fetches every selected receipt from Financial Rules under the current tenant and authenticated session.
- Reporting accepts only the compiled canonical rule identifiers, versions, definition hashes and numeric semantics.
- The four receipts must belong to the current customer and actor, be in `CALCULATED` state, share exact embedded monthly sources, and use the same review date and currency across Net Worth and Monthly Cashflow.
- A missing, foreign, stale, malformed, incomplete or inconsistent receipt fails closed before report persistence.
- The persisted JSON export retains each receipt identifier, authoritative owner, rule identity/version, definition hash, numeric semantics and exact source-calculation identifiers.
- Reusing one report idempotency key with a different receipt selection is rejected as a conflict.

## Product boundary

This review is descriptive customer-reviewed information. It is not independently verified wealth, income or debt; a financial-health score; formal lender or business DSCR; a target or threshold; eligibility; underwriting; forecasting; recommendation; or advice. Reporting does not recalculate the Financial Rules results and does not invoke an AI model.

## Deferred

Formal financial packs, live provider data, model-generated analysis, configurable reporting templates, encrypted long-term report history acceptance, scoring and lending decisions remain outside P2-11.

## Validation

Source validation covers canonical receipt re-fetch, tenant/session propagation, exact source coherence, malformed/repeated identifier rejection, idempotency conflict, Reporting export provenance, governed gateway response validation, browser selection of a coherent receipt set, identifiers-only submission, TypeScript, ESLint and production build. Physical PostgreSQL and installed-browser LOCAL acceptance remain the release-acceptance gate.
