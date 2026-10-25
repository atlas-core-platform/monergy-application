# ID-01 — Existing-persona self-activation in LOCAL UAT

**Status:** Implementation candidate. This is a protected tester-enabled, Development-only entry using the existing Customer & Identity (C&I) password/hash/challenge/session owners and Access Management. It is **not** production IdP/MFA acceptance, production invitation delivery, or blanket persona-workspace completion.

## What changes for a LOCAL tester

In a **new named onboarding profile** explicitly enabled for persona credentials, `/access`, `/advisor` and the supported Customer `/` route offer **Email / Password** as the primary sign-in method. A tenant administrator creates or invites the person and assigns their tenant and role; they never set the person's password and never sign in as them. A protected tester action opens a five-minute, single-use welcome page for that already-provisioned identity. The invited person chooses their own password and then signs in normally. Activation **does not** assign a role or automatically enroll a person. AM still validates current role/membership before protected routes open.

Current developer reference keys remain a clearly secondary **LOCAL test key** mechanism. In profiles that are not opted in, the existing behavior stays unchanged.

This candidate reuses the existing challenge store and password policy but stores separately bounded `persona_credentials` for **existing** tenant principals. It never allows activation to create an actor or claim a self-registered Customer's `registered_credentials`. Existing self-registration uses its own documented password route, terms/privacy/consent and enrollment. All credentials use the existing Identity V3 PBKDF2 password policy with unique salts, 600,000 iterations and 15–128 character password enforcement.

## LOCAL activation and sign-in

1. The tenant administrator creates/invites the identity and assigns only tenant, membership and role.
2. On a protected tester workstation, the tester runs `-Action Welcome` for that existing reference identity. The command authenticates to a tester-only endpoint with a protected profile secret and opens the welcome page without printing its ticket.
3. The browser removes the ticket fragment before rendering. The invited user chooses a strong password. The five-minute ticket is purpose-bound and single-use; confirmation creates a password record **only** for the existing principal and **does not issue a session or grant a role**.
4. The user follows *Continue to sign in* and enters organization, email and password. C&I checks credential version, password lockout and AM's current active membership/subject version before and after session issuance. Route-specific checks remain at `/access`, `/advisor` and `/`.
5. LOCAL tester mode deliberately has no email-code activation or recovery buttons and no external email dependency. A tester can issue a replacement welcome link when a ticket expires. Local keys remain an explicitly separate test channel, not a production credential.

Production email activation and recovery are unchanged: when `CustomerEmailAccess` and authenticated SMTP are configured and tester activation is absent, the existing purpose-bound email-code request/confirm routes, delivery outbox and recovery/session-revocation behavior remain in force.

## Safe LOCAL configuration

Existing `t105` and the isolated durable profile `4191` must not be reconfigured or overwritten. Choose a new unused port; `4193` is only an example.

```powershell
.\build\uat\Invoke-MonergyUat.ps1 -Action Prepare -Profile persona-login -Port 4193 `
  -EnableOnboarding -EnablePersonaLogin -EnablePersonaTesterActivation
.\build\uat\Invoke-MonergyUat.ps1 -Action Start -Profile persona-login `
  -EnableOnboarding -EnablePersonaLogin -EnablePersonaTesterActivation
.\build\uat\Invoke-MonergyUat.ps1 -Action Welcome -Profile persona-login `
  -Tenant T001 -Actor A300
```

### Protected no-SMTP LOCAL activation

No SMTP host, mailbox or other external email service is used in this LOCAL flow. `-Action Welcome` reads its access secret from the ACL-protected profile, accepts only A900/A100/A200/A300 and T001/T002, and never prints the returned activation credential. Do not paste activation links or generated profile JSON into chat, terminals, logs or GitHub.

Default LOCAL reference principals use emails like `a100@example.test`, `a200@example.test`, `a300@example.test`, `a900@example.test`, all tenant-specific (T001/T002) and **not routable public inboxes**. The tester action maps these fixed actors to their existing C&I email; it does not allow a browser or tenant administrator to select an actor. New imported people need a separately governed invitation mechanism; their AM membership must still be active before sign-in.

Existing Customer self-registration and legal text are separately enabled through `-EnableCustomerRegistration -AccountAgreementFile` and still require approved publications. This persona activation mode is for **already provisioned** identities, not a replacement for required registration/legal consent.

These opt-in switches must also be supplied on later Start/Prepare invocations; do not silently enable persona authentication for an existing profile.

## Focused acceptance

- Backend C&I migrations `0013_persona_credentials.sql`, grants and startup/hosted outbox handling must pass in an isolated PostgreSQL test tenant.
- Protected tester secret, exact pre-provisioned email, allowed return route and Development environment are required to create a welcome link; production cannot enable this owner.
- Ticket single use, five-minute expiry, max five attempts, request cooldown, rate limits and wrong-tenant/purpose replay deny. The ticket is supplied in a fragment and cleared before the welcome page renders.
- Password correctness, minimum length, lockout after five failed attempts, credential recovery revoking prior sessions and session-issue/recovery race all fail closed.
- A900 can log in at `/access`, A100/A200 at `/`, A300 at `/advisor` only if their existing AM roles/memberships permit. No user enters `/access` simply by choosing that URL. Customer assignment/Consent remains independent.
- A user without a business workspace may authenticate yet must receive an appropriate **no authorized workspace** state, not Customer-owner impersonation or Tenant Administrator elevation.
- Browser must not store raw passwords, session tokens or activation tickets persistently; logout, disabled membership, wrong-tenant, stale role, expired session and cancellation still deny.
- Run C&I and AM physical tests, `PersonaCredentialTests`, `PersonaCredentialEntry.test.tsx`, CustomerLoginJourney/AccessExperience, TypeScript, full ESLint and real Docker browser acceptance. Use isolated Git worktrees; do not enable development GitHub Actions.

## Deferred scope after this increment

A true production **administrator-sent invitation** (ID-02/03), automated organization discovery, role-aware landing-page selection after a global sign-in, Platform Administrator identity/workspace (ID-04), complete MFA, device/login history and production session security (ID-05) remain distinct work. Do not describe this LOCAL tester activation as production invitation delivery or production authentication.

**Merge gate:** NO-GO until focused source tests, tenant C&I migration/PG permissions and role-specific Docker browser acceptance pass without SMTP. Do not reset the existing t105/4191 profiles.
