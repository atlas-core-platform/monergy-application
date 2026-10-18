# T1-66 — Customer-reviewed loan records

Base Application `bdb420bfa5587b69fb018702e62c0da2533e7757` (PR115), Expert
`db1437492b750ccf815f75528a27814879ad57de` (PR67), Architecture
`90934b51b2b203795acf95da2252a2cf1a1d1f4c`. Branch
`work/t1-66-reviewed-loan-records`. Tracker version8 reviewed before work.

Master section13 explicitly defines loan_id, customer_id, lender, loan_type,
sanction_amount, outstanding, interest_rate, emi, tenure, start_date and end_date.
MON-FIN-006 / M2-WS04-E01-F03 and the current execution queue require full loan
account records beyond individual source figures. This unit adds customer create/
read of those terms, with a meaningful loan name, explicit balance-as-of date,
source currency and exact previously reviewed LOAN fact/revision reference.
Generic lender/type fields are provider neutral. Loan types are Home, Personal,
Vehicle, Education, Business and Other; rates are annual percentages0–100 with
up to4 decimal places, tenure1–1200 months, nonnegative amounts with up to2 decimal
places, positive sanction amount and valid ordered dates. The balance date accepts current local financial dates through the UTC+14 calendar window; later future dates are rejected. No term is extracted,
auto-filled, calculated or inferred from an arbitrary source amount.

Financial Profile owns the new DTOs, application and reference/PostgreSQL
repositories. Creation validates current source ownership/category/exact revision
and original provenance; a superseded immutable source revision remains an exact
reference. Existing facts and their amounts/provenance are not rewritten. The
source currency is owner-derived. New local owner resource routes require
current Self access to Financial Profile and source documents; creation also
requires update access with consistent authority versions. Tenant/customer/actor
come from current server authority, never browser fields. Unknown authority
fields, missing/duplicate/invalid JSON and invalid terms fail before storage.

The product ingress revalidates the session, exact active owned customer/setup,
read/evidence/update authority and context after owner creation and fresh owner
readback. It checks the returned operation, actor, customer and all reviewed terms.
The new form is under Financial information > Loan information. It requires an
explicit source selection and every term, shows source currency/revision, and
requires explicit Save. A source revision change requires a fresh selection.
Lists/details show reviewed terms and source-history links, with no verified
lender balance/ownership/current debt or loan/portfolio total claim.

The operation key is bound to tenant/customer/actor. Same request replay returns
the original immutable creation receipt; changed payload conflicts. In-memory
creation is locked; PostgreSQL serializes the record under a transaction/advisory
lock and checks the source in INSERT SELECT. Migration0003 adds an append-only
loan_records table with source revision foreign key, scoped unique operation key
and customer ordering index. Runtime receives SELECT/INSERT; UPDATE/DELETE are
revoked. The stored row includes actor, operation, source and timestamp as an
owner creation journal. Central Audit Service delivery/search for these new
records is still open; this journal is not presented as centralized audit closure.
Lists are bounded to200 with an explicit hasMore state. No shared-table topology,
production deployment, new package/service or development Actions are introduced.

The client retains an uncertain submission's exact body/operation for retry,
even when the drawer closes. It freezes terms pending confirmation; confirmed
400 validation rejection allows edits. Prior API/session scope data is hidden
immediately. Missing source/read/update/document access and unavailable data have
explicit states. Loan records stay separate from extracted source figures,
Search/basic Reports and dashboard coverage; no aggregate debt is invented.

## Verification

788 backend checks pass:573 VS02,84 Financial Profile owner,40 reprocessing,
64 Reporting,27 Search, confirmed with unique TRX receipts.42 new owner cases
cover invalid terms, exact historical source/provenance without amount changes,
foreign/wrong-category/missing sources, replay/payload conflicts, competing
creation and actual owner+Gateway HTTP routes. Current session/customer/PDP
services are explicit HTTP doubles; Financial Profile routing/repositories and
Gateway/client code execute natively. Authority denial/unavailability, missing
headers, mismatched tenant, readonly/delegated access, unknown/duplicate JSON and
lost confirmation after commit are covered. Reauthorization then replays exactly
one original record. Native OCR/extraction/reprocessing/source-projection
regressions remain green.

135 UI checks pass:111 Documents,14 Financial information and10 new Loan records
checks. They cover explicit Save, no amount inference, source revision changes,
readonly/no-source/unavailable states, exact retry after uncertain save/closing,
validation correction, context change and owner readback/details. TypeScript,
changed source/test ESLint and Vite pass. Gateway and physical test-project builds
have zero warnings/errors. Locked offline restore succeeds using existing cache;
tracked package/lock files are unchanged. A pre-existing physical history test was corrected to pass the explicit current-revision argument, enabling compilation.

A PostgreSQL regression for restart/readback/replay/conflict/scoping/runtime
UPDATE denial is authored and compiles but physical PostgreSQL is NOT RUN.
Live ClamAV, physical PostgreSQL/durable processing/classification, PowerShell/
Compose and full authenticated integrated browser UAT remain NOT RUN. Native
HTTP uses current authority doubles/in-memory data, not live provider/identity/
SMTP/database integration. No business/production acceptance or source-phase/
stage-gate advancement is inferred.

After paired merge:66 numbered units =65 implementations +1 governance closure.
Original29 deliverables remain0 complete,17 partial,12 pending; Loans remains
Partial. Editing/closing/lifecycle, multi-source reconciliation, broader terms,
central audit integration, full asset/liability records and complete financial
truth/calculation journeys remain open. Original151 IDs/81 features/126 Phase1
memberships/source phases are retained. Paired Expert receipt records exact
merged source, source graph and validation. The same tracker identity is updated.
