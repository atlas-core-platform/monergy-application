# ID-02C-02 — Tenant-admin invitation status (candidate)

**Scope:** A read-only projection over the tenant's C&I-owned `persona_invitation_events` ledger. It does not create, resend, cancel, reveal, or authorize invitation tickets, and does not change any Access Management role or business consent.

C&I endpoint: `GET /local/v1/administration/persona-invitations`. The existing C&I administrator authority is required before and after the read; the tenant is derived from that session, never a user-provided tenant parameter. Output is a bounded 50-row page with opaque actor cursor and allowed fields only: actor ID, normalized email, calculated status, issuance time, consumption time. No invitation hash, ticket, verifier, password, administrator key or other bearer secret. Responses carry no-store. Current status is derived from the **latest** issued invitation for each actor: Completed when matching CONSUMED event exists; Expired after five minutes; Pending otherwise. No resend/cancel events exist yet.

In People onboarding, a confirmed Activated receipt now offers **Check invitation status**. It reads the authorized C&I projection and displays only persons included in that AM onboarding receipt. Membership and credential activation are explicitly separate.

## Tests and acceptance

Require an isolated tenant DB with migration 0014 journaled and least-privilege SELECT. Verify administrator sessions return only same-tenant data; A300/A100 must get denied; a revoked/stale admin session fails. Verify Pending → Completed on credential activation, Pending → Expired after five minutes, issuance superseding rules, no raw hashes in JSON, 50-row paging, stable cursors and repeat reads; verify unavailable dependency gives typed 503. Run ImportDrawer/AccessExperience React, full ESLint/TypeScript/build, physical C&I tests, and existing 13/13 isolated Docker/browser persona regressions. Browser must not reveal other imported rows. Preserve all retained volumes and secrets, leave GitHub Actions off.

**Implementation status:** candidate source only, not yet locally compiled or UAT accepted. This is not full ID-02C: cancellation, resend, durable delivery, expiry materialization and notification remain future increments.
