# ID-02C-03B-01 — Renewal supersession audit (candidate)

This is the first bounded part of secure invitation renewal, not the entire ID-02C-03B resend experience.

The existing Development-only, protected tester welcome-link issuance already permits a new ticket for a pre-provisioned unactivated persona subject to a one-minute per-email cooldown and five requests per rolling hour. This unit keeps those limits and reuses C&I ownership. On accepted renewal, the C&I transaction records the superseded **live** invitation as CANCELLED in the immutable event ledger and the new invitation as ISSUED. The active challenge row is replaced, so old links fail while the new link can be consumed. A failed, rate-limited or ineligible request must not cancel the previous ticket or add a false issuance event.

A row lock on the previous live challenge coordinates concurrent renewals and activation, and the status projection continues to use the latest issuance. The existing administrator cancellation endpoint and Access Management memberships/roles/consent remain unchanged. No raw bearer credential is stored in the event ledger.

Physical regression covers: first issue, accepted renewal, one prior CANCELLED event, old ticket rejected, new ticket activated and CONSUMED, isolated fixture cleanup.

**Not delivered:** a dedicated admin-initiated resend endpoint, stable cross-request idempotency keys/reply caching, email delivery, separate recipient mailbox proof, explicit EXPIRED ledger events or admin UI. The tester credential remains a supervised LOCAL/DEV capability and must not be exposed to arbitrary users.

**Acceptance:** C&I, Migrator and gateway Release builds; focused and physical PostgreSQL persona tests, cooldown/limit denial and denied reissue never cancel active tokens; activation/renewal races; 13/13 readiness, 8/8 persona browser and existing invitation status/cancel scenarios; full React/ESLint/TypeScript/build and no regression in administrator authorization. Validate exact PR head before merge; do not touch retained Docker environments, enable dev GitHub Actions or claim full ID-02C-03B completion.
