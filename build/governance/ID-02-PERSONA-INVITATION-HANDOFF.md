# ID-02 — Administrator handoff to invited-person self-activation

## Scope and owner

This is the first bounded ID-02 implementation unit following accepted ID-01 Persona Login LOCAL UAT. The Organization Administrator provisions the existing C&I person via Access Management and assigns only the requested business role/membership. The administrator **never** chooses, sees or enters the person's password, opens their session, or claims mailbox ownership.

Current ID-01 provides a LOCAL tester-protected, purpose-bound, single-use five-minute activation welcome ticket and a distinct user-facing password welcome page. This should ultimately be initiated by the **invited person** after their own login/account identification. The existing tester-only secret is **not** an authorization token to be shared with the person or embedded in an unauthenticated page.

## Unit ID-02A (this PR)

After AM returns an `Activated` People onboarding receipt, `ImportDrawer` explicitly distinguishes:
- **Organization membership and assigned role: active**
- **Credentials/password and signed-in session: not created**
- **Next actor:** the invited person self-activates using protected LOCAL tester welcome in DEV/UAT, or verified emailed invitation in higher environments.

No admin-side activation-link, token, simulated email claim or elevated tester secret is placed in the receipt. An integration regression checks the handoff. This unit does not add a new delivery channel.

## Follow-on ID-02B implementation gate

Design and implement a user-driven, **appropriately authenticated LOCAL tester context** or controlled preview inbox so the user can request/open their own welcome link. Restrict discovery to an existing provisioned principal and fail closed for unverified identities, unauthorized testers, wrong-tenant replay, unavailable membership, expired or consumed ticket. Do not disclose activation tokens merely by entering an arbitrary email. A tester's access to simulate identity ownership must be explicit, auditable, isolated to LOCAL/UAT and impossible in Production. Maintain the same C&I activation and credential/session owner paths as real verified email invitation.

The current ID-01 tester secret must never be exposed through a browser accessible without server-side authorization. A safe LOCAL simulated-email process may require an operator-controlled credential or mailbox identity distinct from tenant admin authority; do not confuse those roles.

## Further ID-02C

Connect the Access Management onboarding receipt to a durable invitation state with verified delivery/retry/cancel/resend semantics, no duplicate active grants, consistent actor ID and tenant. Keep the distinction between activated *membership* and activated *credential* visible in UI and audit. Provide administrator non-sensitive invitation status, but never activation bearer secrets or passwords.

## Acceptance before merge

Run `ImportDrawer.test.tsx` and affected People/AccessExperience suites, full Customer Web ESLint, TypeScript and build. Test successful, failed and cancelled imports, including multi-user ones. Verify the change does not modify C&I principal/credential/session records, Access Management roles, or current customer consent. The test must not alter preserved 4173/4185/4191/4197/4198 Docker profiles, databases, volumes or keys. No development GitHub Actions.

**Status:** ID-02A candidate only; no local tests executed on this exact PR. Do not merge before evidence. ID-02B/C are not implemented.
