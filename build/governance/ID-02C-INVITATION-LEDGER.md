# ID-02C-01 — Durable invitation event ledger

**Scope:** First of several ID-02C implementation units. C&I continues to own credential activation; Access Management continues to own tenant memberships, roles, assignment and authorization. The Organization Administrator never receives bearer activation tokens or chooses another user's password.

## Schema and behavior

Versioned tenant-owned C&I migration `0014_persona_invitation_events.sql` introduces `persona_invitation_events`. Columns are the event's UUID, existing actor ID (tenant-local foreign key), hashed invitation reference, action and recording time. An indexed immutable history records the **ISSUED** event only after `QueueTesterAsync` successfully saves a new challenge. Its transaction commits the challenge and history together, so rejected/cooled-down/unknown recipient requests leave no false issue record.

Successful persona activation conditionally records **CONSUMED** if a matching prior tester issuance exists. Credential creation, existing registration audit and invitation event all commit together. Email activation that was not created using the tester welcome path is unaffected. The ledger contains **no raw ticket, code, password, email body or tenant admin session**. Runtime receives INSERT permission but no update/delete on invitation history.

Physical regression in `PostgresPersonaCredentialTests` validates exactly one ISSUED and one CONSUMED entry for the same pre-provisioned test actor's tester ticket; its unique test rows are cleaned additively after success. Existing single-use, replay, tenant and recovery tests remain.

## Future ID-02C increments — not implemented here

- Event-backed current invitation status/projection (pending, expired, cancelled, consumed) and authorized administrative non-sensitive read models. Do not equate the `registration_challenges` table with durable invitation history.
- Resend/cancel with server-enforced idempotency, rate limit, policy/subject recheck and atomic superseding of prior challenges; expiry processing without false acknowledgments.
- Genuine recipient-bound delivery and independent confirmation for higher environments. LOCAL tester capability is **not** proof of email ownership.
- Reliable notifications, audit correlation and retention. Administrator sees only status and permitted person metadata, never bearer tickets.
- Continue to separate invitation state from AM account-membership `Activated` status.

**Source acceptance gate:** migrate isolated PostgreSQL tenant to 0014 and validate privileges/physical tests, C&I Release build, existing ID-01/02 focused regressions and unchanged 13/13 Docker readiness. Rerun ticket replay/expiry/denied tenant and no-identity tests. No local validation has been executed on this commit. Do not merge pending exact-head evidence, reset retained profiles, or enable dev GitHub Actions.
