# T1-51 - Planned upright JPEG salary-slip OCR

Base application main: `9b2d4af81f91862920b9bfe7139db505e5a15d80` (T1-50, PR100). Architecture pin: `90934b51b2b203795acf95da2252a2cf1a1d1f4c`. Expert base: `0f00931e8748f19131ae3b4edd06b43c13b79924` (PR52). Candidate branch: `work/t1-51-salary-slip-jpeg-ocr`.

## Requirement and implemented journey

This continues master sections11/12 Salary Slip classification / scanned-image OCR, with AR-003 COV-011/013/014 and MON-DOC-004/005/006 partial coverage. JPEG was already an accepted upload format; supported upright printed English JPEG salary slips now enter the existing extraction/review journey alongside PNG and single-page scanned PDF. The original151 normalized requirements /81 canonical features denominator remains unchanged. Full document-classification/OCR or Phase1 acceptance is not implied.

A current customer can extract a supported JPEG, see recognized Salary Slip and a single INCOME Net pay candidate, review explicit amount/currency/pay date/source/confidence, then select/adopt through the existing Financial Profile owner path. Current/recovered views recognize extraction version `salary-slip-jpeg-ocr/1.0.0`; no default selection or automatic financial mutation. Original JPEG bytes/hash/customer/document version remain the source identity; no conversion or derivative Evidence document is created.

Evidence supplies JPEG over the existing bounded binary processing-source field, with dedicated token and current tenant/customer/version binding. The reader and extractor verify the exact original hash before native OCR. Earlier upload scanning, current session/customer/AM and execution authorization rechecks remain intact. No business role/capability, Consent, schema/migration, cross-service SQL, business service, native dependency pin, remote OCR provider or production approval is introduced.

## Bounded JPEG path

- Original input <=1MiB, dimensions100-4096 pixels per side and <=10 million pixels; 8-bit baseline/progressive, one grayscale or three color components.
- Bounded segment/frame/scan/entropy-marker parsing requires exactly one supported frame, valid component references, bounded scan/segment counts and a sole terminal EOI. Truncation/trailing data/ambiguous structure, unsupported frame/precision, CMYK and malformed dimensions fail before native decoding.
- EXIF is optional. Main-image orientation must be absent or a single valid upright orientation1 in valid little/big-endian TIFF metadata. Rotated/reflected, duplicate or malformed EXIF/orientation is rejected; no automatic rotation/repair is attempted.
- The existing pinned Tesseract5.3.4/hash-pinned English data reads exact JPEG bytes using the existing private input, single admission, one-second admission wait, ten-second deadline, bounded stdout/stderr and kill/reap/cleanup behavior. Native JPEG warnings/errors reject damaged compressed data even when header preflight succeeds; no partial candidate set is returned.
- Shared salary title/explicit net-pay/currency/unambiguous pay-payment date and >=90 word-confidence validation remain unchanged. No guessing, arithmetic, annualization, character correction or authenticity verification.
- `processing.jpeg.unsupported` gives a safe customer outcome and commits a failure with zero facts. Native dependency failures remain retryable, cancellation remains cancellation and raw EXIF/native/OCR diagnostics are not returned or logged.

Existing LOCAL binary/library/data packaging already supports JPEG; no Containerfile, PowerShell, tool pin or lockfile changes are needed. Exact bounds and live acceptance instructions are in `build/uat/Salary-Slip-OCR.md`.

## Verification

**154 VS-02 cases pass**, including28 added cases:27 JPEG/native/structure/provenance/validation/failure cases plus one owner HTTP/processing JPEG extension. Existing126 scanner/PNG/PDF/CSV/processing/contract cases remain green. **40 document-reprocessing cases pass**, retaining replay/attempt/predecessor/source-owner and terminal atomic behavior.

Actual pinned OCR covers two printed JPEG layouts, baseline/progressive/grayscale encodings, upright EXIF in both byte orders, unclassified invoice handling and refusal of prematurely truncated entropy with a decoder warning. Actual Evidence HTTP transports exact JPEG bytes into native OCR/processing with original customer/version/hash binding and defensive copying. Explicit scanner fixtures in that intake test are not live ClamAV proof.

Malformed/truncated/extra data, missing/duplicate frames, bad scans/dimensions/precision/encodings, CMYK, malformed/rotated/duplicate EXIF, invalid IFD offsets, wrong MIME/hash/text/binary/size and cancellation are checked before native decode where applicable. Shared low-confidence/missing-field validation is exercised on the JPEG path. Typed unsupported-JPEG processing commits one failure event and zero facts.

**47 Documents UI cases pass**, adding JPEG explicit selection/adoption, recovered JPEG recognition/read-only review and the unsupported-JPEG message to44 existing cases. TypeScript, all changed frontend source/test lint, Vite and Gateway build pass; Gateway has zero warnings/errors. Evidence/Document Intelligence/VS-02/reprocessing compile. The existing copied native Tesseract/library/data tree reads progressive JPEG outside Docker. Diff checks pass and no package lock changes or development GitHub Actions occur.

## Remaining acceptance

Final Docker image/start and integrated browser -> live ClamAV -> JPEG OCR -> explicit review/adoption remain NOT RUN. Live benign/EICAR scanner acceptance remains NOT RUN from T1-48; the compiled physical antivirus case is excluded. Full PowerShell/Compose, pinned Node24.21, PostgreSQL UAT and production/business acceptance remain open. SDK10.0.401 matches its pin; available Node24.19.0 is recorded without changing pins.

General phone-camera quality/perspective/rotation handling, CMYK/other JPEG encodings, multi-page/interactive/encrypted PDFs, other document classes/layouts/languages, handwriting, persistent classification/OCR and operations review queues remain open. Exact merged application revision/tree and System Expert synchronization evidence are recorded in the matching T1-51 Expert delta/receipt.
