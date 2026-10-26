# ID-02C-03A — Pending invitation cancellation (candidate)

This is the **first bounded ID-02C-03 implementation unit**, not the whole resend/cancel/expiry milestone.

An authorized tenant administrator can cancel a still-pending LOCAL tester invitation by actor ID using C&I's owner route `POST /local/v1/administration/persona-invitations/{actorId}/cancel`, forwarded via the explicit UAT gateway allowlist. C&I derives tenant from the validated administrator session. No caller-supplied tenant or credential is honored.

The C&I owner atomically marks the matching outstanding `persona-activation` challenge consumed and appends a `CANCELLED` record in the immutable tenant-owned invitation event ledger. It must match an existing issued hash and current principal, still be unconsumed, unexpired and not already activated. A racing activation either consumes first or sees a revoked challenge, subject to database row locking. Repeating a request with no eligible outstanding challenge safely returns `NotCancelled` and does not write a false event. The status projection now presents `Cancelled` ahead of elapsed-expiry for matching cancelled hashes.

The endpoint returns only `actorId` and `state` and never reveals ticket, hash, credentials, password or tester secret. No membership/role/consent grants are changed. The LOCAL tester welcome flow remains Development-only. Cancellation does not deliver any email.

**Acceptance required before merge:** C&I/Gateway Release builds; isolated PostgreSQL physical cancellation and race tests; authorized admin 200, A100/A300 403, revoked or foreign-tenant admin 401; challenge replay 401 after cancellation; confirm exactly one immutable CANCELLED event, no CONSUMED event and no credentials; second cancellation NotCancelled; failed/expired/activated paths cannot mutate; 13/13 readiness and new browser receipt status with existing persona 8/8 unchanged. Full Customer Web ESLint, React and builds. Preserve retained profiles and Docker volumes; do not enable development Actions.

**Still outstanding:** authenticated/self-service resend, idempotent retry, explicit expiry audit/materialization, administrator cancellation UX, production recipient-verified email delivery, notification and retention. Do not claim full ID-02C-03 complete on this unit.
