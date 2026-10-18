# T1-65 — Reviewed generic asset source types

Base Application `7be146a63e049a87685e1227d1ccd3e71f3e9ca1` (PR114), Expert
`8e6284285af6d9cd22867943ca4a3cb2574eb250` (PR66), Architecture
`90934b51b2b203795acf95da2252a2cf1a1d1f4c`. Branch
`work/t1-65-reviewed-asset-source-types`.

This completes a bounded gap in the existing extraction/review/adoption journey
under Master sections11–15/41. Generic ASSET figures extracted from structured
text, financial CSV or the bounded Excel template previously lacked a canonical
owner subtype. Customers now explicitly choose Property or Vehicle for each
selected generic asset figure before adding it to Financial information. Current
and recovered completed-result review both require the choice. No default type
is supplied, and choosing a type alone makes no financial write.

The Gateway accepts a bounded assetTypes selection array, checks it against
selected customer/document-owned source facts, and uses the shared
FinancialSourceFactBoundary.ReviewedTypes policy. Only exact fixture-extractor,
financial-csv and financial-xlsx 1.0.0 ASSET sources accept this choice. Missing,
duplicate, unselected, unsupported or non-asset choices fail closed. Established
property/registration/vehicle OCR types cannot be overridden. Other categories
retain their existing mapping. Only the canonical fact type changes; original
labels, values, currencies, dates, source/evidence/document identities and
extraction/validation versions reach the owner unchanged.

Financial Profile stores canonical PROPERTY/VEHICLE and returns the reviewed
assetType alongside the customer ASSET source category in current/list/detail/
exact revision history. The customer list and source history display that type.
Existing Search/basic Reports continue consuming the canonical types through
existing source-category mappings. These remain source figures: a review choice
does not verify ownership, purchase, authenticity or current value and does not
create a complete property/vehicle record or financial total.

The owner rejects changing a previously used source fact's canonical type even
under a new operation ID, including historical sources superseded by later
revisions. Validation rejects two types for one source in a batch. In-memory
storage checks all history under its existing lock before writing; PostgreSQL
checks historical revisions under its existing transaction/profile advisory lock.
This preserves batch atomicity and serializes competing types. No schema,
migration, package, service or development Actions are added. Existing session,
tenant, exact customer authority, Consent, selected-source checks and replay
fingerprints remain in force.

## Verification

573 VS02,42 Financial Profile owner,40 reprocessing,64 Reporting and27 Search
checks pass:746 backend total, confirmed with unique TRX receipts. Seventeen new
VS02 cases cover explicit Property/Vehicle choices across actual text/CSV/XLSX
extraction, real Financial Profile HTTP contract routes, replay/new-operation
conflicts/current/exact-history/provenance and cross-customer fact denial, plus
missing/invalid/duplicate/unselected/non-asset/unknown-version choices and
forbidden OCR overrides. Three new owner checks cover competing type choices,
historical-source conflict with atomic mixed-batch rejection and invalid
same-source/two-type batches. Pinned native OCR regressions pass unchanged.

125 Documents/Financial information UI checks pass, including twelve new cases
for generic text/CSV/XLSX explicit classification, recovered review, current
readback and exact source history. A pre-existing deep-link test now waits for
asynchronous detail content; detail loading is deferred and abortable using the
existing loading pattern. TypeScript, changed source/test ESLint and Vite build
pass. Gateway/owner build has zero warnings/errors. Existing assets are used;
tracked packages/locks are unchanged. The new PostgreSQL history-conflict
regression is authored but physical PostgreSQL is NOT RUN; persistence test
project restore/build could not be completed in this environment. Production
PostgreSQL adapter source compiles in the owner/Gateway builds.

Owner HTTP routing uses trusted test contexts and in-memory storage; native OCR
uses scanner/repository doubles. Full authenticated Gateway ingress, live ClamAV,
physical PostgreSQL/durable processing/classification, PowerShell/Compose and
full integrated browser UAT remain NOT RUN. Broader asset types and documents,
complete financial models and original source phases remain open/unchanged.
No business/production acceptance or source-phase/stage-gate advancement.

After paired merge:65 numbered units =64 implementations +1 governance closure
(T1-04). Original29 deliverables remain0 complete,17 partial and12 pending.
Original151 IDs/81 features/126 Phase1-applicable memberships are retained. The
same tracker identity is updated from version7 after paired merge; exact source
and knowledge compilation are recorded in the Expert receipt.
