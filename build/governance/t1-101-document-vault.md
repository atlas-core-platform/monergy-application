# T1-101 — Document Vault (P1-06)

Date: 2026-10-21. Completes the configured Phase 1 Document Vault implementation. Business acceptance and physical/live verification remain separate.

## Customer journey

Documents now includes a vault with the ten required categories: Identity, Income Tax, Banking, Credit, Loans, Insurance, Investments, Property, Vehicles and Other. Owner-side pagination reaches beyond the former recent-100 view, with stable ordering, literal filename search and tenant/customer-bound cursors. The existing recent list is explicitly labelled as the most recent 100 versions.

An exact version shows its name, media type, declared document type, date, financial year, source, version, checksum, scan evidence, authenticity status and actual storage information. Customer-entered organization metadata is separate from immutable original evidence, automated classification and financial records. Missing metadata remains explicitly unrecorded. New Google Drive/OneDrive intakes preserve provider, selected folder ID, file ID and provider version with immutable evidence; legacy provenance is not invented. Current reference storage remains visibly temporary. Malware scan verification at intake never implies document authenticity verification.

Metadata writes require current Self read and upload permissions, optimistic revision checks and an actor-bound operation receipt. Original evidence remains append-only. The memory and PostgreSQL owners preserve metadata operation history and return the original receipt on exact retry. Different payloads, actors or stale revisions conflict.

Replacement requires explicit review and confirmation, a size/type-validated and malware-scanned file, the permanent document ID and expected current version. The owner transaction checks the latest version and SHA-256, rejects unchanged content or a stale view, appends a new version and emits the existing version event exactly once. It preserves earlier evidence and financial-source links. Uncertain writes retain the exact operation/payload for retry; changing workspace clears private UI state. Replacement opens its exact new version in the existing processing/review controls. Processing remains explicit and cannot silently overwrite trusted financial records. Existing authorized Search refresh reads the new owner version on its next query; no index success is fabricated. Version history no longer rejects a valid response solely for having over 100 entries; existing transport size limits remain.

## Ownership and verification

The Gateway derives the customer from current Customer & Identity ownership. Evidence independently rechecks current Self authority and known tenant bindings; lists, details, metadata and replacement remain customer/tenant scoped. Reads confirm authority before returning. Writes recheck before commit. Cross-origin and unknown-query input fail closed. Gateway audit capture covers vault reads and writes. Provider credentials and file bytes never appear in vault metadata responses.

649 nonphysical Evidence/processing tests and 132 Integration Gateway tests pass (781 total). Coverage includes paging beyond 100, foreign tenants/customers/cursors, access revocation, metadata replay/conflicts, original evidence preservation, unchanged/stale replacement, original scan and provider provenance, new-version event idempotency, native OCR and both cloud connector regressions. The existing OCR fixture assertion was aligned with the current owner boundary: a foreign-customer source is hidden as not-found; wrong-tenant requests remain denied.

20 focused UI tests pass: seven new vault tests plus thirteen existing upload/library/exact-history/source/processing-review checks. TypeScript, changed-source/test ESLint, Vite and Gateway build pass. The broader UI run was stopped after narrowing to affected paths; it is not claimed as a complete pass.

Migration `evidence/0004_document_vault.sql` adds immutable provider provenance, mutable version metadata and append-only operation receipts with scoped grants. A restart/parity physical test is prepared. Physical PostgreSQL migration/grant/restart execution, production storage retention/OD-016, real provider import, Docker/PowerShell and full authenticated live UAT were NOT RUN. Apply migrations before starting the changed physical adapter. No packages or development GitHub Actions are introduced.

## Tracker boundary

P1-06 Document Vault is Complete for the configured Phase 1 implementation. Broader classification/OCR breadth remains P1-07; production storage ownership/retention remains the existing MON-DOC-016 decision. External provider inputs continue to be operator onboarding configuration. No legal content, provider credential, KYC policy or acceptance is fabricated.

After paired merges and tracker update: 29 original deliverables = 10 Complete, 18 Partial, 1 Pending (19 incomplete). T1-101 = 100 implementation units plus one governance closure. P1-04 Basic KYC remains Pending for its approved route. `acceptedThrough: T1-06` and `approvedForExpertIngestion: false` remain unchanged.
