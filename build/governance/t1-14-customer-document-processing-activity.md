# T1-14 — Customer Document Processing Activity

Status: bounded implementation candidate under AR-003 / COV-050 and the accepted Document-processing foundation.

## Purpose

Make the Customer's existing document-processing status recoverable after the transient review drawer is closed or the page is reloaded.

T1-14 exposes a read-only projection of the Customer's recent durable **document-processing** jobs. Job Management remains the job-state authority; Evidence remains the document authority; Document Intelligence remains the processing owner.

## Access boundary

T1-14 introduces one new Customer grant through Default Access Pack 2.4:

- `job-management.job.read`

The grant is tenant-scoped because the product gateway and Job Management both resolve/enforce the current Customer boundary before returning data. It does not grant organization-wide job browsing.

The read path must:

1. resolve the current session and owned Customer on the server;
2. freshly evaluate `job-management.job.read`;
3. call Job Management through the workload-only LOCAL/UAT control surface;
4. re-evaluate `job-management.job.read` at Job Management;
5. list only jobs whose `customerId` is the current Customer and whose `jobName` is exactly `document-processing`.

The browser never supplies customer identity as authority.

## Returned customer fields

The product projection may expose only:

- job ID;
- exact document version reference (`payloadReference`);
- state;
- attempt number;
- updated time.

The product projection must not expose:

- failure code;
- retryability;
- lease owner/token;
- reconciliation state;
- idempotency key;
- raw security context;
- operational outbox/audit diagnostics.

## Product presentation

Documents shows a **Processing activity** section with recent jobs newest first.

Customer-facing state language is bounded:

- Scheduled → Queued
- Running → Extracting details
- CancellationRequested → Stopping
- Completed → Completed
- Failed → Couldn't complete
- Cancelled → Cancelled

When the exact document version exists in the already-authorized Evidence library, the customer may open the existing Document details view. Processing activity does not itself grant Evidence access.

## Failure semantics

- Missing `job-management.job.read` → activity is unavailable because it is outside the current access package.
- Job Management failure → show unavailable; never show an empty-history state.
- Successful known empty list → “No document processing activity yet.”
- A failed job remains a failed state; it is never presented as completed or as a financial-information failure.
- Processing status does not imply that any extracted fact was accepted into Financial Profile.

## Explicit exclusions

T1-14 does not implement:

- customer cancel, retry, replay or reconciliation actions;
- Operations Support controls or organization-wide job views;
- failure-code diagnostics;
- notifications/reminders;
- persisted Action Centre tasks;
- automatic reprocessing;
- production queue selection/HA/DR;
- new document-processing types beyond the current bounded journey.

Future recovery/support actions require their own authorization model, product workflow and acceptance.

## Acceptance evidence

Focused evidence must prove:

- durable customer/job-name filtering and newest-first ordering;
- cross-customer list isolation;
- product response omits retry/failure internals;
- completed activity can be recovered after processing;
- failed activity remains visibly failed and read-only;
- Job Management outage is not rendered as empty history;
- missing job-read access does not call the activity endpoint;
- matching Evidence document can be opened only through existing Evidence access.

No additional manually-triggered hosted CI cycle is required solely for this bounded candidate. Execution evidence and product acceptance remain separate CTO decisions.
