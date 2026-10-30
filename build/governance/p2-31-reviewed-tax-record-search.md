# P2-31 — Reviewed tax record Search

## Bounded journey

M2-WS04-E02-F03 and MON-FIN-010 require tax records to participate in governed Search and Reporting. P2-31 completes the Search increment for current Customer-reviewed tax records. Financial Profile remains the owner of each reviewed record, its immutable revision history, its exact historical `TAX_RECORD` Financial Fact and its evidence provenance. Search Retrieval stores no tax record; it builds a derived, rebuildable lexical projection inside the current authorized request.

Each record-shaped result carries the canonical tax record identifier, the exact historical source Financial Fact identifier and the exact provenance identifier. Customer Web routes the result to the tax-record history surface, which opens the current governed record and exposes its source history.

## Safety and truth boundaries

- Current Search, financial-information and document authority is evaluated through the existing Product Search boundary. Financial Profile independently re-evaluates current Self financial and evidence authority when Search reads the reviewed records.
- Tenant, Customer and actor are resolved from the authenticated session. No requester-supplied tenant, Customer or source identity is accepted.
- Search re-reads the exact historical source revision and provenance for every reviewed record. Customer, profile, type, currency, evidence and document-version mismatches fail closed.
- Duplicate identifiers, malformed records, unsupported verification states or a list larger than the complete 200-record owner response fail closed. Search never presents an incomplete tax-record corpus as complete.
- Results explicitly state that a reviewed record is not assessed tax liability, an accepted filing, payment confirmation, refund entitlement or tax advice.
- Search remains lexical. No semantic/vector retrieval, tax calculation, filing, provider verification, recommendation or advice is introduced.
- No persistence or migration is introduced. Search projections remain derived and rebuildable.

## Customer experience

Customer Search now includes reviewed tax-record names, jurisdictions, tax types, amount meanings, reported amounts, currencies, tax-year labels and periods. Selecting a result opens the exact tax-record history drawer in Financial information. Existing financial-fact and document results retain their current behavior.

## Deferred scope

Tax-record Reporting integration, semantic/vector Search, central Audit, tax calculations, filing or payment-provider integration, professional advice and physical/live acceptance remain open. Required-contribution planning remains separately blocked by OD-08/C-10 methodology approval.

## Source validation

- 53/53 focused Search Retrieval tests passed.
- 43/43 focused Customer Web Search and tax-record tests passed.
- UAT Gateway and affected Release builds passed with zero warnings and zero errors.
- Customer Web TypeScript, full ESLint, production build, bundle budget and lazy isolation passed.
- Physical PostgreSQL, installed-browser and retained LOCAL/live acceptance were not run.
