# T1-100 — Customer Login (P1-02)

Date: 2026-10-21. Completes the Phase 1 customer Login journey under the already approved T1-95 email/password policy. No new authentication policy, vendor or legal text is selected.

## Customer journey and change

Configured customer entry now opens email/password sign-in by default. It waits for method discovery instead of briefly displaying a local test-key form. The customer may explicitly choose another method, including existing email-code sign-in and a clearly labelled local test key; a late discovery response cannot override that choice. Administrator entry retains its separate authority and local test flow.

Password rejection now explains checking organization/email/password or choosing Forgot password. It no longer incorrectly describes an invalid OTP. Messages remain non-enumerating and conceal owner details. Credential fields are cleared before submission, with no browser persistence. The user can cancel while password issuance or workspace verification is pending; the existing session owner revokes any late issued session and never publishes stale private context. Recovery announces success only after an explicit positive owner receipt.

Existing T1-95 owner behavior remains authoritative: purpose/tenant-bound email verification and recovery; salted password hashes; lockout/rate limits; resumable pending Customer enrollment without restoring disabled membership; current credential version checked after session issuance; old sessions revoked on recovery. Existing current tenant/customer ownership, membership and setup checks precede access. Expiry, loss of access, reconnect, sign-out and own-session inspection/selected termination retain their owners. No automatic administrator role, browser-stored token, new endpoint, package or migration.

## Verification

322 nonphysical Access Integration tests pass, including password hashing/lockout path, agreement and recovery policy, enrollment and recovery/session races, tenant boundaries and own-session controls. 54 focused UI tests pass across customer login, registration/recovery, email access, administrator access, own sessions and onboarding. New integrated UI tests exercise default password selection, 401/429/503 failures, 403/503 workspace denial and issued-session revocation, cancellation with a late response, explicit method choice, foreign-tenant session rejection, expiry and sign-out. Malformed recovery receipts cannot announce a successful reset. TypeScript, changed-source/test ESLint and Vite production build pass.

These are owner unit/integration and browser-component checks. Physical PostgreSQL, migration/grant/restart execution, real SMTP delivery, Docker/PowerShell and full authenticated live browser UAT were NOT RUN. Existing backend sources are reused, not rewritten; unrelated suites are not claimed as fresh passes. No development GitHub Actions.

## Completion boundary and configuration

P1-02 is Complete for the approved Phase 1 Login flow, joining Registration. Operator onboarding supplies SMTP credentials, registration enablement and approved publications through the existing T1-95 configuration. Missing external setup is not fabricated and does not change the policy. Production IdP/MFA, device management, full login-history/security Settings, account closure and broader OD-10 remain separate incomplete scope. MON-ID-006 remains Partial; business acceptance and live verification do not advance.

After paired merges and tracker reconciliation: original 29 deliverables = 9 Complete, 19 Partial, 1 Pending (20 incomplete). T1-100 = 99 implementation units plus one governance closure. P1-04 Basic KYC remains Pending for its approved route. acceptedThrough T1-06 and approvedForExpertIngestion false remain unchanged.
