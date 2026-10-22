# T1-111 — Independent document classification and unknown review

Date: 2026-10-22. P1-07 remains Partial for broader layout/language quality and production capability approval. This unit delivers all required document-family labels and the missing administration workflow independently of financial extraction.

## Delivered behavior

Document Intelligence classifies supported headings across ITR, Form 16, banking, salary, credit report, loan, insurance, investment, property, vehicle, GST, PAN and Aadhaar families. Multiple different titles, uncertain OCR, late/absent headings, unsupported layouts and empty text remain Unclassified with a bounded reason. No probabilistic confidence or financial verification is invented. Classification is stored before financial extraction and survives extraction failure. Parsed OCR is reused transiently; source text and identity numbers are not stored in metadata.

Document Monitoring links to a paged unknown/all classification review screen. Exact tenant/customer/version/hash identity binds each record. Current tenant administration is required for every read; mutation additionally requires explicit operator-configured LOCAL/UAT reviewer assignment. Review stores actor, timestamp, reason code and revision atomically. Concurrent stale changes reject; automatic reprocessing cannot erase human history or bind different original bytes. Review does not bypass scan, source access, Consent, extraction or financial adoption. Product Gateway audit captures the request/result without retaining review bodies.

## Persistence and onboarding

Optional distinct PostgreSQL databases per tenant retain classification/review state. Migration and least-privilege grants are operator-applied. Runtime readiness checks configured schemas; failures do not fall back to volatile state. Unconfigured reference adapters remain in memory and have no mutation grants by default. `-ClassificationConfigurationFile` retains private connection and reviewer settings across profile starts. See [setup](../uat/Classification-Review.md).

## Verification

680 backend checks pass:627 VS02 regression checks,49 reprocessing checks, and4 additional malformed/oversized review cases. The focused classification total is37 (included in the680, not additional). 36 focused UI checks pass. TypeScript, changed ESLint, Vite build, Gateway build/startup and PowerShell legacy/onboarding/retained configuration checks pass. Physical restart/grant test is supplied but not run.

Live PostgreSQL migrations/grants/restart, native OCR corpus quality and full browser UAT remain unverified. No production capability catalogue/role-pack expansion, arbitrary layout guarantee, financial verification or downstream processing durability is claimed. No development GitHub Actions.

## Tracker

After paired merges:111 numbered units =110 implementations+1 governance closure;17 Complete,12 Partial,0 Pending. Preserve original151 IDs,81 features,126 Phase1 requirements, acceptance through T1-06 and approvedForExpertIngestion=false.
