# ID-02C-04A — Durable recipient-delivery invitation audit (candidate)

This bounded unit extends the accepted C&I invitation lifecycle from the Development-only tester path to the existing authenticated email persona-activation channel.

It does **not** add a Tenant Administrator resend button/API, automatically send mail from an onboarding receipt, introduce a new SMTP provider, or claim production identity readiness.

## Delivery-backed issuance

The existing persona email activation flow already stores a short-lived protected verification code in `registration_challenges` and delivers it through the authenticated TLS SMTP sender.

For `persona-activation` only, the delivery worker now commits two owner-side facts in one PostgreSQL transaction after the sender reports success:

1. the exact current challenge becomes `delivered=true` and its protected delivery payload is removed;
2. one immutable C&I `persona_invitation_events/ISSUED` event is appended for the existing tenant principal and challenge hash.

The invitation ledger therefore records recipient delivery, not merely queueing. Failed SMTP attempts do **not** write `ISSUED`. Registration, recovery and persona-recovery delivery remain outside invitation issuance history.

Delivery completion also rechecks the challenge expiry inside the same owner-side transaction. If SMTP returns after the challenge has expired, C&I does not mark it delivered or append `ISSUED`; it removes the protected payload so the stale code cannot be retried or recovered.

The raw verification code, protected payload, password and SMTP credential are never copied into the invitation ledger.

## Renewal correctness before replacement delivery

When C&I accepts a new `persona-activation` email request for the same unactivated principal, it serializes that recipient and checks the previously delivered invitation before replacing the challenge.

If the prior delivered invitation is still live, it receives one terminal `CANCELLED` event. If its five-minute lifetime already elapsed, it receives `EXPIRED` instead.

This happens only when the replacement request is actually accepted by the existing one-minute/five-per-hour durable rate limits. A rejected/throttled request does not terminally change the valid invitation.

The replacement itself does **not** become `ISSUED` until its email delivery succeeds. During the gap between accepted replacement request and successful delivery, the old invitation is correctly terminal and the new invitation is not falsely represented as delivered.

## Reuse of accepted lifecycle

Once email delivery records `ISSUED`, the already accepted C&I lifecycle applies unchanged:

- administrator status projection can observe the delivered invitation;
- expiry materialization can record `EXPIRED`;
- administrator cancellation can revoke a still-pending delivered invitation;
- successful credential activation conditionally records `CONSUMED` in the same credential transaction;
- old/replaced challenge hashes cannot activate.

No Access Management membership, role, permission, relationship or Consent mutation is introduced.

## Physical regression

A real PostgreSQL regression covers:

- queued email activation has `ISSUED=0` before successful delivery;
- controlled SMTP failure leaves `delivered=false` and `ISSUED=0`;
- later successful delivery atomically produces `delivered=true` and exactly one `ISSUED`;
- a delivery that finishes after challenge expiry produces no `ISSUED`, remains undelivered and clears the protected payload;
- accepted replacement records the old delivered invitation as `CANCELLED` before replacement delivery;
- replacement delivery produces one new `ISSUED`;
- old challenge activation fails;
- replacement activation succeeds and records one `CONSUMED`;
- delivered persona-recovery mail does not create invitation events;
- delivery racing renewal cannot issue the superseded challenge;
- concurrent delivery, activation and cancellation produce at most one terminal outcome;
- administrator projection follows the real lifecycle: absent before delivery → Pending → Cancelled while replacement awaits delivery → Pending after replacement delivery → Completed after activation.

The test uses only isolated LOCAL/CI database rows and a controlled sender; it does not send external email.

## Existing authority and grants

No migration is required. The existing C&I runtime role already has:

- SELECT on C&I tables;
- INSERT/UPDATE on `registration_challenges`;
- INSERT-only authority on `persona_invitation_events`;
- no UPDATE/DELETE authority on invitation history.

Do not weaken these grants.

## Still outstanding

A genuine Tenant Administrator initiated resend/send experience connected directly to onboarding, explicit delivery-failure/admin notification UX, production provider acceptance/operations, and invitation-history retention remain separate increments.

## Acceptance before merge

Validate the exact PR head with Customer & Identity, Database Migrator and UAT Gateway Release builds; focused persona/email logical tests; the new physical PostgreSQL delivery lifecycle regression; existing tester invitation, renewal, expiry, cancellation and idempotency regressions; runtime invitation-ledger privilege checks; full Customer Web lint/types/build and existing persona/browser regressions.

For a production-like email profile with a controlled/local SMTP sink, prove successful recipient delivery produces exactly one `ISSUED`, failed delivery produces none, accepted resend terminally closes the previous delivery, and successful activation records `CONSUMED`. Confirm no raw code/ticket/password/SMTP secret appears in invitation history or logs.

Preserve retained environments/volumes/credentials and keep development GitHub Actions disabled.

**Status:** source candidate only until exact-head LOCAL validation passes.
