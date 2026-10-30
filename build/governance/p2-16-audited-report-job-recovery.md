# P2-16 — Audited report-job recovery

P2-16 applies the governed product-audit boundary to the Organization Administrator report-job recovery delivered by P2-15.

## Ownership and boundary

- Job Management owns the recovery lifecycle and its current before/after job state.
- Reporting owns report generation and the eventual report outcome.
- The Product Gateway records a minimized request/result boundary receipt through the Audit owner.
- The Customer Web only renders the Audit owner's governed evidence.
- Consent remains a separate owner and no Consent decision is copied into this receipt.

The canonical action is `JOB_RECOVERY`. The safe resource reference is the exact job identifier from the bounded administrator recovery route. The result receipt binds the exact response tenant, job and unchanged before/after Customer. Any mismatch fails closed.

## Integrity behavior

- The request receipt must be acknowledged before the owner operation executes.
- The result receipt must be acknowledged before the buffered success response is released.
- A lost or unavailable result receipt never causes automatic business-operation replay or invented rollback.
- `HTTP_SUCCEEDED` describes only the synchronous boundary response. It is not evidence that report processing completed.
- Request body, review reason, operation identifier, report intent/payload, credentials and Consent details are not retained.

## Deliberate scope

This increment covers report-job recovery only. It does not generalize arbitrary producer telemetry and does not change the report-job or Reporting owner contracts. Physical PostgreSQL, installed-browser and retained LOCAL acceptance remain separate deployment gates.
