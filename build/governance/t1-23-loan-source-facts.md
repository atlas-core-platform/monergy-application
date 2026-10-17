# T1-23 — Source-reviewed Loan Amount in Financial Passport

Status: SOURCE CANDIDATE, local .NET/React/UAT tests not executed in current environment.
Base: application main `f34e74530eca84727ae11773c5f016a8a516030d` (T1-22).
Sources: Master §§13,17,20,41; AR-003 Customer Financial Position; canonical `M2-WS04-E01-F03` (Loans, Credit Cards & Credit Profile) **partial** and `M2-WS03-E02-F02` (Structured Extraction & Confidence) **partial**.

## Intended customer journey

Customer opens Documents and explicitly selects a supported structured-text record with format `LOAN|label|numeric-value|ISO-4217 currency|yyyy-MM-dd|source-location|confidence`. The current LOCAL reference extractor emits a **non-authoritative** LOAN candidate tied to exact immutable document version, Evidence ID, processing ID and extraction/validation versions. Customer reviews, selects and expressly confirms the LOAN item. Existing server product endpoint re-evaluates customer access and exact selected source IDs; Financial Profile retains sole authority to normalize/persist it and record its provenance. Customer opens My Finances → Loan information and sees the reviewed numeric value, original label, source/version and revision/history, plus supported Passport coverage.

## Important financial truth limitation

A typed `LOAN` source amount is **not**, by itself, verified outstanding debt, original principal, EMI, current balance, sanctioned amount, eligibility, account ledger or net worth. Its business meaning is retained in the extracted source label, not inferred from the fact type. No loan totals/creditworthiness recommendations are created. This first small slice does not invent loan-account attributes or interest/repayment formulas.

## Services, API, database, UI

- Document Intelligence: existing FixtureDocumentExtractor supports an additional `LOAN` source fact; still LOCAL structured text, no OCR/provider integration.
- Job Management: existing processing/recovered-result lifecycle unchanged.
- Evidence: existing immutable document/version and provenance unchanged.
- Financial Profile: `FinancialObjectTypes.Loan` already exists; existing CID-031 normalization, current revisions and audit remain authoritative.
- Product UAT Gateway: extends supported review and recovered result type allowlists only, still applies current owner security and exact source version.
- Customer UI: Documents displays Loan candidate, recovery, review and source/history links; Financial information has Loan filter/deep link, Passport third category/coverage and count.
- Reporting/Search: still bounded to their earlier supported Income/Expense customer projections, **not** claimed to include Loans in this increment.
- Persistence: existing financial object/fact schema only; no migration.
- Consent/RBAC: existing authenticated resource permissions and applicable purpose/Consent rechecks unchanged.

## Focused implementation tests authored

1. Reference extractor emits a LOAN candidate with source IDs, amount/currency, version; unsupported ASSET remains rejected by this bounded extractor.
2. Documents requires explicit Loan candidate checkbox, then submits exactly selected source-fact ID and receives a Financial Profile owner receipt.
3. Financial information category filter and exact source provenance display the reviewed Loan fact; no aggregate outstanding total.
4. Passport represents 3 supported categories and a Loan fact count without any new loan analytics or financial health score.
5. Earlier Income/Expense tests should continue passing.

## Definition of done

Focused .NET extractor/financial normalization tests, React/Vitest/TypeScript, local owner-backed UAT including history/recovery, security/tenant scope, and exact-source Expert sync validation, with outcome evidence. No GitHub Actions in development. Source merge != complete UAT or full loan/credit profile. No cross-source loan reconciliation, regulated data or credit bureau integration.
