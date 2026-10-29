# P2-02 — Customer-reviewed Credit Card records

## Delivered boundary

P2-02 adds one bounded Customer journey under the existing Financial Information authority. A Customer can create, read and correct a reviewed Credit Card record from one exact canonical `LOAN` Financial Fact revision. The record stores only issuer name, card type and the last four ASCII digits. Outstanding balance, currency and as-of date are copied from the immutable source revision and cannot be edited through the Credit Card record.

The customer experience retains read-only access when `financial-profile.profile.update` is missing and exposes the canonical `customer.financial-information` Request Access action around add and correction operations. Source-document access is still required to view the record and its exact source.

## Owner and integrity controls

- Financial Profile remains the record and source-fact owner.
- Read requires `financial-profile.profile.read` and `evidence.document.read`; create/correct also requires `financial-profile.profile.update`.
- Tenant, actor and Customer identity come from the current trusted session and owner lookup; browser payloads cannot select them.
- Create and correction operations are idempotent. Corrections are append-only and reject stale revisions.
- PostgreSQL runtime privileges are `SELECT, INSERT`; `UPDATE`, `DELETE` and `TRUNCATE` are denied for both record and revision tables.
- Source fact type, Customer, revision, provenance, evidence, currency, value and date are revalidated. Cross-Customer or missing source revisions fail closed.

## Deliberate exclusions

P2-02 does not store a full card number, CVV, expiry date, PIN, OTP or credential. It does not connect to an issuer, payment network, transaction feed, CIC or other credit bureau; verify ownership, limits or transactions; calculate interest; or make a credit decision. Broader credit history, credit scoring, transaction reconciliation and provider onboarding remain separate governed work.

## Acceptance position

Release builds, focused owner/Gateway tests, React tests, TypeScript, ESLint and production bundle verification are source gates. The new migration, runtime grants, restart readback, retained LOCAL profiles and installed-browser journey remain subject to physical acceptance; this increment does not claim production or business acceptance.
