# T1-68 — Reviewed loan status and reopen history

Application base `97f2fa10b67df910f1d1c1a67c5779e793b6bf6b` (PR118), Expert base
`113edd76d4a87010e0843c6d8c8b81583a43ede8` (PR70), Architecture
`90934b51b2b203795acf95da2252a2cf1a1d1f4c`. Branch
`work/t1-68-reviewed-loan-lifecycle`. Tracker version10 reviewed through T1-67.

This continues the planned MON-FIN-006 / M2-WS04-E01-F03 loan lifecycle. Master
section13 defines loan records; section31 calls for identifying active loans.
The D04 Feature-Realization-Matrix explicitly requires lifecycle/revision tests
for this feature. This unit implements customer-reviewed dated status, rather
than inferring active debt from source figures or inventing lender verification.
It does not implement natural-language active-loan queries or total EMI/debt.

## Behavior and boundaries

Existing and newly created records initially show Status not reviewed. Legacy
creation/correction payloads without lifecycle fields decode as UNREVIEWED with
no status date, retaining their original version. No record is presumed active
or closed from its balance, EMI, contractual end date or source statement.

Financial information > Loan information offers explicit Review loan status.
The customer chooses Active or Closed and supplies the status-as-of date. Moving
a Closed record to Active explicitly reopens the customer-reported record; the
earlier closed version remains in history. The dated assertion is customer
reported, not independently verified lender repayment/closure/current-debt truth.
Closing does not zero outstanding, EMI or sanction amounts, change contractual
end dates, rewrite original source facts or remove the source document. A Closed
card labels the unchanged amount Recorded balance, with its original balance date.

Financial Profile owns the operation. The date cannot precede loan start, the
recorded balance date or the latest reviewed status date, or exceed the current
global local-date window through UTC+14. UNREVIEWED cannot be submitted as a
reset; prior reviews stay immutable. Term corrections preserve the lifecycle
state/date of their expected prior version. A start date after the reviewed
status, or a balance dated after a retained Closed status, requires an appropriate
status review first. No loan amount is calculated or inferred by this workflow.

Status reviews append to the existing loan_record_revisions journal. No migration
or new table is needed: additive optional response payload fields retain legacy
defaults, while existing create/correction request DTOs and their operation
fingerprints remain unchanged. Tenant/customer/actor-scoped operation identity,
atomic expected-version checks, immutable creation/history, advisory/transaction
locks, SELECT/INSERT-only grants and revoked UPDATE/DELETE remain in force.
Lifecycle and term corrections share the same version/operation boundary. Exact
replay returns its original status receipt even after reopening or later terms;
changed requests, operation reuse and stale competing writes cannot overwrite it.

The operator customer-to-tenant binding precedes owner policy checks. Current
Self financial read, source-document read and update authority with consistent
versions are required. Tenant/customer/actor and source/currency are server-owned.
Unknown/duplicate/missing JSON fields, invalid state/date/version and mismatched
sources fail closed. The Gateway resolves the current session/active owned
customer/setup, reads the exact prior version, binds the returned operation,
actor, status/date and unchanged complete terms/provenance, reads that exact
saved version, and reconfirms current authority/context before returning success.
Term-correction readback additionally verifies retained lifecycle metadata.

The UI requires explicit choice/date and Save; no automatic status/date is filled
in. Uncertain requests retain/freeze the exact operation/body/expected version,
including after drawer close with no current extracted figures. Overlapping
term/status writes are blocked while confirmation is outstanding. Confirmed400
allows correction; explicit stale-version409 requires refresh/review; uncertain
failures retain the request. Read-only users can inspect status/history without
status actions. API/session changes hide prior forms and values immediately.
History and record details show customer-reported status/date for each version.

## Verification

850 backend checks pass:573 VS02 including real OCR,146 Financial Profile owner,
40 reprocessing,64 Reporting and27 Search.34 new owner cases cover status/date
validation, explicit close/reopen, unchanged source/amounts, legacy UNREVIEWED
defaults, closed-term correction, lifecycle/term concurrency, operation namespaces,
authority/malformed input, foreign-tenant collisions and lost confirmation replay
after reopen. Native owner/Gateway HTTP uses explicit current session/customer/PDP
doubles and in-memory owner storage.

147 UI checks pass:111 Documents,14 Financial information and22 Loan records.
The six new UI cases cover explicit dated review/no balance clearing, reopen
without current source figures, frozen retry after close/overlap prevention,
validation/stale versions, read-only history and changed API/session scope.
TypeScript, changed source/test ESLint and Vite pass. Gateway and physical
persistence test-project builds have zero warnings/errors. No package/lock,
service, development Actions or production deployment changes are introduced.

The physical PostgreSQL regression includes restart/readback of Closed state,
reopen, replay of old status/correction receipts, preserved history, scope and
runtime UPDATE denial. It is authored and compiled, not run. Live ClamAV,
physical PostgreSQL/durable processing, Compose and full authenticated integrated
browser UAT remain NOT RUN. Owner history is not central Audit Service closure.
Full lender balances, multi-source reconciliation, broader lifecycle and complete
financial truth remain open. Original Loans stays Partial.

After paired merge:68 numbered units =67 implementations +1 governance closure.
Original29 deliverables remain0 complete,17 partial,12 pending. Original151 IDs,
81 feature IDs,126 Phase1 memberships, source phases and business acceptance are
preserved. The paired Expert receipt locks final merged source/graph and checks;
the same workbook identity is updated. The repository status JSON is reconciled
through T1-68, including the T1-67 correction/history progress.
