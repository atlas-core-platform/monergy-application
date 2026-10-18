# LOCAL salary-slip image and scanned-PDF OCR

T1-49 (PNG), T1-50 (single-page scanned PDF) and T1-51 (upright JPEG) implement bounded slices of the planned Salary Slip classification and OCR requirement (master sections11/12; AR-003 COV-013/014, MON-DOC-004/005/006 partial). It uses the existing Evidence → Document Intelligence → customer review → Financial Profile journey. It adds no business role, business service, provider approval, migration or cross-service SQL.

Supported first slice:

- Printed English PNG, supported upright JPEG or an unlocked, single-page scanned PDF; original upload limit 1MiB. PNG/JPEG: 100–4096 pixels per dimension and at most 10 million pixels. PDF: one page, no forms/scripts, 36–1440 points per page dimension; render the complete page with longest edge 2500 pixels and output at most 1MiB. A multi-page PDF is rejected as a whole, never silently reduced to its first page.
- Exactly one `SALARY SLIP`, `PAYSLIP` or `PAY SLIP` title among the first ten OCR text lines.
- One colon-labelled `Net Pay`, `Net Salary` or `Take Home Pay`; one `Pay Date` or `Payment Date`.
- Unambiguous `yyyy-MM-dd` or English day/month-name/year date. A pay period is not a pay date.
- `Currency: INR` (also USD/EUR/GBP/CAD/AUD/SGD/AED), or a supported code immediately before/after the net-pay amount. Conflicting/duplicate currencies fail.
- Nonnegative decimal with up to two decimal places and optional western comma grouping. No inferred currency, date, annualization, gross-minus-deductions calculation or OCR character correction. Gross and deductions are not additional income candidates.
- Every word in the title and selected field lines must have OCR confidence ≥90. Ambiguous, duplicate, unreadable or unsupported layouts produce zero candidates. Unrecognized titles produce a typed unclassified failure; there is no admin classification queue in this slice.

A result is an extraction candidate, not verification of authenticity. The customer checks amount, currency, pay date and source before explicitly selecting and adding it. No selection or financial write happens automatically. Both live and recovered reviews label the recognized salary slip. Raw OCR output and unrelated employee details remain transient and are not sent to the browser or logged.

## Native adapter and packaging

The operator supplies absolute `Monergy:DocumentProcessing:OcrExecutable` and `OcrDataDirectory` paths; PDF additionally requires absolute `PdfInfoExecutable` / `PdfRenderExecutable` paths; optional `OcrLibraryDirectory` is also absolute. Missing/mismatched dependencies fail retryably unavailable. The adapter requires engine5.3.4 and English traineddata SHA256 `7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2`. No shell, network OCR provider, document-supplied command or original filename is used. Each native adapter has one admission and a one-second admission wait. PDF metadata/rendering has a six-second deadline and the complete PDF-to-OCR extraction has a ten-second deadline; cancellation/deadline kills and reaps the process tree. Stdout/stderr are bounded. Input is a private random temporary file with mode0600 under a0700 directory, removed after execution.

`Runtime.Containerfile` has a LOCAL-only native stage on the existing pinned Noble SDK image. Exact Tesseract/Leptonica/English-data package versions are recorded there. `prepare-ocr.sh` checks the data hash, copies the native dependency closure and verifies the copied engine. The final chiseled runtime receives only that tree; `Invoke-MonergyUat.ps1` configures paths for Document Intelligence. Production service images are unchanged. Transitive OS package refreshes remain an image/SBOM review concern at live acceptance, not a production dependency approval.

Native checks (Linux with pinned engine/data installed):

```sh
dotnet test tests/vs02/Monergy.Vs02.Tests --no-restore --filter 'FullyQualifiedName!~PhysicalClamdTests'
```

`MONERGY_TEST_TESSERACT` / `MONERGY_TEST_TESSDATA` override native test paths. Synthetic PNG fixtures contain no real personal data. Native tests use the actual engine for two differing salary-slip layouts, an unclassified invoice and the exact-byte Evidence HTTP/processing journey. Explicit process doubles separately check deadline/cancellation/cleanup; explicit scanner doubles in this HTTP test are not live antivirus proof.

## T1-50 PDF behavior and native dependency

