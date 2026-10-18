# T1-58 — Insurance policy OCR and explicit review

Base Application `e2fbf5793746057d9e72f132fdf5405913da1b2d` (PR107), Expert
`919c03c160cd9d35185f4ae4eb7dc3a50ea735fd` (PR59), Architecture
`90934b51b2b203795acf95da2252a2cf1a1d1f4c`. Branch
`work/t1-58-insurance-policy-ocr`.

## Planned scope and source meaning

Master §§11–13/41 explicitly includes Insurance Policy classification, scanned
extraction and sum_assured/premium fields. This bounded document unit advances
MON-DOC-004/005/006/007 and the existing partial insurance source-fact journey.
It does not add an original requirement or complete the full insurance model.

Existing PNG, upright JPEG and readable unlocked single-page scanned PDF adapters
now recognize exactly one INSURANCE POLICY or LIFE INSURANCE POLICY title within
the first ten OCR lines. Any second known salary/loan/insurance title anywhere
rejects the page as unclassified. Only printed English and existing format/native
preflight limits are supported; filename alone does not classify a document.

A valid page has exactly one Policy Date, Sum Assured and Premium (or Premium
Amount) label, with a supported explicit currency on each amount or one matching
Currency label. Both amounts must share currency. Existing eight-currency,
nonnegative amount, grouping/precision, exact ISO or unambiguous English date,
bounded TSV and minimum90-percent word confidence checks apply. Maturity Date,
Annual Premium, quotations and other ambiguous labels are not substituted.
All required fields are validated before either candidate becomes visible.

The two INSURANCE candidates retain distinct labels: Sum assured stated on policy
and Premium stated on policy (frequency not established). Both carry Policy Date,
original Evidence/customer/document version, confidence and distinct line
provenance. Identities bind the original version, processing run and field;
extraction version is insurance-policy-{png|jpeg|pdf}-ocr/1.0.0. No arithmetic,
annualization, current-cover, cash/asset-value or authenticity assertion occurs.
The existing financial boundary maps the source category to INSURANCE_POLICY;
only explicitly selected and confirmed figures reach Financial Profile.

Incomplete insurance has safe nonretryable processing.insurance-policy.incomplete
with zero committed facts and one terminal failure event. Common unclassified,
low-confidence, native unavailable/format/deadline/cancellation behavior remains.
The shared salary/loan money validator is extracted without changing their fact
identities or semantics. Original-byte malware gating, SHA256, customer/tenant
checks, native private-file lifecycle and no external OCR provider are retained.

Current and recovered Documents views recognize insurance and explain dated
literal figures. The current view supports independent selection; the recovered
read-only view cannot adopt. Neither upload nor extraction automatically changes
financial truth. Guidance and typed failure text describe exact required fields.

## Verification

274 VS02 checks pass, including46 new insurance cases and the 36
NativeOcr-tagged checks;40 reprocessing checks pass. New cases cover both native
figures across PNG/JPEG/image-only PDF, real native incomplete/mixed rejection,
label aliases, conflicting/missing/duplicate/invalid fields, low confidence on
each required line, late/multiple/unknown titles and typed zero-fact failures.
Actual Evidence HTTP transports each original format with exact binary/hash and
tenant/customer denial checks to pinned OCR. Explicit Financial Profile adoption
and original provenance are verified after the existing category mapping.
Scanner/repository dependencies are explicit doubles, not live ClamAV/PostgreSQL.

68 Documents UI checks pass, including7 new cases:three format-specific independent
premium selections with no automatic adoption,three recovered insurance/read-only
views and one incomplete-policy message. TypeScript, changed source/test lint,
Vite and zero-warning Document Intelligence/Gateway builds pass. The synthetic
PDF was rendered and visually inspected. Native Tesseract5.3.4, hash-pinned English
data and Poppler24.02.0 are the existing approved local adapters. No new package,
service, migration or development GitHub Actions. SDK10.0.401 matches the pin;
available Node24.19.0 does not prove pinned24.21.

Live Docker/ClamAV benign/EICAR intake, physical PostgreSQL/durable OCR state,
PowerShell/Compose and full browser-to-native integrated UAT remain NOT RUN.
Broader document classes/layouts/languages, multipage policies, policy provider/
type/maturity/verification and complete account records remain open. No business
or production acceptance is inferred.

After paired merge,58 numbered units comprise57 implementation units and1
governance closure (T1-04). Original29 deliverables remain0 complete,17 partial
and12 pending journeys. Classification/OCR/Extraction remain Partial. Original151
IDs/81 features and126 Phase1-applicable membership are unchanged. Exact merged
Application source/tree and compiled knowledge are in the paired Expert receipt.
