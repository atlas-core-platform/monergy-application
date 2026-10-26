# ID-02C-03D — Tenant Administrator invitation cancellation UX (candidate)

This bounded unit closes the administrator cancellation **UI** gap left by ID-02C-03A. It reuses the already accepted C&I owner endpoint `POST /local/v1/administration/persona-invitations/{actorId}/cancel`; it does not introduce a new cancellation mutation, resend path, email delivery mechanism or Access Management grant.

## Experience

After an onboarding receipt reaches `Activated`, the existing **Check invitation status** surface continues to show only invitation records belonging to actors from that onboarding receipt. A destructive **Cancel invitation** action is shown only when C&I currently reports that actor's credential invitation as `Pending`.

The administrator must confirm the action. The confirmation explicitly states that cancellation revokes only the pending credential invitation: organization membership and assigned access remain active. The browser never receives or displays the invitation ticket, verifier, password or LOCAL tester capability.

The Customer Web calls the existing C&I cancellation route using the authenticated tenant/session headers and no caller-supplied tenant. Actor IDs are path-bounded in the client and are still revalidated by the owner endpoint.

## Race-safe UI behavior

The cancellation response is not treated as the final display authority.

- `Cancelled`: the row is immediately made non-actionable, then C&I status is re-read.
- `NotCancelled`: the UI does **not** claim cancellation. It re-reads C&I and shows the authoritative terminal state such as `Expired` or `Completed`.
- If the mutation succeeds but the follow-up status refresh fails, the UI distinguishes the accepted cancellation from the failed refresh.
- 401/403 responses continue through the existing session-expiry/authority-loss handling.

This keeps browser behavior aligned with the database race guarantees already accepted in ID-02C-03A/C.

## Source regression

Focused `ImportDrawer` tests cover:

1. Pending invitation -> confirmation -> POST cancellation -> refreshed `Cancelled`; unrelated invitation rows remain hidden and organization access is explicitly preserved.
2. Pending invitation -> cancellation loses a terminal-state race -> `NotCancelled` -> refreshed `Expired`; no false cancellation success is displayed.

## Still outstanding

General administrator resend UX/API, stable cross-request resend idempotency/reply caching, production recipient-verified email delivery, notifications and retention remain separate increments. Cancellation remains credential-invitation lifecycle only and must never be interpreted as removing organization membership or business access.

## Acceptance before merge

Validate the exact PR head with the focused ImportDrawer suite, existing Access/Persona React regressions, full Customer Web ESLint, TypeScript and production build. Run isolated LOCAL browser UAT against the real C&I/Gateway owner route: A900 can cancel a Pending invitation; A100/A300 are denied; revoked/cross-tenant sessions are denied; a cancelled ticket cannot activate; race-to-expiry/activation refreshes authoritative status; Completed/Expired/Cancelled rows expose no cancellation action. Reconfirm 13/13 readiness, 8/8 persona journeys and no bearer material in administrator responses. Preserve retained environments/volumes/credentials and keep development GitHub Actions disabled.

**Status:** source candidate only until exact-head LOCAL validation passes.
