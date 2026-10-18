# LOCAL salary-slip PNG OCR

T1-49 implements the planned Salary Slip classification and OCR requirement (master sections11/12; AR-003 COV-013/014, MON-DOC-004/005/006 partial). It uses the existing Evidence → Document Intelligence → customer review → Financial Profile journey. It adds no business role, business service, provider approval, migration or cross-service SQL.

Supported first slice:

- Printed English, one-page PNG; original upload limit1MiB; 100–4096 pixels per dimension and at most10 million pixels.
- Exactly one `SALARY SLIP`, `PAYSLIP` or `PAY SLIP` title among the first ten OCR text lines.
- One colon-labelled `Net Pay`, `Net Salary` or `Take Home Pay`; one `Pay Date` or `Payment Date`.
- Unambiguous `yyyy-MM-dd` or English day/month-name/year date. A pay period is not a pay date.
- `Currency: INR` (also USD/EUR/GBP/CAD/AUD/SGD/AED), or a supported code immediately before/after the net-pay amount. Conflicting/duplicate currencies fail.
- Nonnegative decimal with up to two decimal places and optional western comma grouping. No inferred currency, date, annualization, gross-minus-deductions calculation or OCR character correction. Gross and deductions are not additional income candidates.
- Every word in the title and selected field lines must have OCR confidence ≥90. Ambiguous, duplicate, unreadable or unsupported layouts produce zero candidates. Unrecognized titles produce a typed unclassified failure; there is no admin classification queue in this slice.

A result is an extraction candidate, not verification of authenticity. The customer checks amount, currency, pay date and source before explicitly selecting and adding it. No selection or financial write happens automatically. Both live and recovered reviews label the recognized salary slip. Raw OCR output and unrelated employee details remain transient and are not sent to the browser or logged.

## Native adapter and packaging

The operator supplies absolute `Monergy:DocumentProcessing:OcrExecutable` and `OcrDataDirectory` paths; optional `OcrLibraryDirectory` is also absolute. Missing/mismatched dependencies fail retryably unavailable. The adapter requires engine5.3.4 and English traineddata SHA256 `7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2`. No shell, network OCR provider, document-supplied command or original filename is used. One admission, a one-second admission wait and ten-second overall deadline bound native execution; cancellation/deadline kills and reaps the process tree. Stdout/stderr are bounded. Input is a private random temporary file with mode0600 under a0700 directory, removed after execution.

`Runtime.Containerfile` has a LOCAL-only native stage on the existing pinned Noble SDK image. Exact Tesseract/Leptonica/English-data package versions are recorded there. `prepare-ocr.sh` checks the data hash, copies the native dependency closure and verifies the copied engine. The final chiseled runtime receives only that tree; `Invoke-MonergyUat.ps1` configures paths for Document Intelligence. Production service images are unchanged. Transitive OS package refreshes remain an image/SBOM review concern at live acceptance, not a production dependency approval.

Native checks (Linux with pinned engine/data installed):

```sh
dotnet test tests/vs02/Monergy.Vs02.Tests --no-restore --filter 'FullyQualifiedName!~PhysicalClamdTests'
```

`MONERGY_TEST_TESSERACT` / `MONERGY_TEST_TESSDATA` override native test paths. Synthetic PNG fixtures contain no real personal data. Native tests use the actual engine for two differing salary-slip layouts, an unclassified invoice and the exact-byte Evidence HTTP/processing journey. Explicit process doubles separately check deadline/cancellation/cleanup; explicit scanner doubles in this HTTP test are not live antivirus proof.

## Remaining live acceptance

Docker is NOT RUN in this workspace. Build the LOCAL image, start the onboarding profile (including the T1-48 scanner), upload the benign salary-slip fixture, extract/review it, confirm no profile mutation before selection, adopt the selected value, reopen the completed result, and verify exact customer/version provenance. Repeat unclassified/ambiguous/low-confidence and OCR-unavailable outcomes. The live ClamAV benign/EICAR check, full PowerShell/Compose startup and pinned Node24.21 verification also remain open. Native packaging proof does not establish operation inside the final chiseled container. No PostgreSQL or production acceptance is claimed.

Other document types, PDF/JPEG OCR, handwriting, other languages, arbitrary salary layouts, persistent classification/OCR storage, a review queue and production operations remain planned work. The original151 requirements /81 canonical features denominator is unchanged.
