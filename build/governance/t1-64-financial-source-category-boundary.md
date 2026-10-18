# T1-64 — Insurance/Tax source-category compatibility

Base Application `cf6f3219f1b3b09d0e049fe6b80c9d336be99bea` (PR113), Expert
`c013f2c7016a99f16f4d8632c99c59672f66b2f9` (PR65), Architecture
`90934b51b2b203795acf95da2252a2cf1a1d1f4c`. Branch
`work/t1-64-financial-source-category-boundary`.

This repairs an existing extraction/review/adoption/source-view journey under
Master sections10–15/41. It introduces no new document class or financial model.
Source review found that customer INSURANCE/TAX source categories were passed
unchanged to the Financial Profile owner, which requires canonical
INSURANCE_POLICY/TAX_RECORD. Two focused regression cases reproduced the actual
owner rejecting these source types before the fix. Canonical responses were
also omitted by source-category-filtered Search/Reports and not translated in
customer Passport/list/detail/history views. Earlier native insurance tests
manually translated the category, masking the actual Gateway boundary mismatch.

FinancialSourceFactBoundary generalizes the existing asset adapter. It maps
INSURANCE/TAX to canonical owner types before explicit selected adoption and maps
canonical owner responses back to existing customer source categories in
Passport/current/exact history/Search/basic Reports. Legacy category responses
remain supported. Existing exact PROPERTY/VEHICLE mappings and unrelated object
types remain unchanged. The old native insurance test's manual translation is
removed: it now uses the same boundary helper as the Gateway for all selected
facts. No owner contract, owner validation, schema, repository or calculation
rules are changed. No migration, new package/service or development Actions.

Original labels, values, currencies, dates, source fact/document/evidence IDs,
extraction/validation identity and provenance are preserved. No automatic
adoption, verified coverage/tax assessment/refund/current asset value, financial
totals or complete policy/tax/domain records are inferred. Existing customer,
session, tenant, Consent and selected-source ownership gates remain. Generic
ASSET text/CSV/XLSX figures still lack a canonical subtype and cannot be adopted.
No PROPERTY/VEHICLE identity is invented, and an invalid mixed batch writes no
profile/facts/events. This corrects earlier broad seven-category adoption wording:
seven categories extract, while untyped generic asset adoption remains open.

## Verification

The two focused owner regression cases fail before the fix and pass after it.
556 VS02 checks pass (9 new category cases). Six new cases exercise real text/CSV/
bounded XLSX extraction -> shared category mapping -> actual Financial Profile
HTTP contract routes/in-memory owner -> replay/current/exact revision/provenance
readback, with cross-customer fact denial. A mixed mapped Insurance/untyped ASSET
batch is rejected atomically without profile/fact/event writes. Native insurance
PNG/JPEG/PDF owner checks now use the real shared category boundary rather than
test-specific translation. Pinned native OCR uses explicit scanner/repository
doubles. HTTP contract routing uses trusted test contexts and in-memory storage;
it does not prove full Gateway ingress, physical PostgreSQL or authenticated
live integration.

40 reprocessing,39 Financial Profile owner,9 affected Reporting and10 affected
Search checks pass:654 backend total, confirmed through unique TRX receipts.
Reporting/Search exercise canonical INSURANCE_POLICY/TAX_RECORD alongside legacy
INSURANCE/TAX, crossed with ASSET/PROPERTY/VEHICLE sources, retaining provenance
and supported-category filtering.113 existing Documents/Passport UI regressions
pass. Gateway/owner build has zero warnings/errors. Frontend production source
is unchanged; TypeScript/lint/Vite evidence remains the T1-63 baseline, not a new
build receipt. Existing assets used without tracked package/lock changes; fresh
historical locked restore is not demonstrated.

Live ClamAV benign/EICAR, physical PostgreSQL/durable processing/classification,
PowerShell/Compose and full integrated browser UAT remain NOT RUN. Untyped asset
subtype/adoption, broader documents, complete financial models and original
source phases remain open/unchanged. No business/production acceptance or
source-phase/stage-gate advancement is inferred.

After paired merge:64 numbered units =63 implementations +1 governance closure
(T1-04). Original29 deliverables remain0 complete,17 partial and12 pending.
Extraction/Financial document search/Financial summary remain Partial. Original
151 IDs/81 features/126 Phase1-applicable memberships retained. Exact source and
paired knowledge compilation/synchronization are recorded in the Expert receipt.
