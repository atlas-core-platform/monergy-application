# T1-57 — Financial information XLSX intake

Base Application `8500f123a8150cbc4dd9b3b3902c219577100057` (PR106), Expert
`8e6195971aa8b928ac9d48ab9a09cdf404bb3e02` (PR58), Architecture
`90934b51b2b203795acf95da2252a2cf1a1d1f4c`. Branch
`work/t1-57-financial-xlsx-intake`.

Master §§9–13/41 explicitly includes Excel financial files. This bounded increment
advances Upload and Extraction under MON-DOC-002/003/007 and the existing partial
MON-FIN source-fact journeys. Those original deliverables remain Partial.

## Customer and owner behavior

Documents provides a downloadable `.xlsx` financial information template using
Category, Description, Amount, Currency, Date and Source. One visible worksheet
holds a header and1–100 data rows. The seven existing categories are INCOME,
EXPENSE, LOAN, ASSET, INVESTMENT, INSURANCE and TAX. Dates must be exact YYYY-MM-DD
text; Amount is a plain decimal string or a numeric cell with General format.
The included row is explicitly synthetic example data to replace before upload.

The existing1MiB original-byte intake, signature validation, malware-check gate,
SHA256 identity, version and exact tenant/customer scope apply to XLSX. The binary
HTTP processing adapter preserves those bytes and independently verifies the
owner hash; binary content is never decoded as text. Template MIME variants are
normalized only for an `.xlsx` filename; `.xls` and `.xlsm` remain unsupported.
Processing/recovery controls recognize the new MIME type. Review labels copied
Excel cells explicitly; the customer must still choose and confirm adoption.
No upload or extraction writes financial truth automatically.

Document Intelligence uses a bounded BCL ZIP/XML reader, without spreadsheet
execution, filesystem extraction, network fetches or a new dependency. It accepts
only five to ten explicitly named package parts, each at most512KiB, with total
expanded XML at most1MiB and compression ratio at most100. Actual expansion is
independently bounded and every included XML part is parsed with DTDs prohibited
and external resolution disabled. Single-sheet relationships, sequential six-cell
rows, supported styles, plain inline/shared strings and exact required headers
are checked. Existing CSV category/currency/amount/date/duplicate validation is
shared without changing CSV fact identities. XLSX fact identity is deterministic
and carries original document/version/hash, row location and financial-xlsx/1.0.0.

Formulas including cached results, external links, macro parts, extra/duplicate/
traversal parts, hidden rows/columns/sheets, merged cells, unsupported formatting,
numeric date serials, rich strings, malformed archives/XML, duplicate data and
bounds violations reject the whole workbook. Unsupported/damaged files produce
no candidate financial facts. This is a financial-template reader, not arbitrary
bank-statement mapping or support for every Excel writer/layout. Ordinary theme
and document-property parts emitted by an independent writer are supported.

## Verification and remaining work

200 nonphysical/non-OCR VS02 checks pass, including41 XLSX cases, plus40
reprocessing checks. They include the actual downloadable template, a second
fixture independently opened/saved by openpyxl, all seven categories, shared
strings/reordered headers, cancellation, row/ZIP/XML limits and malformed/unsafe
packages. Actual owner HTTP tests cover original byte/hash delivery, tenant scope,
scan-gated intake, processing and explicit Financial Profile adoption. Repository
and scanner dependencies are explicit doubles; these are not live scanner or
physical database tests. Independent openpyxl reading confirms the downloadable
sample amount is numeric and its date is text.

61 Documents UI checks pass, including7 new XLSX cases covering official/empty/
ZIP/octet-stream browser MIME variants, exact template bytes, explicit upload,
processing permission and explicit review/adoption. TypeScript, all changed
frontend lint and Vite pass. Gateway/owner builds pass with zero warnings/errors;
the built public template is byte-identical to source. No migrations, new
packages/lockfiles or development GitHub Actions. SDK10.0.401 matches the pin;
available Node24.19.0 does not prove pinned Node24.21.

Live Docker/ClamAV benign/EICAR intake, durable byte storage, physical PostgreSQL,
PowerShell/Compose and integrated browser UAT are NOT RUN. Earlier native OCR
receipts are retained, not rerun for this unit. Legacy XLS, macro-enabled XLSM,
arbitrary workbook/bank layouts, larger/multiple-sheet workbooks, broader OCR
classes and complete financial-domain records remain open. No business or
production acceptance is inferred.

After paired merge,57 numbered units comprise56 implementation units and1
governance closure (T1-04). Original29 deliverables remain0 complete,17 partial
and12 pending journeys. Original151 IDs/81 features and126 Phase1-applicable
membership are unchanged. Exact merged source/tree and compiled knowledge are
recorded in the paired Expert receipt.
