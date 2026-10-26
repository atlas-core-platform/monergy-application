# ID-02C-03C — Durable invitation expiry materialization (candidate)

This bounded unit closes the explicit expiry-audit gap left by ID-02C-03A/B. It does **not** add production email, an administrator resend workflow, notification delivery or a new credential grant.

## Behavior

C&I remains the owner of invitation challenges, credentials and invitation history. When an issued LOCAL tester activation challenge has reached its stored `expires_at`, invitation-status/cancellation owner operations lock that challenge and append one immutable `EXPIRED` event if no `CONSUMED`, `CANCELLED` or prior `EXPIRED` terminal event exists. Repeated reads/cancellation attempts are idempotent because the ledger keeps the existing unique `(invitation_hash, action)` invariant.

A renewal that replaces an already-expired challenge now records the old invitation as `EXPIRED`, not `CANCELLED`. A live superseded challenge continues to record `CANCELLED`. The replacement issuance remains subject to the existing one-minute cooldown and five-per-hour limit.

Expiry never creates a credential, session, Access Management membership/role/permission or consent change. No raw welcome ticket, verifier, password or tester capability is written to the event ledger or returned to the administrator.

## Concurrency boundary

Expiry processing locks the still-unconsumed expired challenge before writing the terminal event. A racing activation/cancellation that commits first changes the challenge state and is re-evaluated out; otherwise expiry owns the already elapsed challenge. The terminal insert rechecks the ledger in a fresh READ COMMITTED statement before writing.

## Physical regression

The added PostgreSQL regression proves:

- an already-expired issued ticket materializes exactly one `EXPIRED` event;
- repeated status reads do not duplicate the event;
- the expired ticket cannot activate credentials;
- administrator cancellation returns `NotCancelled` and does not create `CANCELLED`;
- a later renewal preserves the old `EXPIRED` terminal history;
- the replacement ticket can still activate once and records `CONSUMED`.

## Still outstanding

General administrator resend UX/API, stable cross-request idempotency/reply caching, production recipient-verified email delivery, notifications and retention remain separate increments. The protected tester flow remains Development/LOCAL/UAT-only and must never be treated as proof of mailbox ownership.

## Acceptance before merge

Validate the exact PR head with Customer & Identity, Database Migrator and UAT Gateway Release builds; focused/physical PostgreSQL persona tests; existing cooldown/throttling and activation-versus-renewal races; administrator authorization negatives; 13/13 isolated readiness; existing 8/8 persona browser journeys plus invitation status/cancellation coverage; full React, ESLint, TypeScript and production build. Preserve retained environments/volumes/credentials and keep development GitHub Actions disabled.

**Status:** source candidate only until those exact-head gates pass.
