# ID-01 — Existing-persona email/password activation in LOCAL UAT

**Status:** Implementation candidate. This is an operator-enabled, Development-only entry using the existing Customer & Identity (C&I) password/hash/email/challenge/session owners and Access Management. It is **not** production IdP/MFA acceptance, full staff invitation delivery, or blanket persona-workspace completion.

## What changes for a LOCAL tester

In a **new named onboarding profile** explicitly enabled for persona credentials, `/access`, `/advisor` and the supported Customer `/` route offer **Email / Password** as the primary sign-in method. A person with a pre-provisioned C&I tenant principal and an active AM membership may verify control of their already-recorded email, establish a strong password and sign in. Staff identity activation **does not** assign a role or automatically enroll a person. AM still validates current role/membership before protected routes open.

Current developer reference keys remain a clearly secondary **LOCAL test key** mechanism. In profiles that are not opted in, the existing behavior stays unchanged.

This candidate reuses the existing customer registration/email-code infrastructure but stores separately bounded `persona_credentials` for **existing** tenant principals. It never allows activation to create an actor or claim a self-registered Customer's `registered_credentials`. Existing self-registration uses its own documented password route, terms/privacy/consent and enrollment. All credentials use the existing Identity V3 PBKDF2 password policy with unique salts, 600,000 iterations and 15–128 character password enforcement.

## Activation, sign-in, recovery

1. Enter organization and the **email already provisioned by C&I**, select *Activate account*. C&I queues a purpose-bound, five-minute six-digit challenge. Responses are generic for nonexistent, disabled/unknown, already activated and rate-limited recipients. SMTP delivery stays private and asynchronous.
2. Read the code from your **operator-protected LOCAL mail receiver**, choose a strong password, and confirm. Confirmation creates a password record **only** for the existing principal; **it does not issue a session or grant a role**.
3. Sign in using organization, email and password. C&I checks the credential version, password lockout and AM's current active membership/subject version before and after session issuance. Role-specific route checks remain at the existing `/access` Organization Administrator, `/advisor` Financial Advisor or `/` Customer owner boundary.
4. Forgot Password sends a separate purpose-bound email code. Successful recovery rotates the credential version and revokes old trusted sessions. Local keys remain an explicitly separate recovery/test channel during transition, not a production credential.
5. Email discovery and request/confirm endpoints are available **only** when persona mode and already authenticated SMTP email access are selected. No actor ID, role, password or magic key is ever accepted from browser code for authority decisions.

## Safe LOCAL configuration

Existing `t105` and the isolated durable profile `4191` must not be reconfigured or overwritten. Choose a new unused port; `4193` is only an example.

```powershell
.\build\uat\Invoke-MonergyUat.ps1 -Action Prepare -Profile persona-login -Port 4193 `
  -EnableOnboarding -EnableEmailAccess -EnablePersonaLogin `
  -EmailConfigurationFile E:\Private\monergy-persona-smtp.json
.\build\uat\Invoke-MonergyUat.ps1 -Action Start -Profile persona-login `
  -EnableOnboarding -EnableEmailAccess -EnablePersonaLogin `
  -EmailConfigurationFile E:\Private\monergy-persona-smtp.json
```

### Approved Gmail SMTP for local testing

The operator selected Gmail SMTP for Development-only persona validation. Generate a **new Google App Password** after enabling Google 2-Step Verification, if the account supports App Passwords. Do not use the normal Gmail password, hardcode credentials in Git, or disable TLS/certificate validation. Store the private configuration at `E:\\Private\\monergy-persona-smtp.json` (the exact local path can differ):

```json
{
  "SmtpHost": "smtp.gmail.com",
  "SmtpPort": 587,
  "SmtpUser": "YOUR_ACCOUNT@gmail.com",
  "SmtpPassword": "YOUR_PRIVATE_GOOGLE_APP_PASSWORD",
  "From": "YOUR_ACCOUNT@gmail.com",
  "LocalPersonaTestInbox": "YOUR_ACCOUNT@gmail.com"
}
```

