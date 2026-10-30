# P2-32 — Reviewed tax record Reporting

## Bounded journey

P2-32 advances `MON-FIN-010` / `M2-WS04-E02-F03` with one bounded Reporting journey.
Current Customer-reviewed tax records now join the governed basic financial summary as distinct
`TaxRecord` items. Financial Profile remains the owner of the record, its immutable revision
history, its exact historical `TAX_RECORD` Financial Fact and its evidence provenance. Reporting
stores only the generated report and its governed source snapshot.

## Safety and truth boundaries

- Reporting keeps the existing current-session `reporting.report.generate` authorization and
  Customer boundary. Financial Profile independently re-evaluates current Self financial-read and
  document-read authority before releasing reviewed records.
- Tenant, Customer and actor come from the authenticated session. The browser submits no record,
  fact, revision, provenance, evidence or document-version identifiers.
- Reporting re-reads every record's exact historical Financial Fact revision and exact Financial
  Profile provenance. Customer, profile, type, currency, evidence and document-version mismatches
  fail closed.
- A duplicate identifier, malformed record, continuation page or record set larger than the
  complete 200-record owner response fails closed. Reporting never labels an incomplete record set
  as a complete summary.
- Each item retains the tax-record identifier, source Financial Fact identifier and revision,
  provenance, evidence and document-version identifiers. The generated JSON export contains the
  same source reference released by Reporting.
- Customer Web labels the item as a reviewed tax record and routes it to the exact governed tax
  history. Existing Financial Fact and Financial Rules item behavior is unchanged.
- A reviewed record does not establish assessed tax liability, an accepted filing, payment
  confirmation, refund entitlement, provider verification or tax advice.

## Deferred scope

Tax calculation, filing, provider/payment verification, refund determination, reconciliation,
professional tax advice, central Audit and physical/live acceptance remain open. Required-
contribution planning remains separately blocked by the approved-methodology decision.

## Source validation

- Reporting owner tests cover exact historical source/provenance, complete-corpus handling and
  tenant/Customer/source mismatch failure.
- Customer Web tests cover the reviewed-tax label, exact history route, technical source details,
  safe language and export identity.
- Reporting and UAT Gateway Release builds, TypeScript, ESLint, production bundle and lazy
  isolation are required before merge.
- Physical PostgreSQL, installed-browser and retained LOCAL/live acceptance remain pending.
