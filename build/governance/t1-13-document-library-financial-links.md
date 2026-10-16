# T1-13 — Document Library Metadata & Financial Links

Status: bounded implementation candidate under AR-003 / COV-012.

## Purpose

Advance the already-accepted **My Documents** journey without introducing document classification, future financial domains, or a new owner. T1-13 improves how customers organize the existing Evidence library and connects an exact document version back to the currently supported trusted Financial information that was derived from it.

## Existing authorities reused

- Evidence remains authoritative for document identity, version, source, timestamps, content metadata and integrity hash.
- Financial Profile remains authoritative for trusted financial facts and their exact source document version.
- The customer product shell remains authoritative for current session/customer resolution and capability exposure.

No new capability, permission, contract, database, queue, event or service is introduced.

## Document library behavior

The customer may:

- filter by the existing file types already supported by Evidence;
- sort the current authorized library by newest uploaded, oldest uploaded, or file name A–Z;
- inspect Evidence-owned metadata including file type, file size, version, upload time, received time and source;
- see a linked-trusted-item count only after Financial Profile has successfully loaded.

Sorting is a client presentation operation over the current authorized library. It must not mutate Evidence order/state.

## Related trusted Financial information

For a selected document version, the product may show trusted Financial Profile facts only when all of the following are true:

1. the current Customer has `financial-profile.profile.read`;
2. Financial Profile loaded successfully;
3. the fact type is currently accepted by this product increment: `INCOME` or `EXPENSE`;
4. `fact.source.documentVersion` exactly equals the selected Evidence `documentVersionId`.

Each relation links to the existing exact Financial fact/source-history route:

`/finances?fact=<financialFactId>`

A document ID, file name, evidence ID, source-fact ID or approximate match must never be used to infer the relation.

## Failure and access semantics

- No Financial Profile read capability → say linked financial information is outside current access.
- Financial Profile owner failure → show unavailable; never show zero links.
- Financial Profile loaded successfully with no matching supported facts → it is valid to show no currently linked trusted Income/Expense information.
- Future Financial Profile fact types must remain invisible here until their own domain increment is accepted.
- Evidence access remains independently enforced by the existing product boundary.

## Explicit exclusions

T1-13 does not implement:

- document categories or automatic classification;
- tags, folders, retention/deletion policy, archival or replacement;
- OCR/AI classification or production extraction;
- bank, loan, credit, investment, insurance, property, vehicle, tax or goal document semantics;
- cross-document aggregation or duplicate detection;
- financial verification/certification;
- new search infrastructure (T1-11 remains the authorized search journey).

## Product language

Use **Documents**, **Document details**, **Source**, **Received**, and **Related financial information**. Technical IDs and SHA-256 remain secondary under Technical details.

## Acceptance evidence

Focused component evidence must prove:

- deterministic sort order;
- exact document-version-to-financial-fact linking;
- linked count is shown only after Financial Profile is known;
- successful zero-link state;
- unavailable Financial Profile does not become zero links;
- missing Financial Profile permission does not expose linked data;
- future/unaccepted Financial fact types remain outside the relation view.

No additional manually-triggered hosted CI cycle is required solely for this bounded candidate. Execution evidence and product acceptance remain separate CTO decisions.
