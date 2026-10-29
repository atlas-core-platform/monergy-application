# P2-12 — Durable financial report generation

## Scope

P2-12 implements `MON-REP-005` for the existing Customer basic financial summary and the exact
Phase 2 financial-review receipt selection. Reporting remains the report-content authority. Job
Management owns scheduling, retry state and safe lifecycle status. Financial Profile and Financial
Rules remain the authorities for the facts and calculation receipts included by Reporting.

The increment does not add a formal report pack, new calculation, score, recommendation, AI
analysis, provider integration or notification channel.

## Governed flow

1. Product Gateway resolves the authenticated Customer and checks
   `reporting.report.generate` for that Customer resource.
2. Reporting records an immutable operation intent containing the canonical Customer, actor,
   operation identifier, exact optional receipt identifiers and payload fingerprint.
3. Reporting schedules one `report-generation` job with Job Management. Browser/network replay of
   the same operation is idempotent; changed receipt selections conflict.
4. Job Management re-evaluates current `reporting.report.generate` authority before every execution
   attempt. A disabled actor, removed grant or changed Customer boundary therefore fails closed.
5. Job Management invokes Reporting's workload-only execution endpoint. Reporting independently
   re-evaluates current authority and loads the immutable intent; the job cannot post report facts or
   replace the selected receipts.
6. Reporting uses its existing source-connected generation application and deterministic report
   identity. Job Management retains the terminal result reference.
7. Product Gateway exposes bounded job status. Customer Web displays Scheduled/Running state,
   prevents duplicate submission, and reads the generated report only after completion.

## Integrity boundaries

- Tenant, Customer and actor are resolved from trusted session context, not browser payloads.
- Operation identifiers are 128-bit lowercase hexadecimal values; job and report identifiers are
  server-derived.
- The Reporting intent table grants runtime `SELECT` and `INSERT` only. No update/delete privilege
  is introduced.
- Job scheduling and report generation each retain their existing audit/outbox behavior.
- Status and report reads re-check current read authority and do not enumerate another Customer's
  work.
- The synchronous `CID-051/v1` contract remains for accepted reference/persistence compatibility;
  Product Gateway uses the new job-backed `CID-051/v2` path.

## Validation and acceptance boundary

Release builds, Reporting and Job Management source tests, focused Customer Web tests, lint,
TypeScript and production bundle checks are source gates for this increment. Physical PostgreSQL,
installed-browser and retained LOCAL UAT remain explicit acceptance gates; passing source checks do
not convert those gates into production acceptance.
