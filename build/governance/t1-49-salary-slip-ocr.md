# T1-49 — Planned salary-slip PNG classification/OCR candidates

Base application main: `7066f916f0bd682823c8bd3be0940a65daa6750f` (T1-48, PR98). Architecture pin: `90934b51b2b203795acf95da2252a2cf1a1d1f4c`. Expert base: `ec0a2c212972fede06d9bfcacfc0f6e9af337f95` (PR50). Candidate branch: `work/t1-49-salary-slip-ocr`.

## Requirement and delivered behavior

Master sections11/12 explicitly list Salary Slip classification and OCR of scanned documents. AR-003 COV-013/014 map MON-DOC-004/005/006 to classification/OCR/validation/review. This bounded first scanned-document slice stays within that plan. It does not mark those entire requirements complete or change the original151 requirements /81 canonical features denominator.

A customer can extract details from a supported printed English salary slip in PNG format, see recognized Salary Slip and a single net-pay INCOME candidate, review amount/currency/pay date and provenance, then explicitly select and adopt it through the existing Financial Profile owner path. The earlier upload scanner gate remains in place. Both current and recovered result transports preserve extraction version; recovered review can identify the OCR source. Current session/customer/AM capabilities, tenant and workload-only boundaries remain unchanged. No new business role/capability, Consent, database/schema, provider approval or cross-service SQL.

Evidence supplies the exact original binary source from the existing content store using a bounded base64 field over its dedicated-token processing-source endpoint. Document Intelligence verifies bytes/hash, document version and customer before using them. OCR uses a local pinned Tesseract5.3.4 process plus hash-pinned English data; no remote OCR provider or shell. PNG dimensions/size, admission/deadline, output limits, private temporary files and process cancellation/cleanup bound processing.

Recognition requires a clear supported title, labelled net pay/currency/date and ≥90 word confidence for the selected lines. Duplicate/missing/ambiguous fields, unsupported currencies/date/grouping, malformed images/output and low confidence yield zero candidates. Typed failure outcomes distinguish unclassified, incomplete, low confidence and temporarily unavailable OCR. No guessed currency/date, arithmetic, gross-pay double counting, automatic adoption or authenticity claim. Full support formats and operator paths are in `build/uat/Salary-Slip-OCR.md`.

## Verification evidence

- **105 VS-02 backend cases passed**, including **26 new OCR cases** plus79 existing processing/Evidence/security/CSV/contract cases. The one compiled physical ClamAV case is explicitly excluded and NOT RUN.
- Actual native pinned Tesseract reads two differing synthetic salary-slip layouts (including a month-name date and grouped amount), rejects an invoice as unclassified, and processes original PNG bytes through actual Evidence HTTP and owner processing. Exact customer/version/hash binding and defensive binary copies are checked.
- Explicit OCR process doubles verify the ten-second deadline, in-flight cancellation, native process reaping, mode0600 input and temporary directory cleanup. These doubles are not recognition proof. Explicit malware-scanner doubles at the HTTP intake are not ClamAV proof.
- **40 document-reprocessing regression cases passed**, preserving replay, predecessor/attempt identity, atomic terminal failure and source ownership behavior. Locked restore uses the existing local package cache and makes no lockfile changes.
- **41 Documents UI cases passed**, including the PNG explicit-review/adoption path and four typed OCR failure outcomes. Existing upload, library, version history, processing, recovered review and finance adoption regressions pass. No preselected adoption.
- Frontend TypeScript, all changed frontend source/test lint and Vite build pass. Existing runtime response-validation guards are retained with narrowly explained lint annotations; a redundant string union is simplified without changing accepted values.
- Gateway build passes with zero warnings/errors. Evidence/Document Intelligence/VS-02 compile. Native runtime collection script and actual copied engine/data OCR run pass; shell syntax and diff checks pass.

## Separate acceptance boundaries

The LOCAL Containerfile/PowerShell wiring is authored, but final Docker image build/start and the integrated browser → ClamAV → OCR → review journey are NOT RUN. Live ClamAV benign/EICAR detection remains NOT RUN, as recorded at T1-48. No PostgreSQL UAT or production/business acceptance is claimed. Full PowerShell/Compose and pinned Node24.21 checks remain pending; native SDK10.0.401 matches its pin and available Node24.19.0 is recorded without changing pins. Development GitHub Actions remain manual and none are triggered.

General document classification catalog, PDF/JPEG OCR, arbitrary layouts, handwriting/languages, persistent classification/OCR storage, operations review queues and production monitoring/operations remain open. This is an implementation/source merge with native proof for the bounded path, not full Phase1 acceptance. Exact merged application revision/tree and Expert source-compilation receipt are recorded in the matching Expert T1-49 delta and documentation.
