# ID-02C-04B — Tenant-admin invitation send and resend

This bounded unit connects an activated People onboarding receipt to the existing C&I-owned, authenticated-email persona invitation lifecycle.

It does **not** let an administrator choose or view a password, verification code, challenge identifier, activation link or signed-in session. It does not automatically send mail merely because membership was activated, add a new SMTP provider, weaken the recipient rate limits, or claim Production identity readiness.

## Administrator journey

After People onboarding reaches `Activated`, the administrator can refresh Credential invitation status for the exact actors in that receipt.

- A person with no delivery-backed issuance is shown as `Not sent`.
- An expired or cancelled invitation can be resent.
- A pending invitation can be cancelled, not duplicated.
- A completed invitation has no administrator mutation.
- Profiles without authenticated email delivery expose status but no send action.

Send/resend requires the current Tenant Administrator session twice immediately before the request. The browser supplies only the canonical actor ID from the onboarding receipt. C&I resolves the tenant-owned normalized email and silently accepts ineligible or unknown actors without disclosing whether credentials exist.

The response contains only actor ID and `Accepted`. Recipient email, verification code, challenge, protected payload and password never appear in the mutation response. The UI says delivery was requested—not delivered—and requires a later owner status refresh.

## Existing lifecycle reused

C&I queues the same five-minute `persona-activation` challenge used by the recipient-owned email flow. Existing owner controls remain authoritative:

- one-minute resend cooldown and five accepted requests per hour;
- protected code stored only until successful delivery, terminal failure or expiry;
- `ISSUED` appended only after the configured sender confirms recipient delivery;
- an accepted replacement terminally closes the prior delivered invitation;
- activation remains recipient-owned and sign-in remains a separate operation;
- the immutable invitation ledger retains `ISSUED`, `CANCELLED`, `EXPIRED` and `CONSUMED` semantics.

The UAT Gateway allowlist accepts only exact bounded actor paths ending in `/send` or `/cancel`; arbitrary invitation subpaths remain blocked.

## Verification boundary

Required source verification:

- Customer & Identity tests for administrator authority, owner-resolved recipient selection, generic ineligible response and secret-free receipt;
- Customer Web invitation send/resend, disabled-delivery and existing cancellation regressions;
- TypeScript, changed-source lint and Customer Web build;
- Customer & Identity and UAT Gateway Release builds;
- existing persona credential logical and physical regression compilation.

Physical PostgreSQL plus controlled SMTP-sink UAT remains an operator acceptance gate. It must prove the accepted administrator request produces no `ISSUED` before successful delivery, failed delivery produces none, successful delivery produces exactly one, resend terminally closes the prior delivery, and activation records `CONSUMED` without exposing secrets.

Explicit delivery-failure notification UX, Production SMTP/provider operations and invitation-history retention policy remain separate increments.

**Status:** Application source verification passed; exact Application merge and paired System Expert source lock remain required.
