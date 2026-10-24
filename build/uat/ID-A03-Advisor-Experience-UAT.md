# ID-A03 — Financial Advisor named customers and reviewed-source UAT

**Status:** Implementation candidate, physical UAT pending. Use a disposable/approved LOCAL onboarding profile. Never reset the retained `default` or `t105` volumes and never inject fabricated financial rows directly into PostgreSQL.

## Intended experience

An active Advisor sees **C&I-verified customer names** (not numbered labels) only when current AM capability, named relationship and live Consent decisions authorize both `financial-profile.profile.read` and `evidence.document.read`. All labels and owner versions come from Customer & Identity's internal LOCAL credential-gated port, never the browser or AM's assignment table.

The dedicated `GET /product-api/v1/advisor/customers` endpoint re-evaluates Advisor authorization, relationship/Consent-filtered eligible IDs, canonical owner versions and C&I labels before returning a bounded named list. No email, KYC, account number or unassigned customer name is returned. The separate delegated profile read retains all ID-A02 checks.

The Advisor UI uses readable customer names, type/category labels, locale-formatted amount, effective date, and exact Evidence source name for reviewed facts. All review operations are read only. The deprecated Ant Design `List` widget is replaced with semantic list markup. This is not approval for mutation, editing, arbitrary cross-customer navigation or a global customer directory.

## Focused tests

- `AdvisorCustomerLabelTests`: C&I owner port token/tenant/customer identity, valid label/version, mismatch and invalid-record denial
- `AdvisorWorkspace.test.tsx`: recognized customer name, no raw customer ID, reviewed income/source rendering, denied/deleted Consent response without facts, invalid labels fail closed, non-Advisor entry denied
- Customer Web TypeScript, production build and focused lint
- Customer Identity/Gateway build and focused integration tests

## LOCAL physical/browser gates

1. Use an **isolated worktree** for the Application PR and preserve all local worktrees, modified `.gitignore`, `build/demo/`, retained `t105` Docker volumes and credentials. Do not enable GitHub Actions.
2. Rebuild LOCAL Docker non-destructively and check readiness **HTTP 200 / 13 service boundaries** plus ClamAV active/healthy.
3. Establish `A300` in the correct Financial Advisor role, *explicitly* assign the existing resource-scoped Financial Profile and Evidence read permissions through reviewed Organization Administration, assign customer `C001`, and obtain **genuine owner A100 Consent** for both capabilities. The default pack is unchanged.
4. Confirm `/advisor` shows the C&I-owned display name and never `Assigned customer 1`, email or raw Customer ID. Confirm `/product-api/v1/advisor/customers` returns only currently authorized `customerId/displayName` pairs plus the validated session/policy version metadata. Count and named rows should match current AM decisions.
5. Create **one non-empty reviewed Financial Profile fact via the actual customer journey**, not fixture SQL: sign in as A100 at `/`, upload an explicitly supported document through Documents, process/OCR it, review the extracted candidate, and explicitly add it to Financial Information. A known *synthetic* single-page salary-slip fixture exists at `tests/vs02/Monergy.Vs02.Tests/Fixtures/salary-slip-scan.pdf`; the supported OCR rules and requirements are in `build/uat/Salary-Slip-OCR.md`. Confirm native OCR/ClamAV are healthy; record a genuine reviewed `INCOME` fact, its date, amount and source. If unsupported or OCR uncertain, **BLOCK**, do not manually insert/fabricate a financial fact.
6. With live two-capability Consent still valid, open the Advisor's named Customer and verify the same reviewed fact, correct category/date/amount and exact Evidence source name appear read-only. Advisor has no button to edit/promote facts and no unrelated customer data.
7. Negative tests: A300 without the role, without the AM capability, without a relationship, without either Consent grant, after Consent expiry/revocation, disabled/stale session, foreign tenant, unknown customer and owner/label mismatch. Denied responses must not leak display names, financial facts or source names. C&I customer display port without workload token must return 403. Invalid/unknown SPA/API routes must remain denied.
8. Reconcile/revert any **approved** test-specific role/relationship/Consent changes through supported APIs (do not erase immutable audit records). For the persisted test financial fact, use a disposable profile or obtain operator approval for retained fixture data; do not delete customer business history to fake cleanup.
9. Report precise `PASS/FAIL/BLOCKED` counts for focused tests, real browser, final Docker health, and whether a **non-empty** source-backed read was proven.

## Boundaries

Successful HTTP 200 with an empty financial profile is *not* positive ID-A03 non-empty-source acceptance. React mocks alone are not physical Evidence-to-Profile proof. The current C&I customer label is the source of truth; do not replace it with an assigned-label table or browser-entered name. ID-A03 source/UX acceptance remains separate from broader Advisor business journeys and Production identity/security gates.
