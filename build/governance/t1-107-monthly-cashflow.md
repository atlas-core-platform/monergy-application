# T1-107 — Monthly Income and Expenses dashboards (P1-19/P1-20)

Date: 2026-10-21. Completes the supported customer-reviewed monthly dashboard journeys. Financial Profile owns reviewed inputs; Financial Rules alone calculates totals and month changes. Customer-declared household coverage is explicit, not external verification.

## Behavior and ownership

Workspace > Income and Expenses shows the latest saved monthly review, exact income/expense/balance totals or coverage gaps, month, named household and currency. Missing review is not zero. An incomplete newest review never falls back to an older complete result. Exact result re-read confirms the displayed history item. The review route supports manual income and expense categories, household-member labels, source notes and exact saved record revisions covering the entire selected completed month. No proration, FX, annualisation or carry-forward is inferred. Partial import lists are rejected, not silently imported. Imported source values and revisions are verified by Financial Profile.

Both income and expense inventories require explicit completeness confirmation. Any change resets it. Missing values/dates, mixed dates/currencies, repeated source facts and duplicate member/category/name lines produce insufficient-data results without totals. An explicitly confirmed empty side can be zero. Exact decimal cents and signed balance/change values remain strings through the browser. This does not automatically reconcile a customer's accounts or establish verified income/spending.

Reviews and calculations are immutable, tenant/customer/actor/operation-scoped and replay-safe. Update creates a replacement-linked review without changing history. Financial Rules revalidates the exact position before returning results. Same-household/same-currency complete months compare in chronological order, including unequal calendar-month lengths. Saved history is paged at50; comparison choices survive paging. The server re-reads both exact calculations and enforces current permissions. Exact document version, evidence, provenance and financial record revision remain visible in source details.

Customer-owned Gateway routes preserve existing profile read/update, calculation and document permissions, with before/after session, owner and access checks. Existing sensitive product-ingress Audit middleware applies. Financial Profile migration0024 and Financial Rules migration0004 add append-only tables with runtime SELECT/INSERT and UPDATE/DELETE revoked. LOCAL/UAT configuration enables the same Financial Rules integration. No package or capability is added.

## Verification

442 focused backend checks pass across monthly cashflow, Net Worth and reviewed Income/Expense owners. 84 UI checks pass, including review/import/retry/history, exact overview reread, incomplete coverage, cross-page comparisons and existing Net Worth regression. TypeScript, changed source/test ESLint and Vite production build pass. Physical persistence tests are added and the persistence test project builds with zero warnings/errors; a live PostgreSQL instance was not exercised. Full Windows/Docker/SMTP/provider UAT was not run. No development GitHub Actions.

## Tracker

P1-19/P1-20 Complete; P1-13/P1-14 remain Partial for broader record/provider/reconciliation lifecycle. After paired merges:16 Complete,13 Partial,0 Pending;T1-107=106 implementation units+1 governance closure. All original IDs/phases and acceptance through T1-06 remain unchanged. approvedForExpertIngestion remains false. No user product decision is needed for this increment.
