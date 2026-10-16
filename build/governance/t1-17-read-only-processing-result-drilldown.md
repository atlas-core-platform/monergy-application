# T1-17 — Read-only Processing Result Drill-down

Status: bounded implementation candidate under AR-003 / COV-014 / COV-050, building on the accepted T1-14 Customer processing activity.

## Purpose

Allow a Customer to reopen the extracted candidate information produced by a **completed** document-processing run after the live extraction drawer has been closed or the page has been revisited.

This is a read-only recovery view. It does not re-run extraction and it does not provide a second path to promote facts into Financial Profile.

## Authorization separation

Execution and read-only recovery remain separate capabilities:

- scheduling/running extraction: `document-intelligence.document.process`;
- processing-status ownership/read: `job-management.job.read`;
- recovered candidate content also requires `evidence.document.read`.

T1-17 does **not** make `job-management.job.read` an execution grant.

The product gateway must freshly authorize both:

1. `job-management.job.read`;
2. `evidence.document.read`.

Job Management must independently re-evaluate both capabilities before returning recovered content.

No new capability or Default Access Pack version is introduced.

## Durable identity

The old live-status path depends on the transient document-processing intent registry and remains appropriate for the active extraction flow.

T1-17's recovered result must not depend on that registry.

For a completed durable Job Management record:

- `jobId` identifies the durable job;
- `payloadReference` is the exact Evidence-owned document version;
- persisted `outcomeReference` is the completed Document Intelligence processing ID.

Job Management uses the durable `outcomeReference` + `payloadReference` + Customer ID to request the completed result from Document Intelligence.

## Document Intelligence read projection

Document Intelligence exposes a workload-only LOCAL/UAT read projection.

The read projection:

- accepts only the trusted Job Management workload;
- validates processing ID, document-version ID and Customer ID;
- reads the existing processing repository directly;
- does **not** invoke extraction;
- does **not** invoke the execution policy;
- returns facts only for a `Completed` processing record;
- fails closed on customer/document identity mismatch.

The absence of an execution-policy call on this path is intentional because execution authorization was already separated from read authorization. Job Management is the read-authorizing owner for this bounded projection.

## Customer product response

The Customer product response may expose only:

- job ID;
- document-version ID;
- state = `Completed`;
- updated time;
- bounded candidate facts:
  - source fact ID;
  - fact type;
  - label;
  - candidate value;
  - currency;
  - effective date;
  - source location;
  - confidence;
  - document-version ID;
- temporary-data label.

The product response must not expose:

- Customer ID;
- Evidence ID;
- extraction/validation implementation versions;
- failure code;
- retryability;
- Job Management lease/reconciliation/idempotency data;
- raw security context.

## Fact-type boundary

T1-17 accepts only currently supported candidate types:

- `INCOME`
- `EXPENSE`

The gateway must reject duplicate source-fact IDs, invalid confidence, invalid identifiers/labels or future/unaccepted fact types.

A future extractor cannot silently expand this experience.

## Product presentation

Only a **Completed** processing-activity row may offer **View result**.

The recovered result drawer is explicitly read-only:

- it may show label, type, value, effective date, source location and confidence;
- it may open the already-authorized Document;
- it may expose job/version IDs under Technical details;
- it must not show fact-selection checkboxes;
- it must not show **Add selected information**;
- it must not offer retry, replay, cancellation or reconciliation.

Promotion to Financial Profile remains available only in the explicit live review/confirmation flow.

## Failure semantics

- missing job-read or Evidence-read authority → 403 / no recovered-result action;
- cross-customer durable job → 404/non-disclosure;
- non-completed job → result not ready;
- missing durable outcome or missing exact Document Intelligence processing result → not found/unavailable;
- malformed or mismatched owner result → fail closed;
- a failed processing run remains status-only and does not expose a recovered candidate result.

## Explicit exclusions

T1-17 does not implement:

- reprocessing;
- retry/replay/cancel/reconcile;
- recovered-result promotion;
- editing extracted candidates;
- failed-run diagnostics;
- Operations Support controls;
- exhaustive processing history;
- OCR/AI expansion;
- new financial fact types;
- Production queue/HA/DR claims.

## Acceptance evidence

Focused evidence must prove:

- a completed activity row can reopen the exact recovered result;
- recovered facts are read-only and contain no promotion controls;
- job-read alone is insufficient without Evidence read;
- failed activity has no recovered-result action;
- result identity matches the durable job and document version;
- LOCAL/UAT enforces unauthenticated/admin/cross-customer denial;
- LOCAL/UAT response excludes customer/Evidence/implementation/failure/retry internals.

No additional manually-triggered hosted CI cycle is required solely for this bounded candidate. Execution evidence and product acceptance remain separate CTO decisions.
