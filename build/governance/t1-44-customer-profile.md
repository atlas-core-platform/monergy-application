# T1-44 — Customer-owned profile

Status: IMPLEMENTED_FOCUSED_NATIVE_VERIFIED — source integration only; PostgreSQL owner UAT and business/production acceptance remain open. Application PR #94 extends main `fdd31a8a9ee9707d4b920fdce8161fe29fb646c0` (T1-43 / PR #93). The paired System Expert receipt records the exact final merged source and extraction identity.

## Customer behavior and authority

My Profile at `/profile` lets the current customer view their registered email and edit their display name (1–120 characters) and optional international contact number. Email remains read-only. Email verification is derived from recorded C&I CODE_VERIFIED events; the contact number is explicitly unverified for sign-in. A successful save requires an exact operation/version receipt, a fresh profile read and owner context refresh so the workspace label reflects the saved name without unmounting the page.

GET/PUT `/local/v1/me/customer-profile` derives tenant and actor from the current trusted session, validates current AM membership and resolves exactly one active owned customer. Unknown JSON fields are rejected; the caller cannot choose a customer or actor. Returned profile bindings are checked, and current membership/session and ownership are rechecked after dependencies. This is an intrinsic self-service identity operation, with no delegate/admin editing, new role, business capability or ownership creation.

## Persistence and concurrency

The additive C&I tenant migration creates `customer_profiles` and `customer_profile_receipts`. Profile version is independent of `customers.version`: contact/name updates cannot change ownership versions or invalidate Consent. Receipt records contain operation UUID, actor/customer, payload fingerprint, profile version and time; contact values are not stored in the receipt. Runtime grants allow profile insert/update and receipt insert, with no receipt update/delete/truncate or profile delete/truncate.

The save carries the customer ID verified by the initial read into the database transaction and rejects a changed ownership result before mutation. A transaction advisory lock derived from that customer serializes profile edits without granting UPDATE on the ownership table. Version validation, profile write and receipt insertion are one transaction. Same-operation/same-payload replay returns its original receipt; reuse with another payload/customer/actor conflicts. Ownership is checked again before commit and by the application after commit. A post-commit denial does not claim the committed write was rolled back. Current external AM membership is not a distributed transaction.

Both owner directory and owned-customer reads project the saved display name while retaining the original ownership version. Gateway route/direct-entry protection, existing bootstrap grants and the hash-checked additive migration overlay are wired. Migration SHA256: `59221a319148815d856ab7493548b66591fe94dccd8178efe113b5288ae83266`.

## Verification

55 focused backend checks passed: 25 new profile cases, 22 email-access regressions and 8 existing protocol/session cases. They include an actual loopback HTTP host for missing sessions, strict JSON, cross-origin rejection and successful authenticated GET/PUT. Dependency doubles explicitly test owner binding, tenant/session substitution, missing ownership, membership/ownership changes, input normalization, rejected mutations and post-commit uncertainty. They do not execute PostgreSQL adapter SQL.

21 selected customer UI checks passed: 10 profile, 10 email-entry and 1 home regression. Profile checks exercise read-only email/unverified phone, exact editable payload/operation/version, owner reload, real provider context refresh while retaining the page, conflict/uncertainty blocking another mutation until reload, foreign response rejection, late response suppression across customer/API change, input validation/discard, and anonymous direct-entry gating. The first profile run had one failing test-host URL stub; correcting it to the actual tenant-session endpoint produced the final passing run. No production defect is attributed to that test stub.

TypeScript, all changed frontend source/test lint, customer Vite production build, Gateway and Database Migrator builds passed; .NET builds report zero warnings/errors. The final C&I/profile test build passed after the transaction-lock correction. `git diff --check`, shell syntax and the migration source hash pass. No new dependencies or GitHub Actions.

The recovered runtime initially needed NuGet restoration; restore succeeded with one MSBuild node. PostgreSQL is not installed and the process environment lacks the non-root execution capability required by PostgreSQL. Actual SQL operation replay/concurrency/rollback, bootstrap/runtime grants, owner-backed UAT, PowerShell execution and live SMTP delivery remain NOT RUN. The SQL adapter was reviewed, including correction of the draft row lock that would have required ownership-table write permission. Native test doubles are not database acceptance evidence.

## Remaining requirements

MON-ID-005/012 advance only within this bounded existing-customer profile scope. Public self-registration/new customer ownership, published terms/privacy acceptance, mobile OTP, password/PIN/MFA, KYC and account closure remain open. This unit supplies no legal text, production identity-provider approval or user/business acceptance. The original 151 requirements and 81 canonical features remain the completion denominator; T1 units are engineering increments.
