# ID-02C-04C — Invitation delivery attention

This bounded unit makes the existing recipient-delivery queue visible to the Tenant Administrator who initiated a credential invitation. It does not expose SMTP diagnostics, retry counts, verification codes, challenge identifiers, passwords or signed-in sessions. It does not change organization membership or business access.

## Owner-derived status

C&I projects the current `persona-activation` challenge for each tenant-owned principal. Before the configured sender confirms delivery, the administrator sees `Delivery pending`. If the bounded three-attempt limit is exhausted, or the five-minute challenge expires before confirmed delivery, the administrator sees `Delivery failed`. A successful sender confirmation remains the only transition that appends `ISSUED`; the established `Pending`, `Completed`, `Cancelled` and `Expired` invitation states retain their existing meanings.

The status projection returns only actor ID, tenant-owned normalized email, safe state and the existing issued/consumed timestamps. Challenge hashes, protected codes, internal delivery attempt count and provider errors are never returned. Pagination remains actor-bound and tenant-isolated.

## Administrator recovery

The People onboarding receipt displays a warning when one or more invitation deliveries need attention. It explains that recipient delivery was not confirmed and organization access is unchanged. The administrator can review the address with the person and use the existing authenticated resend action. The mutation remains non-enumerating and reports only that delivery was requested; a later owner refresh is required to learn the safe outcome.

## Verification boundary

Required source verification covers the C&I status projection and compilation, the failed-delivery warning and resend UI, TypeScript, changed-source lint and Customer Web build. Existing invitation send, resend, cancellation, disabled-delivery and secret-exclusion cases must remain green.

Physical PostgreSQL plus a controlled SMTP sink remains an operator acceptance gate. It must demonstrate queued, retrying, exhausted, successful and expired delivery outcomes without an `ISSUED` event before confirmed delivery. Production SMTP/provider acceptance and operations, proactive out-of-band administrator notifications, and invitation-history retention policy remain separate work.

**Status:** Application source verification passed; exact Application merge and paired System Expert source lock remain required.
