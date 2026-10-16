# T1-16 — Action Centre Processing Attention

Status: bounded implementation candidate under AR-003, extending the accepted T1-12 Action Centre with the durable read-only processing owner accepted in T1-14.

## Purpose

Surface durable document-processing state in Action Centre without turning Job Management runs into tasks, recommendations, support actions or a new workflow owner.

T1-16 consumes only the existing Customer product API:

`GET /product-api/v1/processing-activity`

introduced by T1-14.

No new capability, permission, service, contract, database, queue or event is introduced.

## Owner and access boundary

- Job Management remains authoritative for durable processing state.
- The product gateway remains responsible for the bounded Customer projection.
- Access remains governed by the already-accepted `job-management.job.read` grant in Default Access Pack 2.4.
- Action Centre never calls Job Management directly.
- Action Centre never accepts a browser-supplied customer identity.

## Processing attention rules

T1-16 derives at most two aggregate attention items from the **recent processing activity returned by the bounded product API**:

1. **Processing still active**
   - States: `Scheduled`, `Running`, `CancellationRequested`.
   - Count is the number of processing **runs**, not a claim about unique documents.
   - Presentation is one aggregate attention item regardless of run count.
   - Allowed action: navigate to Documents → Processing activity.

2. **Processing couldn't complete**
   - State: `Failed`.
   - Presentation is one aggregate attention item regardless of failed-run count.
   - Allowed action: navigate to Documents → Processing activity.

The following terminal states do not create Action Centre attention:

- `Completed`
- `Cancelled`

A failed run remains a processing-state fact only. T1-16 does not infer the cause, financial impact, retryability, urgency or recommended remediation.

## Bounded recent-history semantics

T1-14 exposes a recent, newest-first bounded processing window rather than exhaustive lifetime job history.

Therefore:

- absence of processing attention means only that no attention state was found in the currently returned recent activity;
- the Action Centre must not claim that no older failed processing run exists;
- clear-state language must refer to the **currently loaded supported state**, not lifetime history;
- the processing summary label is **Recent processing attention**.

## Action Centre completeness

The Action Centre clear state is permitted only when all three accepted input families are available and successfully loaded:

- Documents;
- trusted Financial information;
- recent processing activity.

If `job-management.job.read` is outside current access, or the processing owner cannot be refreshed:

- processing attention is unknown, never zero;
- Action Centre must not render an all-clear state;
- existing known Document/Financial state may remain visible with a warning.

## Presentation limits

The summary card may display the number of recent processing runs requiring attention:

`active runs + failed runs`

The attention list may display at most:

- one aggregate active-processing item;
- one aggregate failed-processing item.

This is not a persisted task count.

## Explicit exclusions

T1-16 does not implement:

- one Action Centre task per processing job;
- task persistence, completion, assignment, due dates or history;
- retry, replay, cancellation or reconciliation;
- support escalation;
- failure-code or diagnostic presentation;
- urgency, severity, priority or risk ranking;
- notifications or reminders;
- automatic remediation;
- exhaustive/lifetime processing-history claims.

Future recovery/support actions require their own owner contracts, permissions and product acceptance.

## Acceptance evidence

Focused component evidence must prove:

- empty recent processing activity is a known zero when the owner call succeeds;
- Scheduled/Running/CancellationRequested aggregate into one active-processing attention item;
- Failed runs aggregate into one failed-processing attention item;
- Completed/Cancelled runs create no processing attention;
- the processing summary counts runs requiring attention;
- Job Management unavailability does not become zero or all-clear;
- missing `job-management.job.read` does not call the processing endpoint and does not create an all-clear state;
- actions link only to the existing read-only Documents processing activity.

No additional manually-triggered hosted CI cycle is required solely for this bounded candidate. Execution evidence and product acceptance remain separate CTO decisions.
