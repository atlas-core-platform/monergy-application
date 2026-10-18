# Document Intelligence reference implementation

MWP-03-D12 adds a bounded `SIMULATOR` candidate for
`M2-WS03-E02-F03 — Reprocessing & explicit failure handling`.

The service owns processing and reprocessing state, execution-attempt identity,
immutable predecessor linkage, extracted candidate facts, and terminal event
intent. Evidence remains the authority for customer/evidence/version identity;
the emitted facts remain non-authoritative inputs to later normalization.

## Semantics

- CID-026 `ReprocessDocument` creates a new operation linked to a terminal
  predecessor for `FAILED_PROCESSING` or `UPDATED_EVIDENCE` intent.
- Transport redelivery replays the existing operation after current policy is
  checked. It does not extract or append terminal intent again.
- Status disclosure and extraction both re-evaluate the current owner-local
  reference policy; protected extraction is rechecked after the Evidence read.
- CID-025 execution retry remains the existing retry mechanism and is distinct
  from a new CID-026 business operation.
- CID-028 records successful validated-source-fact intent; CID-029 records a
  safe, classified terminal failure intent. State and event intent commit under
  the same reference-repository lock.
- Idempotency is scoped by contract/version, customer, and caller key and is
  bound to the complete canonical semantic payload using an unambiguous typed
  identity. Predecessor eligibility and snapshot capture are revalidated at
  atomic child admission.
- Committed fact collections and success-event payloads are owner-frozen
  snapshots; repository reads do not expose mutable collection aliases.

## T1-17 read-only completed-result projection

T1-17 adds a separate workload-only LOCAL/UAT read projection for a completed
document-processing result. This path is not an execution contract and does not
reuse the execution-policy check.

Job Management first authorizes the Customer's `job-management.job.read` and
`evidence.document.read` access, then calls Document Intelligence with the
trusted Job Management workload and the durable processing/document/customer
identity. Document Intelligence reads the existing processing repository
directly and returns candidate facts only for the exact completed record.

The projection does not invoke extraction, retry, reprocessing, normalization,
or Financial Profile mutation. The customer product response further narrows
the result to accepted Income/Expense candidate fields and omits customer,
Evidence and failure/retry internals. T1-49 additionally preserves the extraction version to label recognized salary-slip candidates during recovered review.

## T1-42 financial information CSV

The LOCAL/UAT processor additionally accepts UTF-8 `text/csv` using the customer-downloadable template: Category, Description, Amount, Currency, Date, Source. It copies explicit values into the existing seven candidate categories. Header order/case, quoted fields, embedded newlines and UTF-8 BOM are supported. An invalid row rejects the entire file; 1–100 records and existing size, source hash and owner checks apply. CSV candidates require explicit customer review and do not update Financial Profile automatically. This does not provide arbitrary bank-statement mapping, Excel parsing, OCR, inferred document classification or production-provider acceptance.

## Targeted verification

Run from the repository root:

```powershell
dotnet test tests/document-reprocessing/Monergy.DocumentReprocessing.Tests/Monergy.DocumentReprocessing.Tests.csproj --no-restore
dotnet test tests/vs02/Monergy.Vs02.Tests/Monergy.Vs02.Tests.csproj --no-restore
./build/verify-document-reprocessing.ps1 -SelfTest
./build/verify-document-reprocessing.ps1
git diff --check
```

The scenario inventory is maintained in
`build/governance/d12-scenario-matrix.json`.

## Limits

This implementation is in-memory reference evidence restricted to `LOCAL` and
`CI_EPHEMERAL`. It does not establish restart durability, exactly-once delivery,
a production broker, general OCR/AI/provider compatibility, automatic financial
correction, deployment readiness, or acceptance. D11 is
not a source dependency; its deferred acceptance and recorded OCI security
failure remain unchanged.

## T1-49 salary-slip image OCR

The LOCAL customer flow supports a bounded first scanned-document path: printed English salary slips in PNG format. Evidence supplies original bytes over its existing workload-only processing-source endpoint; Document Intelligence verifies the exact source hash and customer/version identity. A pinned local Tesseract 5.3.4 engine reads the image. Deterministic recognition requires one Salary Slip / Payslip title in the first ten text lines, one labelled net-pay value and an explicit pay/payment date. Currency must be labelled or an explicit supported code beside net pay. Only a single INCOME net-pay candidate is published, with source version, OCR line/confidence and extraction version. Existing customer selection/adoption remains mandatory.

See `build/uat/Salary-Slip-OCR.md` for exact formats, bounds, native verification and the separate unexecuted Docker acceptance steps. This is a partial MR sections11/12 and AR-003 COV-013/014 implementation, not the full document catalog or general salary-slip layout/language support.

## T1-50 single-page scanned salary-slip PDF OCR

The same LOCAL customer journey now accepts unlocked, single-page image-only salary-slip PDFs. Pinned Poppler checks page/security metadata and renders the full page through a bounded binary stdout pipe before the existing pinned Tesseract OCR and shared strict salary-field validator. Facts retain the original PDF evidence/customer/version and use `salary-slip-pdf-ocr/1.0.0`; the rendered image is transient. Multi-page, locked, interactive, malformed or oversized PDFs fail without partial candidates; native unavailability remains a retryable failure. Exact source identity and current authorization checks remain unchanged. Current and recovered reviews label the recognized salary slip and require explicit customer selection before financial adoption.

This remains a partial master sections11/12 / COV-013/014 implementation. General PDF layout/font support, multi-page processing, other document classes/formats/languages and final Docker/ClamAV integration acceptance remain open. See `build/uat/Salary-Slip-OCR.md` and `build/governance/t1-50-salary-slip-pdf-ocr.md` for bounds and native evidence.
