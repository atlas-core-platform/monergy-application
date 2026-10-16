# T1-15 — Customer Document Version History

Status: bounded implementation candidate under AR-003 / COV-012.

## Purpose

Complete the accepted Evidence version-history portion of **My Documents** without introducing document replacement, retention, classification, or a new owner.

T1-15 lets a Customer open a Document and inspect the exact authorized versions owned by Evidence. Historical versions can then be opened through the existing exact source-document detail path.

## Authorities reused

- Evidence remains authoritative for document identity, version identity, version number, source, file name, content type, integrity hash and received time.
- Access Management continues to authorize `evidence.document.read`.
- Customer & Identity continues to resolve the authenticated Customer relationship.
- The product gateway remains responsible for server-side customer resolution and bounded response validation.

No new capability, permission, service, database, queue, event or Evidence contract is introduced.

## Product endpoint

The product gateway exposes:

`GET /product-api/v1/evidence/{documentId}/versions`

The browser supplies only the document identifier selected from its already-authorized library. The gateway must:

1. validate the document identifier;
2. resolve the current session and owned Customer on the server;
3. freshly evaluate `evidence.document.read`;
4. call Evidence CID-021 `GetEvidenceMetadata` with the resolved Customer;
5. reject cross-customer/not-found metadata without disclosure;
6. validate exact document/customer identity;
7. reject duplicate version IDs or duplicate version numbers;
8. validate each version's identifiers, source/file metadata, supported content type, SHA-256 and received time;
9. return versions ordered by version number descending, then received time descending, then version ID.

The product response includes only Evidence metadata already present in CID-021 plus the bounded temporary-data label. Evidence content references are never exposed.

## Document-details presentation

Version history loads **lazily** only after the Customer opens Document details. The library does not issue a metadata request for every row.

Each version may show:

- version number;
- file name;
- file type;
- received time;
- source.

The version corresponding to the currently open library record is marked **Open / Current view**.

Selecting another version must use the existing:

`GET /product-api/v1/evidence/source/{documentVersionId}`

path, which rechecks current Evidence access and opens exact source-document details.

## Failure semantics

- Owner unavailable or malformed response → history is unavailable; never infer “one version.”
- Cross-customer or inaccessible document → 404/non-disclosure.
- Missing Evidence read capability → Document library/history is unavailable under the existing access model.
- A successful history response must contain at least one version.
- The product does not infer whether a historical version is obsolete, superseded, correct, verified or financially authoritative.

## Explicit exclusions

T1-15 does not implement:

- upload-as-new-version / replace-document UX;
- delete, restore, archive or retention controls;
- current/preferred-version mutation;
- diffing document contents;
- document categories, tags or classification;
- OCR/AI comparison;
- legal record/version certification;
- new financial-domain semantics.

Any future version mutation workflow requires explicit Evidence write contracts, idempotency/conflict rules, retention/legal semantics and product acceptance.

## Acceptance evidence

Focused evidence must prove:

- history loads only after Document details opens;
- multi-version history renders in owner-defined newest-version order;
- current version is distinguished from historical versions;
- exact historical version opens through the source-document endpoint;
- malformed/mismatched history fails closed;
- owner-unavailable is not rendered as a one-version history;
- LOCAL/UAT enforces unauthenticated/admin/cross-customer denial and exact version metadata.

No additional manually-triggered hosted CI cycle is required solely for this bounded candidate. Execution evidence and product acceptance remain separate CTO decisions.
