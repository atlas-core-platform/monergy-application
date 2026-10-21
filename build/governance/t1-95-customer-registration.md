# T1-95 — Customer Registration (P1-01)

The user approved email/OTP registration, password sign-in and email-verified password recovery on 21 October 2026. Policy provenance is recorded in t1-95-registration-policy.md. External credentials and legal publications are supplied during operator onboarding; no mail vendor or fabricated legal content is required to implement the configurable flow. Production identity infrastructure and the broader OD-10 decision remain open.

## Customer journey

The entry screen offers email/password sign-in, account creation and recovery. Registration obtains the current tenant's operator-published terms/privacy/consent, sends a purpose-bound email challenge, and requires all three explicit acceptances and a 15–128 character password. Customer & Identity atomically creates an immutable principal, owned customer, reviewed name, salted password hash, agreement evidence and verification evidence. Access Management grants only the existing Customer role. Confirmation returns no session: the customer signs in and current ownership/membership/setup checks admit the workspace. Existing imported identities cannot be claimed through registration.

Passwords use IdentityV3 PBKDF2 with 600,000 iterations and unique salts. Codes expire after five minutes, allow five guesses and are delivered from an encrypted tenant/purpose-bound outbox. Recipient requests have a one-minute cooldown and five-per-hour limit; unknown recipients receive the same generic challenge response. Five failed password attempts lock that account for fifteen minutes. Password recovery replaces the credential version, consumes the code, and revokes prior sessions. Sign-in checks the credential version after session creation and revokes a raced session. Passwords/codes are cleared from the form and never persisted in browser storage or audit payloads.

Cross-owner completion is resumable: verified password sign-in retries a pending enrollment. Enrollment is serialized and idempotent for the same active Customer membership; it never restores a disabled membership or overwrites a conflicting imported identity. Tenant-bound owner decisions authorize newly registered customers without requiring seed configuration, while known conflicting tenant mappings and legacy non-relationship routes still fail closed.

## Onboarding configuration

For a new named LOCAL onboarding profile, supply `-EnableOnboarding -EnableEmailAccess -EnableCustomerRegistration -EmailConfigurationFile <protected-email.json> -AccountAgreementFile <approved-publications.json>` to `build/uat/Invoke-MonergyUat.ps1 -Action Prepare` (or Start). The email JSON contains `SmtpHost`, `SmtpPort` (optional, default 587), `SmtpUser`, `SmtpPassword`, and `From`. Existing MONERGY_EMAIL_SMTP_HOST/PORT/USER/PASSWORD and MONERGY_EMAIL_FROM environment inputs remain supported. Authenticated STARTTLS is required. Protect input and generated profile files; neither belongs in Git or browser storage.

The agreement file uses the existing `Publications` configuration: each publication identifies TenantId and UTC PublishedAt, with exactly TERMS, PRIVACY and CONSENT documents containing Version, Title, Body and SHA256 of the exact body. The owner validates it and derives the revision. No publication means no registration challenge. Registration forces the existing workspace setup gate. These are operator onboarding inputs, not a claim that a browser credential-management screen has been delivered. Current tenant database composition remains Development LOCAL/CI only.

## Verification boundary

Registration owner tests cover explicit agreements, changed publication, purpose-bound challenges, password validation/hashing, interrupted enrollment, disabled membership and credential-recovery races. UI tests cover agreement-bound registration, recovery, sign-in clearing and failed publication handling. Tenant boundary tests cover new customers, denied access and wrong-tenant decisions. Physical registration tests are provided for single-use codes, committed failed attempts, cross-tenant rejection and recovery/session invalidation. Physical PostgreSQL, migration/grant/restart execution, real SMTP delivery, Docker composition and full live UAT remain NOT RUN. Business acceptance remains unchanged.

Delivery status is finalized only with canonical Access Management, Application and System Expert merges and the corresponding tracker update.

Fresh verification: 322 nonphysical Access Integration checks (including 17 registration and four new-customer boundary cases), 1,486 Financial Profile checks and 20 focused UI checks pass. TypeScript and changed-file ESLint pass. AM and Gateway builds pass. Access Management PR15 merged 21c0e933719034554be7c61d8e74f7ec433fc840; Application pins its verified tree. Counts after paired delivery: 6 complete,19 partial,4 pending;95 numbered units =94 implementations +1 governance closure.
