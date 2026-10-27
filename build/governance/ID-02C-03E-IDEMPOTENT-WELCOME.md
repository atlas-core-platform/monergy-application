# ID-02C-03E — Idempotent protected welcome retry (candidate)

This bounded unit closes the retry/idempotency gap in the existing Development-only invited-person welcome flow. It does **not** implement production email delivery, a Tenant Administrator resend workflow, notification infrastructure or any Access Management/Consent mutation.

## Request identity without bearer persistence

The protected tester welcome request now requires a client-generated lowercase 32-hex `requestId`. C&I validates it and combines it with the normalized tenant/recipient identity using a dedicated domain-separated HMAC key derived from the existing owner-side challenge secret.

The resulting 64-hex welcome ticket is deterministic for that tenant, recipient and request ID but cannot be derived from the request ID alone. PostgreSQL continues to store only the ticket hash/verifier and immutable invitation events; the raw ticket/reply is never persisted.

This means a browser/network retry can reproduce the same welcome ticket without server-side bearer caching.

## PostgreSQL idempotency contract

`QueueTesterAsync` returns the stored expiry when the exact derived challenge hash is still the current live issued invitation.

Such a retry:
- does not increment the one-minute/five-per-hour renewal counters;
- does not append another `ISSUED` event;
- does not cancel or expire the active invitation;
- returns the original stored expiry, not a new five-minute window.

Concurrent duplicate requests are also reconciled: if two requests both miss the initial read, one wins the insert and the other re-reads the committed exact hash after the conflict wait.

Once the deterministic hash has any invitation history but is no longer the live invitation, it cannot be resurrected. A consumed, cancelled, expired or superseded ticket requires a **new** request ID for an intentional new issuance.

A different request ID remains a real renewal and is still subject to the existing one-minute cooldown and five-per-hour limit.

## Browser behavior

Customer Web retains one request ID for the same tenant/email/return destination across an uncertain failed request. Re-entering the protected tester authorization retries with that same ID.

After a successful receipt, **Request another link** explicitly clears the prior request identity so the next request is a true renewal and is governed by the existing renewal limits.

The request ID is not a password, tester authorization or activation bearer. The protected tester authorization remains header-only and is cleared from the form after submission.

## Regression coverage

- Logical C&I coverage requires identical welcome path and expiry for the same request ID.
- Physical PostgreSQL coverage races concurrent duplicate requests, verifies one `ISSUED`, `requests=1`, stable expiry, normal cooldown rejection for a different hash, and rejection of same-hash resurrection after cancellation.
- React coverage proves an uncertain transport retry reuses the request ID and an intentional new-link action generates a new one.
- Persona browser UAT replays each persona's exact welcome request and requires an identical receipt before activation.
- Invalid request IDs fail closed with HTTP 400.

## Still outstanding

General Tenant Administrator resend UX/API, production recipient-verified email delivery, notifications and retention remain separate increments. This unit does not make LOCAL tester activation proof of mailbox ownership or production identity readiness.

## Acceptance before merge

Validate the exact PR head with Customer & Identity, Database Migrator and UAT Gateway Release builds; logical and physical persona tests; concurrent duplicate/idempotent retry probes; existing cooldown/five-per-hour and renewal/activation/expiry races; full Customer Web React, ESLint, TypeScript and production build; PowerShell LOCAL welcome action; 13/13 readiness/topology and existing persona browser journeys. Confirm one request ID produces one issuance/rate-limit count, terminal same-ID replay cannot resurrect a ticket, and bearer material is never persisted or logged. Preserve retained environments/volumes/credentials and keep development GitHub Actions disabled.

**Status:** source candidate only until exact-head LOCAL validation passes.
