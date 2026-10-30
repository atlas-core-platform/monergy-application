# P2-18 — Security-relevant sign-in attention

P2-18 adds a bounded Organization Administrator review signal for repeated failed sign-ins. Audit owns the immutable evidence and the derived tenant review view. Customer & Identity continues to own authentication, password policy, session creation, rate limiting and account state.

## Governed behavior

- Customer password, persona password, email-confirmation and tenant-session authentication boundaries use the canonical `LOGIN` audit category.
- Audit counts only `LOGIN` result records with `HTTP_FAILED` in the exact previous 15 minutes for the authenticated administrator's tenant.
- Five or more failures produce `REVIEW_NEEDED`; fewer produce `NO_CURRENT_SIGNAL`.
- The administrator can open the exact failed-login evidence window in Audit Log. Current administrator authority is checked before and after the owner read.
- Tenant boundaries, append-only evidence and exact request/result meaning remain unchanged.

## Privacy and authority boundary

The signal is tenant-wide. It does not identify an individual and does not retain or infer an email address, password, credential, IP address, device fingerprint, customer financial data or document content. It is review evidence, not proof of attack, automatic account suspension or authorization to change an identity. Audit does not authenticate users, modify sessions or bypass Customer & Identity.

## Deferred scope

No generalized SIEM, incident case workflow, notification delivery, configurable threshold, IP/device collection, automatic lockout or cross-tenant security view is introduced. Physical PostgreSQL, installed-browser and retained LOCAL acceptance remain separate gates.
