# T1-40 — Reviewed Tax source figures in basic Reports

Base Application main: `653b28e8271790a719ce5c630accbb180b857f98` (native verification remediation #88).
Requirements: Master §§13,20,30,41; M2-WS08-E02-F01/F03 (partial).

Reporting's existing LOCAL/UAT product adapter now includes explicitly reviewed `TAX` Financial Profile facts, alongside Income, Expense, Loan, Asset, Investment and Insurance. It reads the current authorized customer through CID-030 and validates every exact CID-033 fact/provenance/Evidence association. Reporting remains the report owner; Financial Profile remains the financial-data authority. No new table, permission, contract, tax calculation, filing, provider, refund or assessed-liability claim.

The Customer sees the source figure in basic Reports, opens its exact `/finances?fact=` history, and exports the unchanged Reporting-owned JSON content. The page explicitly distinguishes document-stated Tax figures from assessed taxes owed, accepted filings and refunds.

## Focused verification

- Reporting owner tests: **4 passed**, including Tax fact/provenance inclusion, unsupported-type exclusion, missing authorization boundary, cross-customer denial and specifically mismatched Tax provenance rejection.
- Reports component suite: **8 passed**. The new Tax check reads the downloaded Blob and verifies its text exactly equals Reporting's export payload; no client reconstruction.
- TypeScript and modified-test lint: **passed**. The final Tax export check also passed independently after its FileReader type guard was added.
- Reports source retains **3 pre-existing lint findings**, unchanged from the base; no new source lint finding.
- No development GitHub Actions. Docker/PostgreSQL-backed owner UAT and full source-matched Expert graph compilation remain **not run**; no full product acceptance is claimed.

System Expert partner records the Reporting → Financial Profile CID-030/CID-033 → Customer Reports/source history/export journey. A maintenance receipt preserves the intervening tested #88 source merge while retaining historical acceptance/source locks. Next functional unit: Tax source coverage in Action Centre.
