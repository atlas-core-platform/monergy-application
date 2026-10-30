# P2-20 — Provider-neutral connection overview

P2-20 advances `MON-PR-008`, `MON-PR-009`, `M2-WS06-E01-F01` and
`M2-WS06-E01-F02` with one bounded customer journey. An authenticated customer
can open **Connections** and review a canonical status for each currently
registered provider before opening that provider's owner-specific management
surface.

## Ownership and authorization

- Integration Gateway owns the provider-neutral overview contract and the
  projection of owner-specific states into safe business language.
- The Google Drive and OneDrive adapters continue to own OAuth, tokens,
  provider calls, folder selection, connection records and imports. The
  overview does not copy or replace those responsibilities.
- Tenant, actor and customer are resolved from the current verified session and
  customer-owner workspace. The request cannot supply a provider, tenant,
  actor, customer, grant or provider payload.
- `integration-gateway.connection.manage` is re-evaluated before any provider
  status is read. Provider failures do not weaken that boundary.

## Canonical result

The overview returns exactly the registered Google Drive and OneDrive sources
in stable provider-code order. Each source is projected to one of:

- `Not configured`
- `Ready to connect`
- `Setup in progress`
- `Connected`
- `Needs attention`
- `Unavailable`

Unknown or failed provider results fail closed to `Unavailable`. A last-confirmed
time is exposed only for a `Connected` result. No provider credentials, bearer
tokens, authorization codes, account identifiers, folder identifiers, file
metadata, import payloads or raw provider errors are returned.

`Not configured` means that the organization has not supplied the provider
configuration. It does not imply provider success. Opening a provider does not
grant Consent, connect an account or import data.

## Verification boundary

Source validation covers current-session authorization before provider reads,
stable canonical projection, safe partial failure, unknown-state fail-closed
behavior, exact tenant/customer binding, stale-browser-context rejection and
the separate provider-management routes. Physical provider OAuth, retained
LOCAL UAT, installed-browser acceptance and live provider execution remain
deployment acceptance gates and are not represented as completed by this
increment.