The values above are placeholders, NOT credentials. Secure the file to the current Windows user/authorized Docker operator before starting the profile. Use a real Gmail address owned by the operator for all three Gmail fields. Gmail SMTP uses authenticated TLS/STARTTLS; Gmail may refuse App Passwords for certain Workspace/Advanced Protection settings.

LOCAL persona reference emails `a100@example.test`, `a200@example.test`, `a300@example.test`, `a900@example.test` are intentionally non-routable. **With explicit `-EnablePersonaLogin` and `LocalPersonaTestInbox`, only those fixed local reference addresses are delivered to the Gmail inbox.** The challenge remains bound to the original C&I email and tenant, and its purpose and single-use checks remain unchanged. The email body identifies the LOCAL reference recipient. Other imported/registered people receive mail at their actual C&I email and are not redirected.

This is a consciously shared development test mailbox, **not genuine separate-person email ownership verification** and never authorized outside `Monergy:ExecutionZone=LOCAL`. Do not use it to assert production identity verification or real-person invitation acceptance. Turn off the override for real multi-person tests. Neither Gmail App Password nor OTP should appear in chat, PRs, source logs, screenshots or example output. If an App Password is unavailable, use a different approved authenticated SMTP provider rather than weakening TLS.



Default LOCAL reference principals use emails like `a100@example.test`, `a200@example.test`, `a300@example.test`, `a900@example.test`, all tenant-specific (T001/T002) and **not routable public inboxes**. The mail receiver must capture these addresses securely. New imported people may use separately verified emails, but their AM membership must be active before sign-in.

Existing Customer self-registration and legal text are separately enabled through `-EnableCustomerRegistration -AccountAgreementFile` and still require approved publications. This persona activation mode is for **already provisioned** identities, not a replacement for required registration/legal consent.

These opt-in switches must also be supplied on later Start/Prepare invocations; do not silently enable persona authentication for an existing profile.

## Focused acceptance

- Backend C&I migrations `0013_persona_credentials.sql`, grants and startup/hosted outbox handling must pass in an isolated PostgreSQL test tenant.
- Correct principal receives a purpose-bound activation code through actual SMTP; unknown, unprovisioned or already activated email receives the same generic HTTP envelope but **no deliverable activation**.
- Single use, expiry, incorrect code (max five attempts), request cooldown, rate limits and wrong-tenant/purpose replay deny.
- Password correctness, minimum length, lockout after five failed attempts, credential recovery revoking prior sessions and session-issue/recovery race all fail closed.
- A900 can log in at `/access`, A100/A200 at `/`, A300 at `/advisor` only if their existing AM roles/memberships permit. No user enters `/access` simply by choosing that URL. Customer assignment/Consent remains independent.
- A user without a business workspace may authenticate yet must receive an appropriate **no authorized workspace** state, not Customer-owner impersonation or Tenant Administrator elevation.
- Browser must not store raw passwords, session tokens or emailed codes persistently; logout, disabled membership, wrong-tenant, stale role, expired session and cancellation still deny.
- Run C&I and AM physical tests, `PersonaCredentialTests`, `PersonaCredentialEntry.test.tsx`, CustomerLoginJourney/AccessExperience, TypeScript, full ESLint and real Docker browser acceptance. Use isolated Git worktrees; do not enable development GitHub Actions.

## Deferred scope after this increment

A true **administrator-sent invitation** (ID-02/03), automated organization discovery, role-aware landing-page selection after a global sign-in, Platform Administrator identity/workspace (ID-04), complete MFA, device/login history and production session security (ID-05) remain distinct work. Do not describe this LOCAL activation as completed invitations or production authentication.

**Merge gate:** NO-GO until focused source tests, real SMTP delivery, tenant C&I migration/PG permissions and role-specific Docker browser acceptance pass. Do not reset the existing t105/4191 profiles.
