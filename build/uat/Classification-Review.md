# Document classification and review

Document Intelligence detects ITR, Form 16, bank statement, salary slip, credit
report, loan, insurance, investment, property, vehicle, GST, PAN and Aadhaar
document families from supported headings. Repeated headings of the same family
are allowed; competing families, unsupported layouts and uncertain OCR remain
Unclassified. A type is a source-derived classification, not identity or financial
verification. OCR/extraction retains its own stricter validation and failure state.

Open **Document Monitoring → Review document classifications**. The default view
shows unknown documents; All classifications includes automatic and reviewed
results. Review records bind customer, tenant, original version and SHA-256.
Reviewing stores actor, reason code, timestamp and revision atomically. A stale
review receives a conflict; reload before reviewing again. Reprocessing the same
original cannot erase review history. Review does not trigger financial adoption
or bypass extraction, scanning, consent or document access.

Classification runs when the customer requests processing. Existing versions
without classification must pass the existing processing/reprocessing flow.
Default LOCAL adapters keep classification state in memory. For retained reviews,
configure distinct, pre-provisioned Document Intelligence PostgreSQL databases per
tenant and apply `services/document-intelligence/migrations/001-document-classifications.sql`
and `002-document-ocr.sql` as their owner. Grant the runtime role CONNECT, schema USAGE and only
SELECT/INSERT/UPDATE on `document_classifications` and `document_ocr`; no DDL or DELETE grants.
Readiness fails if configured storage is unavailable. No runtime migration or
automatic copying of old in-memory state occurs.

Provide a private JSON file during onboarding:

```json
{
  "RuntimeConnections": {
    "T001": "Host=YOUR_HOST;Database=YOUR_T001_DOCUMENT_INTELLIGENCE_DB;Username=YOUR_RUNTIME_ROLE;Password=YOUR_SECRET;Timeout=5;Include Error Detail=false"
  },
  "Reviewers": { "T001": ["A900"] }
}
```

Replace placeholders with your configuration. The actor list is an explicit
LOCAL/UAT metadata-review permission, requiring current tenant-administrator
authority as well. Empty/absent assignments deny mutation. Assignments do not
grant content or financial access. They are intentionally separate from the
future production capability catalogue/role-pack approval.

```powershell
.\build\uat\Invoke-MonergyUat.ps1 -Action Prepare -Profile onboarding -EnableOnboarding -ClassificationConfigurationFile C:\private\classification.json
.\build\uat\Invoke-MonergyUat.ps1 -Action Start -Profile onboarding
```

The configuration is retained in the protected profile and reused on subsequent
starts. Keep it out of source control and shared logs. Physical migration/grant,
database restart, native OCR corpus quality and authenticated browser UAT require
operator validation. Production enablement is not claimed.

## Customer OCR text

Customer document processing records OCR separately from financial extraction.
Open the uploaded PDF/PNG/JPEG document in the vault and choose **Read scanned
text**. This displays the latest recorded transcription for that exact source
version. Refresh only reads stored output; request processing first if no result
exists. Unknown document classes can still have readable OCR text.

PDF transcription supports up to 20 unlocked pages within the existing 1 MiB
original limit. English Tesseract OCR and Poppler retain their pinned engine
versions, bounded raster sizes and timeouts. Password-protected PDFs, forms,
JavaScript and excessive pages are rejected. Financial extraction still supports
its documented single-page templates. OCR never verifies or adopts a financial
fact. Low-confidence lines and unreadable pages are explicitly marked.

The customer must retain current document-read and processing permissions;
administrator classification access does not expose scanned text. Original
source access and hash are rechecked before reading retained OCR. Tenant OCR text
uses the same configured Document Intelligence database as classification, in a
separate table. Apply both migrations before starting a retained profile; schema
readiness fails closed. Unconfigured reference profiles retain OCR only in memory.
Native corpus accuracy, physical database restart and full UAT remain unverified.
