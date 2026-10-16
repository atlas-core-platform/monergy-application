# T1-18 — Resume Financial Review from a Recovered Result

Status: bounded implementation candidate under AR-003 / COV-016 / COV-050, extending T1-17's durable recovered processing result.

## Purpose

Allow a Customer who returns later to a **completed** document-processing result to resume the same explicit review-and-confirm flow used by the live extraction drawer.

T1-18 does not create a second normalization rule or automatic promotion path. It reuses the existing Financial Profile promotion endpoint and makes that endpoint source its candidate facts from the durable completed-result path introduced by T1-17.

## Authorization separation

Execution, result review and Financial Profile mutation remain separate:

- run/extract a document:
  - `document-intelligence.document.process`
- read a recovered completed result:
  - `job-management.job.read`
  - `evidence.document.read`
- add reviewed facts to trusted Financial information:
  - `financial-profile.profile.update`
  - `financial-profile.profile.read`
  - `job-management.job.read`
  - `evidence.document.read`

A completed recovered review does **not** require `document-intelligence.document.process` because no extraction is being started or retried.

No new capability or Access Pack revision is introduced.

## Pre-mutation checks

Before any Financial Profile mutation, the product gateway must successfully establish all of the following:

1. current authenticated Customer ownership;
2. self-access for `financial-profile.profile.update`;
3. `financial-profile.profile.read`;
4. `job-management.job.read`;
5. `evidence.document.read`;
6. exact completed durable job/result identity;
7. selected source-fact IDs exist in that recovered result;
8. selected facts are accepted types: `INCOME` or `EXPENSE`;
9. each selected fact belongs to the current Customer and exact persisted document version;
10. no duplicate selected `factType + label` pair.

Financial Profile read authorization must be checked **before** normalization. T1-18 must not mutate Financial Profile and then discover that the caller cannot read the resulting profile.

## Durable candidate source

The promotion endpoint must no longer reconstruct candidate facts through the old live processing-status path.

It uses:

- durable Job Management `jobId`;
- Job Management persisted `outcomeReference` as processing ID;
- persisted `payloadReference` as exact document-version ID;
- the T1-17 Job Management → Document Intelligence read-only completed-result projection.

This preserves review continuity after the live extraction drawer has been closed or the browser has been reloaded.

## Customer experience

When a recovered result is opened:

- if the Customer has Financial Profile read/update authority:
  - accepted candidate facts may be individually selected;
  - the Customer explicitly presses **Add selected information**;
  - only selected IDs are submitted;
  - success updates the local Financial Profile projection;
  - selections are disabled after confirmed success.
- if Financial Profile update or read authority is absent:
  - the recovered result remains read-only;
  - no selection controls are shown;
  - no promotion request is made.

The recovered review state is separate from the live extraction drawer's selection/promotion state.

## Mutation and idempotency

T1-18 reuses the existing:

`POST /product-api/v1/financial-profile/from-processing/{jobId}`

endpoint and its operation ID.

Normalization remains owned by Financial Profile and remains idempotent/conflict-aware under the existing `NormalizeSourceFacts` contract.

T1-18 does not change:

- Financial fact identity rules;
- source/provenance creation;
- audit behavior;
- duplicate/conflict semantics;
- normalization version.

## Truthfulness boundaries

Recovered candidate facts remain **candidate information** until explicit confirmation succeeds.

A completed processing run does not mean:

- all extracted information is correct;
- all candidates should be added;
- the Financial Profile is complete;
- the information is verified/certified;
- a score, recommendation or advice exists.

## Explicit exclusions

T1-18 does not implement:

- automatic promotion after processing;
- select-all by default;
- recovered-result editing;
- retry/replay/reprocessing/cancellation/reconciliation;
- new financial fact types;
- deduplication beyond the existing reviewed fact rules;
- professional verification;
- Financial Health scoring, advice or recommendations;
- Production queue/HA/DR claims.

## Acceptance evidence

Focused evidence must prove:

- a recovered completed result with Financial Profile read/update authority exposes explicit selection controls;
- selected recovered facts use the existing promotion endpoint;
- success returns the updated Financial Profile and confirmed count;
- recovered review remains read-only without Financial Profile update authority;
- no execution permission is required by the recovered promotion server path;
- all required read/update permissions are established before normalization;
- LOCAL/UAT promotion occurs after the durable completed-result read and remains idempotent/conflict-aware.

No additional manually-triggered hosted CI cycle is required solely for this bounded candidate. Execution evidence and product acceptance remain separate CTO decisions.
