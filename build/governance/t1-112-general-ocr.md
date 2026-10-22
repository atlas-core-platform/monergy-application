# T1-112 — General scanned-document OCR

Date: 2026-10-22. P1-08 implementation is Complete against MON-DOC-006 (scanned documents become machine-readable information) and MON-DOC-013 (long-running OCR runs asynchronously). This is engineering completion, separate from native corpus quality, physical integration and business acceptance.

## Delivered behavior

Document Intelligence transcribes PNG/JPEG and up to20 unlocked PDF pages independently of document class or supported financial templates. It retains page/line order, engine version, timestamp and the exact tenant/customer/document-version/source hash. Unknown document types can have useful OCR output even when financial extraction fails. Uncertain lines and empty pages are explicit; a failed engine never publishes partial pages as a complete result. PDF passwords, forms, JavaScript and excessive pages are rejected. Existing one-MiB original, raster, parser, output and time limits remain enforced. English OCR is supported; arbitrary languages, handwriting and perfect accuracy are not claimed.

The customer vault exposes Read scanned text and a refreshable multi-page view. Reading never starts synchronous OCR. Current Self document-read and processing permissions, source availability and exact original hash are checked before returning text, with authority rechecked afterward. Foreign tenant/customer/version, revoked access, bad shapes and changed hashes fail closed. Administrator classification metadata does not grant content access. Gateway routes permit exact GET only, set no-store and participate in product request/result audit without recording text bodies.

Job Management now retains its trusted workload tenant when executing Document Intelligence processing; this fixes skipped classification on the asynchronous path. Its owner call budget accommodates the bounded OCR operation within the existing job lease. Financial extraction retains its stricter single-page layouts and never adopts OCR text as verified financial facts.

## Persistence and onboarding

Optional distinct tenant PostgreSQL Document Intelligence databases store the latest source-bound OCR result. Existing ClassificationConfigurationFile supplies private runtime connections. Apply both owner migrations and runtime SELECT/INSERT/UPDATE grants before starting retained profiles. Readiness checks both schemas and never falls back to memory on configured database failure. Unconfigured reference profiles are volatile. See [setup](../uat/Classification-Review.md).

## Verification

701 backend checks pass:652 VS02 and49 reprocessing regression checks. They cover all-page transcription, uncertain/empty text, page limits, engine failure without partial publication, exact source/revocation/tenant access, changed hashes, stored-state immutability and the asynchronous workload path. Native-process protocol fixtures prove multi-page rendering requests and preserve single-page financial rejection; they are not native corpus accuracy evidence. 50 focused UI checks pass. TypeScript, changed ESLint, Vite, Gateway build and actual routing/startup probes pass.

Physical OCR persistence/restart/hash/grant checks are supplied but not run. Live PostgreSQL migration/grants/restart, native multi-page corpus quality and full authenticated browser UAT remain unverified. No production capability expansion, financial verification, durable downstream processing/indexing or acceptance advance is claimed. No development GitHub Actions.

## Tracker

After paired merges:112 numbered units =111 implementations+1 governance closure;18 Complete,11 Partial,0 Pending. Preserve original151 IDs,81 features,126 Phase1 requirements, acceptance through T1-06 and approvedForExpertIngestion=false.
