# P2-03 — Customer-reviewed Investment Lifecycle

## Delivered boundary

P2-03 adds one bounded Customer journey under Financial Profile ownership. A Customer can mark an existing reviewed Investment record `ACTIVE` or `CLOSED` with an effective date. Each lifecycle review creates a new immutable Investment record revision, preserves the exact original Financial Fact/provenance/document/evidence references, and is idempotent for browser or network retries.

Corrections retain the last lifecycle status. Stale revisions fail closed. Status dates cannot precede the record's reviewed value date or a prior lifecycle review, and cannot be in the future. Closing a record removes it from asset and net-worth snapshot inputs on and after the closure date; reopening it restores eligibility while retaining the closed revision in history.

## Authorization and integrity

Read requires current self access to `financial-profile.profile.read` and `evidence.document.read`. Lifecycle writes additionally require current self access to `financial-profile.profile.update`. Tenant and Customer are resolved from the authenticated owner context, never from the request body, and authorization is reconfirmed after the owner write.

The Gateway validates the exact revision increment, actor, operation, status/date, immutable record values and immutable source lineage before returning confirmation.

## Deliberately deferred

Lifecycle status is customer reported. P2-03 does not verify ownership, trades, closure, balances or current valuation with an institution; connect to a broker, custodian or market-data provider; calculate portfolio totals, returns or net worth; or provide investment advice. Those remain separate governed journeys and provider configurations.
