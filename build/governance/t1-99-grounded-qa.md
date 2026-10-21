# T1-99 — Document-grounded Q&A and deterministic net worth

Date: 2026-10-21. P1-24 remains Partial. This increment extends T1-98; it does not close OD-11 or advance business acceptance.

## Customer journey

Financial Q&A now offers financial source records, an explicitly selected document version, or an explicitly selected reviewed net-worth position. Selection changes require renewed per-question permission. Document answers quote bounded passages with version/hash/location references. Net-worth questions execute the existing Financial Rules owner using the selected reviewed inventory and retain the exact result/rule reference. History links reopen the document version or saved calculation without calculating again. Financial Q&A is included in protected-route navigation; direct net-worth links reach the SPA.

## Owner and data boundaries

Document Intelligence exposes GET /local/v1/customers/{customerId}/documents/{versionId}/qa-passages behind current Self Evidence/AI capability checks before and after reading. Existing Evidence content verification and tenant workload boundaries remain authoritative. Supported inputs are bounded text/CSV, the existing spreadsheet template, upright PNG/JPEG and single-page unlocked PDFs through the existing pinned OCR/PDF components. Oversized, unsupported or uncertain content returns insufficient information, without silent truncation or adopting OCR as financial truth. Limit: 20 passages, 20,000 text characters; uncertain OCR lines reject the whole answer source.

AI Intelligence calls the owning services, validates identities, re-reads the exact reviewed inventory and Financial Rules result, and checks calculation capability. The model cannot choose customer or document/position IDs, execute arbitrary tools, calculate, or provide new financial figures. Model output selects canonical passages/results only; original validation rejects additional prose, unknown IDs and contradictory selections. Provider access remains through Integration Gateway. Existing encryption, rate limits, idempotent attempts and current-source authorization on history remain. Prompt version v2 changes the configuration revision and requires fresh permission.

Retained records include selected document/position IDs, retrieved source claims, content hashes/locations or exact calculation IDs/rule versions, model/prompt/configuration and final response. Retrieval is checkpointed before answer selection. Existing stored source-only answers remain readable through optional fields. Financial Rules is the sole arithmetic authority; the shared response validator only checks wire shape and provenance.

## Verification

132 Gateway/Q&A tests, 49 nonphysical Document Reprocessing tests and 72 Financial Rules tests pass (253 backend). 52 focused UI tests pass, including source selection/renewed permission, exact calculation links and access gating. TypeScript, changed-scope ESLint and Vite build pass. AI Intelligence, Document Intelligence and UAT Gateway build with zero warnings/errors. Owner-boundary tests reject foreign customer results, denied calculation permission and a changed reviewed inventory; retained replay does not repeat model spend.

No live model provider/quality evaluation, native OCR execution in the added mocked reader tests, physical PostgreSQL, Docker, PowerShell or full authenticated UAT was run. No development GitHub Actions were triggered. No new package, migration, service or automatic permission grant.

## Configuration and remaining scope

Existing optional operator configuration supplies provider endpoint, model, API key and published notice. Local composition adds existing Document Intelligence and Financial Rules URLs to FinancialQa settings. No fabricated approved/legal text or mandatory external onboarding dependency.

P1-24 stays Partial: evaluated model relevance/grounding, OD-11 approval and broader complete financial summaries remain open. Current document answers are quotations/transcriptions and net-worth results cover one explicitly reviewed inventory; they are not universal document interpretation, verified balances, recommendations or forecasts. Production retention/key custody and live authenticated verification remain separate. P1-04 is the only Pending journey and requires its approved KYC route (OD-04/05/06).

Original 29 deliverables: 8 Complete, 20 Partial, 1 Pending; 21 incomplete. Through T1-99: 98 implementation units plus 1 governance closure. acceptedThrough T1-06 and approvedForExpertIngestion false are unchanged.
