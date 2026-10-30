# P2-27 — Customer-reported insurance policy lifecycle

## Delivered boundary

This increment completes the first bounded lifecycle slice of `M2-WS04-E02-F02` for reviewed insurance policies.

- A Customer with current Financial Profile update authority can record a policy as `ACTIVE` or `CLOSED` with an effective date.
- A closed policy can be reopened as `ACTIVE`; the earlier closed version remains in immutable revision history.
- Status reviews are tenant-bound, customer-bound, actor-bound, retry-idempotent and protected by optimistic revision checks.
- The Financial Profile owner revalidates the immutable original Financial Fact and provenance before appending a status revision.
- The product Gateway checks self/update, Financial Profile read and document-evidence read authority before the write and again after owner readback.
- Corrections retain the current lifecycle state. A closed policy cannot be corrected to move its policy-information date after the recorded closure date.
- Existing records deserialize as `UNREVIEWED` without an invented lifecycle date.

## Customer language and evidence

The UI labels the status as customer reported. A status review records what the customer reviewed; it does not establish provider-confirmed cover, premium payment, benefit or claim eligibility, cash value, underwriting, or financial advice. Every version retains the exact source Financial Fact revision, evidence and document version.

## Deferred scope

Provider integrations, premium schedules and payment state, benefits, claims, nominations, underwriting, cash-value calculations, recommendations, property lifecycle and vehicle lifecycle remain separate journeys. This increment introduces no schema migration because the existing owner stores versioned policy receipts as governed JSON and the contract supplies backward-compatible lifecycle defaults.
