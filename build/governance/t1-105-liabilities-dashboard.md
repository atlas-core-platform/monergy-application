# T1-105 — Liabilities dashboard (P1-21)

Date:2026-10-21. Completes the configured customer-reviewed liabilities dashboard using the existing T1-90 inventory and Financial Rules result contracts. Original financial record journeys P1-16 and P1-17 remain Partial; independent creditor/debt verification and full reconciliation are not claimed.

## Customer journey

Workspace Overview now shows Liabilities alongside Assets and Net Worth, guarded by existing financial-profile read and calculation capability. Loans, credit cards and other liabilities display separately, with category counts/filter, declared balance/date, source note, exact saved record version and available financial/document provenance. Document links additionally require current document permission. Update links to the existing explicit Net Worth inventory review, where completeness and overlap must be reviewed before calculation.

The dashboard reads the latest saved result by calculation time, freshly confirms the exact immutable calculation and displays its owner-supplied total liabilities. No browser financial arithmetic, currency conversion, automatic balance carry-forward, loan-closure inference or older-total substitution occurs. Filter changes affect entries only. Incomplete results display owner gap messages without a total; empty history is not zero, and zero requires a calculated explicitly complete review. A claimed calculated result without confirmed inventory completeness fails closed. A reported closed loan is not proof of repayment.

Saved history pages in batches of50; selecting a result freshly confirms its exact saved content. Each review retains its own balance date/currency and source versions. Refresh and customer/API/document-permission changes clear private values; aborted/stale responses cannot restore prior context. Foreign/missing/mismatched/denied result reads fail closed. Assets are never included as liability entries. The existing dated liability-record snapshot remains available separately.

## Verification

37 focused UI checks pass across LiabilitiesDashboard, AssetDashboard, NetWorthSummary and WorkspaceHome. Eighteen liabilities checks cover three kinds, authoritative total, filters, insufficient latest versus older complete, empty versus zero, source links, document permission, denied refresh, stale customer, history cursor/selection, overlap/date/currency/missing-value gaps, false completeness and asset exclusion. TypeScript, changed-source/test ESLint and production Vite pass. 68 unchanged NetWorthJourney owner backend checks pass, including completeness, exact decimal arithmetic, source reconciliation and owner boundaries.

No API, migration, package or persistence owner changes. Physical PostgreSQL, independent creditor verification and full live Windows/Docker UAT remain unverified. External provider inputs remain onboarding configuration. No development GitHub Actions.

## Tracker boundary

After paired merges:P1-21 Complete for configured reviewed-inventory dashboard;original29=14 Complete,15 Partial,0 Pending. T1-105=104 implementation units+1 governance closure. MON-DAS-001 remains Partial overall because income/expense and other unified summary coverage remains open. P1-16/17 debt record/lifecycle/reconciliation gaps and broad Settings requirements remain tracked. acceptedThrough T1-06 and approvedForExpertIngestion false unchanged. No user configuration is required for this implementation; live demonstration needs an owned saved inventory review in UAT.
