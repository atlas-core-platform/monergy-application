# P2-21 — Provider connection permission readiness

P2-21 advances `M2-WS06-E01-F03` and the existing execution-time Consent
boundary with a bounded customer-visible journey. The provider-neutral
Connections view now shows a canonical permission readiness state beside each
registered Google Drive and OneDrive connection.

## Canonical projection

Permission readiness is derived only from the safe owner-confirmed connection
state:

| Connection state | Permission readiness |
| --- | --- |
| Not configured / Ready to connect | Not requested |
| Setup in progress | Pending |
| Connected | Current |
| Needs attention | Review required |
| Unavailable or unknown | Unavailable |

`Current` is returned only after the provider adapter's status path has checked
the authenticated Customer workspace, the connection-management capability
and the current purpose-bound Consent record. Actual connect, folder, list,
download and import operations continue to re-evaluate their required current
authorization and Consent at their existing server boundaries.

The overview never creates, renews, revokes, approves or otherwise changes
Consent. It accepts no tenant, actor, customer, provider, connection or Consent
identifier from the browser. Inconsistent or unknown connection/permission
combinations are rejected by Customer Web rather than displayed as usable.

No Consent identifier, version, expiry, provider credential, authorization
code, token, account/folder identifier, import payload or raw owner error is
exposed. Physical Consent-owner/provider execution, installed-browser and
retained LOCAL acceptance remain pending.
