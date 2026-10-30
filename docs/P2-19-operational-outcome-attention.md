# P2-19 — Operational outcome attention

P2-19 adds a bounded Organization Administrator review signal for product requests whose owner outcome is explicitly unconfirmed. Audit owns the immutable request/result evidence and the derived tenant review view. The service that owns the requested operation remains the business-outcome authority.

## Governed behavior

- Audit counts only product activity records in the authenticated administrator's tenant whose phase is `RESULT` and outcome is `UNKNOWN`.
- One or more unconfirmed outcomes in the exact previous 15 minutes produce `REVIEW_NEEDED`; none produce `NO_CURRENT_SIGNAL`.
- The administrator can open the exact evidence window with the existing `UNKNOWN` outcome filter.
- Current Organization Administrator authority is checked before and after the owner read.
- Tenant boundaries, append-only evidence, route categories and request/result meaning remain unchanged.

## Truthful outcome boundary

`UNKNOWN` means that Monergy could not confirm the operation owner's outcome. It does not mean the operation failed or succeeded, does not prove that a durable job completed and does not authorize a retry. The owning service remains responsible for reconciliation and business status.

The signal contains counts and timestamps only. It does not copy request bodies, owner payloads, credentials, session secrets, financial values, documents, Consent decisions, IP addresses or device fingerprints.

## Deferred scope

No automatic replay, rollback, failure classification, incident case workflow, notification delivery, configurable threshold, cross-tenant view or generalized SIEM is introduced. Physical PostgreSQL, installed-browser and retained LOCAL acceptance remain separate gates.