The PDF adapter requires `pdfinfo` and `pdftoppm` version 24.02.0. The LOCAL Noble stage pins `poppler-utils` / `libpoppler134` to `24.02.0-1ubuntu9.9` and copies their library closure into the existing native tree. PDF metadata must contain unique Pages/Encrypted/JavaScript/Form/Page-size fields with one unlocked non-interactive page and bounded dimensions. Bad/duplicate/missing metadata, unsupported page size, locked/multi-page/malformed PDFs and oversized/warning/error render output fail with `processing.pdf.unsupported`; no partial candidates are emitted. Missing/mismatched dependencies or renderer deadline fail retryably unavailable. Caller cancellation remains cancellation.

Only the original PDF bytes/hash/evidence/customer/version are authoritative source identity. The rendered PNG is captured through a bounded binary stdout pipe, remains transient and is not staged as another Evidence document. Candidate extraction version is `salary-slip-pdf-ocr/1.0.0`; field parsing and ≥90 confidence validation are shared with PNG. Current/recovered review identifies the salary slip and continues to require explicit selection/adoption. Raw PDF metadata, native diagnostics and unrelated OCR text never enter customer responses or logs.

Synthetic scanned-PDF fixtures have an image-only page and no selectable text. Actual pinned Poppler → Tesseract checks cover two layouts, an invoice and the exact PDF Evidence HTTP/processing journey; actual Poppler rejects multi-page, encrypted and malformed files. Explicit process doubles separately check spoofed metadata, forms/scripts/page bounds, version mismatch, deadline/cancellation/private-input cleanup and prompt oversized-output refusal. The copied native binary/library/data tree also performs actual PDF render → OCR outside Docker. This does not establish general born-digital/font compatibility, arbitrary salary layouts or operation in the final chiseled container.

## T1-51 JPEG behavior

The existing pinned Tesseract/Leptonica native tree also reads supported JPEG bytes directly. No conversion, new native executable, software pin, image or service is added. JPEG source bytes/hash/customer/version remain the candidate provenance and extraction version is `salary-slip-jpeg-ocr/1.0.0`. Existing explicit title/field/date/currency/confidence checks and customer selection/adoption remain mandatory in both current and recovered result views.

Supported encodings are 8-bit baseline/progressive JPEG with one grayscale or three color components, within the existing original1MiB /4096-pixel-side /10-million-pixel bounds. Header/segment/frame/scan/entropy-marker boundaries and a sole terminal EOI are checked before native decoding. EXIF can be absent or have a single valid main-image orientation entry1; rotated/reflected, duplicate or malformed orientation metadata is refused. CMYK, other frame/precision encodings, malformed/oversized content and ambiguous structure are outside this slice. Native JPEG decoding must also complete without warnings/errors; header preflight alone cannot prove complete compressed pixels. Damaged entropy with a forged terminal marker is rejected by the actual decoder warning check, without partial facts.

`processing.jpeg.unsupported` asks the customer for a clear upright/new JPEG. Native dependency unavailability remains retryable and cancellation remains cancellation. Raw EXIF/native diagnostics are not exposed, no automatic orientation/character/field repair occurs and authenticity is not verified. Actual native tests cover baseline/progressive/grayscale, both EXIF byte orders, an unclassified invoice, damaged entropy and the exact JPEG Evidence HTTP/processing journey. The existing copied native binary/library/data tree reads progressive JPEG outside Docker. Arbitrary phone-camera quality, perspective, rotations, CMYK and other layouts/languages remain open.

## Remaining live acceptance

Docker is NOT RUN in this workspace. Build the LOCAL image, start the onboarding profile (including the T1-48 scanner), upload the benign PNG, JPEG and scanned-PDF salary-slip fixtures, extract/review it, confirm no profile mutation before selection, adopt the selected value, reopen the completed result, and verify exact customer/version provenance. Repeat unclassified/ambiguous/low-confidence and OCR-unavailable outcomes. The live ClamAV benign/EICAR check, full PowerShell/Compose startup and pinned Node24.21 verification also remain open. Native packaging proof does not establish operation inside the final chiseled container. No PostgreSQL or production acceptance is claimed.

Other document types, multi-page/interactive/encrypted PDFs, broader JPEG encoding/photo quality, handwriting, other languages, arbitrary salary layouts, persistent classification/OCR storage, a review queue and production operations remain planned work. The original151 requirements /81 canonical features denominator is unchanged.
