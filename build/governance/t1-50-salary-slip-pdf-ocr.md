# T1-50 - Planned single-page salary-slip PDF OCR

Base application main: `eba9634c500eb72abd9b2fd71de98dab346fac6a` (T1-49, PR99). Architecture pin: `90934b51b2b203795acf95da2252a2cf1a1d1f4c`. Expert base: `7c0d8c375e508f223490d37d08ab0dce5427a456` (PR51). Candidate branch: `work/t1-50-salary-slip-pdf-ocr`.

## Requirement and implemented journey

This continues the existing master sections11/12 Salary Slip classification / scanned-document OCR requirement and AR-003 COV-013/014, MON-DOC-004/005/006 partial coverage. It adds single-page scanned PDFs to T1-49's PNG path. The original 151 normalized requirements /81 canonical features denominator is unchanged; neither the full document catalog nor complete OCR acceptance is asserted.

A current customer can extract a supported printed English salary slip from an unlocked single-page image-only PDF, see the recognized Salary Slip and one INCOME Net pay candidate, review amount/currency/pay date/source/confidence and explicitly select/adopt it through the existing Financial Profile owner path. Current and recovered result views recognize extraction version `salary-slip-pdf-ocr/1.0.0`. No default selection or automatic financial mutation. The earlier upload malware gate and existing current session/customer/AM/workload checks remain intact.

Evidence returns original PDF bytes from the existing workload-only processing-source endpoint using the bounded binary field introduced at T1-49. Document Intelligence verifies the original source hash/customer/document version before rendering. The temporary raster is not staged as a new Evidence document; candidate provenance points to the original PDF version. No schema/migration, cross-service SQL, business role/capability, Consent change, business service, remote OCR provider or production provider approval is added.

## Bounded native path

- Actual local Poppler24.02.0 checks unique page/encryption/script/form/geometry metadata and renders the full sole page. Multi-page, locked, interactive, malformed, conflicting metadata and unsupported page sizes fail without partial candidates.
- Original input remains at most 1MiB. Page dimensions are 36-1440 points; rendering uses longest edge2500 pixels and a binary stdout limit1MiB. Native metadata, diagnostic and OCR output are bounded; raw metadata/diagnostics/unrelated employee details never reach customer responses or logs.
- Absolute operator-owned executable paths, no shell/document-supplied command or original filename, private0700 input directory/0600 file, one renderer admission, one-second admission wait, six-second metadata/render deadline and ten-second PDF-to-OCR deadline bound work. Cancellation/limits stop, kill/reap and clean up native work. A shared process transport now handles binary/text output and promptly interrupts both pipes on a limit breach; the PNG adapter uses that same transport.
- Existing pinned Tesseract5.3.4 / English-data SHA256 and strict salary-field/title/confidence validation are reused. Explicit net pay, supported currency, unambiguous pay/payment date and >=90 word confidence remain required. There is no currency/date/pay-period inference, arithmetic, annualization or authenticity verification.
- Customer failure `processing.pdf.unsupported` explains readable/unlocked/single-page support; missing/mismatched renderer or renderer deadline remains retryably OCR unavailable. Cancellation remains cancellation.
- LOCAL Containerfile pins Noble Poppler packages `poppler-utils` and `libpoppler134` to `24.02.0-1ubuntu9.9`; the existing native collection script copies their library closure and verifies both tool versions. PowerShell configures two additional operator paths. Production service images stay unchanged.

## Verification

**126 VS-02 backend cases pass**, including 21 added cases:20 PDF/render/validation/failure/process cases and one actual owner HTTP/processing PDF extension. Existing105 scan/PNG/CSV/processing/contract cases remain green. **40 document-reprocessing cases pass**, preserving replay/attempt/predecessor identity, source ownership and terminal atomic behavior.

Actual pinned Poppler -> pinned Tesseract covers two different image-only salary-slip PDF layouts, including the explicit date/currency/amount variants, plus unclassified scanned invoice handling. PDF fixtures have no selectable text and the successful rendered page was visually inspected. Actual Poppler rejects multi-page, encrypted and malformed input. Actual Evidence HTTP carries exact PDF bytes into native OCR and processing with exact original customer/version/hash checks and defensive copying. Explicit scanner fixtures in that HTTP test do not establish live ClamAV scanning.

Explicit process doubles separately verify unique metadata/one page/unlocked/no forms/scripts/page bounds, version mismatch, private input modes, six-second renderer deadline, in-flight cancellation, process reaping/temp cleanup and prompt oversized-raster interruption. They do not purport to render PDF or recognize text. A typed unsupported-PDF processing failure commits one failure event and zero facts.

**44 Documents UI cases pass**, including explicit PDF selection/adoption, recovered PDF recognition/read-only review and the unsupported-PDF customer outcome; existing41 cases remain green. TypeScript, all changed frontend source/test lint, Vite and Gateway build pass. Gateway has zero warnings/errors; Evidence, Document Intelligence, VS-02 and reprocessing compile. Native package collection plus copied Poppler/library -> Tesseract/data render/OCR passes outside Docker. Diff checks and shell syntax pass; no package lock changes or GitHub Actions are triggered.

## Remaining acceptance

Final Docker image build/start and integrated browser -> live ClamAV -> PDF render/OCR -> explicit review/adoption are NOT RUN. Live benign/EICAR scanning remains NOT RUN from T1-48. The physical antivirus case is compiled and explicitly excluded. Full PowerShell/Compose, pinned Node24.21, PostgreSQL UAT and production/business acceptance remain open. Native SDK10.0.401 matches its pin; available Node24.19.0 is recorded without changing pins.

General born-digital/font/layout compatibility, multi-page/interactive/encrypted PDFs, JPEG OCR, other document classes, arbitrary salary layouts, handwriting/languages, persistent classification/OCR and operations review queues remain open. Operational bounds and live acceptance instructions are in `build/uat/Salary-Slip-OCR.md`. Exact merged revision/tree and System Expert compilation/verification are recorded in the matching Expert T1-50 delta/receipt.
