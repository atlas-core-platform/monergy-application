# T1-67 — Reviewed loan corrections and history

Application base `66853793ea64608fdaa5e982e637a887e426de6d` (PR117), Expert base
`dea1b7e46ebefbfd7f8abc9409a5b229c9f7f930` (PR69), Architecture
`90934b51b2b203795acf95da2252a2cf1a1d1f4c`. Branch
`work/t1-67-reviewed-loan-corrections`. Tracker version9 reviewed through T1-66.

This is the next bounded part of the planned Master section13 / MON-FIN-006 /
M2-WS04-E01-F03 Loans journey. Customers correct the terms they entered in T1-66
and inspect prior reviewed versions. Closing/lifecycle, source reconciliation,
central Audit Service integration and full lender/financial verification remain
open. Original Loans stays Partial; no source-phase, acceptance or gate advances.

## Behavior and ownership

Financial information > Loan information exposes Edit and Change history for
saved loan records. Edit preloads the saved terms, identifies its record version
and retains the exact original source revision/currency. A superseded source need
not be the current extracted figure to correct an existing record. No new source
or currency binding is accepted in corrections. Every saved version retains the
customer's full reviewed loan terms, owner source provenance, actor, operation
and timestamp. No extracted source figure or prior loan version is overwritten.

Financial Profile owns corrections and current/historical reads. Existing stored
creation payloads without a version deserialize as version1. A correction supplies
an expected record version and a new operation ID. A successful correction appends
version N+1 under a lock/transaction; another operation against stale N receives
LOAN_REVISION_CONFLICT without a write. Operation fingerprints include the expected
version and normalized terms, scoped by tenant/customer/actor. Repeating an exact
operation returns its original correction receipt even after later versions.
Changing the body/version/record, or reusing a creation operation for a correction
or vice versa, conflicts. Creation replay still returns immutable version1.

Migration0004 adds append-only loan_record_revisions with a tenant/customer/record
foreign key, unique record revision and scoped actor/operation uniqueness. Both
creation and correction serialize operation and record writes. Runtime retains
SELECT/INSERT only; UPDATE/DELETE are revoked on both history and creation tables.
Current lists read the latest version without counting a correction as a new
loan. History pages contain at most50 versions, with an explicit older-version
cursor; exact version reads remain available. Existing loan lists remain bounded
to200 most recently added records with an explicit hasMore state.

Operator customer-to-tenant binding precedes owner policy checks. Current Self
financial read, source-document read and update access with consistent authority
versions are required for corrections. History needs current Self read/document
access, including for read-only customers. Browser JSON cannot supply tenant,
customer, actor, currency, provenance or record identity. Unknown/duplicate fields,
missing versions and invalid terms fail before writes. The Gateway resolves the
current session/active owned customer/setup, checks receipt ID/version/actor/body
and unchanged source metadata, freshly reads that exact saved version, then
rechecks authority/context. Invalid input is reported as400; malformed owner
responses/unavailability remain503. Cross-tenant customer-ID collisions fail closed.

An unconfirmed UI correction freezes its body, expected version and operation for
exact retry, even after drawer close or when no current extracted loan figures
remain. Explicit stale-version rejection disables the old form and instructs the
customer to refresh/review the newer version. Confirmed400 permits form correction;
other uncertain failures retain the original request. Read-only users can inspect
history but cannot edit. Old session/API records and history disappear immediately
on scope change. History failures are explicit, never an empty financial position.

## Verification results

816 backend checks pass:573 VS02 (including real OCR),112 Financial Profile
owner,40 reprocessing,64 Reporting and27 Search. The27 new owner cases cover
corrections/concurrency/replay/source preservation, legacy payloads, history,
authority and malformed input.141 UI checks pass:111 Documents,14 Financial
information and16 Loan records. TypeScript, changed source/test ESLint and Vite
pass; Gateway and physical persistence test-project builds have zero warnings
and errors. Locked restore uses the existing pinned package/lock definitions;
tracked dependency and lock files remain unchanged. Physical-only live ClamD
requires an unavailable fixture and is excluded from the573 native VS02 checks.

## Verification boundary

Focused owner and actual owner/Gateway HTTP checks exercise correction/readback,
version conflicts, immutable source/creation receipts, operation replay after later
changes, concurrent edits, legacy stored payloads, cursor history, malformed input,
current authority denial/unavailability, lost confirmation and foreign-tenant
collisions. Native owner/Gateway code runs against explicit current session,
customer and PDP HTTP doubles with in-memory owner storage. Browser component
checks cover explicit correction, immutable historical source, stale versions,
uncertain retry after close, validation correction, read-only history/paging and
scope changes. Broad source extraction/normalization/Search/Reports regressions
and TypeScript/ESLint/Vite verification accompany the final merge receipt.

A physical PostgreSQL restart/readback/replay/stale-version/source-history/runtime
UPDATE-denial regression is authored and compiled, not executed here. Live ClamAV,
physical PostgreSQL/durable processing, Compose and full authenticated integrated
browser UAT remain NOT RUN. This owner history is not centralized Audit Service
delivery/search closure. No new dependency/service/development Actions or
production deployment is introduced. No lender verification, aggregate debt or
net worth is inferred from customer-reviewed terms.

After paired merge:67 numbered units =66 implementation units +1 governance
closure. Original29 deliverables remain0 complete,17 partial,12 pending; original
151 requirement IDs,81 feature IDs,126 Phase1 memberships and source phases remain.
The paired Expert receipt locks the final merged source/graph and verification;
the same tracker identity is reconciled after both merges.
