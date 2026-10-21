# T1-103 — Basic KYC (P1-04)

Date: 2026-10-21. Completes the configured manual evidence-review implementation of MON-ID-005 and the KYC-status portion of MON-FIN-002. Business acceptance and regulated verification remain separate.

## Journey and ownership

Customer & Identity owns append-only KYC events and optimistic case versions. Customers select up to three exact scanned PDF/PNG/JPEG Evidence versions (identity required), choose an operator-configured reviewer, accept the exact published notice and confirm submission. Customers see current state and paged history, withdraw pending cases, and resubmit after changes, rejection or withdrawal. Assigned reviewers see pending cases, read original evidence under current independent authorization, and confirm a reasoned manual acceptance, changes request or rejection. MANUAL_REVIEW_ACCEPTED never means external identity verified.

Tenant/session authority, current active customer ownership, configured reviewer assignment, exact notice revision, CAS and operation identity are owner-enforced. Reviewer assignment and administrator role do not grant document access. Existing Advisor document authorization and current customer-advice Consent are independently required; no Access Management grant is minted or widened. Evidence checks exact tenant/customer/version, scan evidence, MIME, size and content digest, and rechecks authorization after reading. Self-review, stale/foreign sessions, removed reviewers and changed notices fail closed. Immutable operation replay rejects a changed payload. Runtime KYC events permit INSERT, not UPDATE/DELETE/TRUNCATE.

UI privacy boundaries clear private data on failed refresh/context changes and ignore stale responses. Unknown mutation results retain the exact operation for retry. Case history retains the accepted notice and exact evidence references. Gateway routes and onboarding configuration support customer /kyc and reviewer /kyc/reviews. See build/uat/BASIC-KYC.md for required operator inputs and independent evidence grants.

## Verification and limits

111 focused backend checks pass:97 BasicKyc/SelfCustomerProfile/CustomerPan and14 DocumentVault/Evidence checks. 28 UI checks pass. TypeScript, changed-source/test ESLint, production Vite, C&I/Evidence/Gateway/migrator builds, actual Gateway startup in both product modes and mocked legacy UAT profile/configuration checks pass. HTTP tests exercise submit/withdraw/history, assigned review, replay/conflict, authorization and exact evidence bytes. Persistence tests use explicit doubles; PostgreSQL migration/grants/locking/restart were not physically exercised.

Live Docker/Windows UAT, external providers and physical production evidence storage are unverified. Current original-byte serving uses the existing reference Evidence store and fails closed if unavailable. Configuration supplies operator notice and named reviewers, never invented approved legal text or provider credentials. Regulated Aadhaar/bank/government verification under MON-SRC-011 remains a separate configured integration and is not claimed complete. No development GitHub Actions were used.

## Tracker boundary

After paired merges: P1-04 Complete for configured manual review; original29 =12 Complete,17 Partial,0 Pending. Seventeen journeys remain unfinished. T1-103 =102 implementation units plus one governance closure. acceptedThrough T1-06 and approvedForExpertIngestion false remain unchanged.
