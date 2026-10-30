# P2-24 — Connector health and telemetry

P2-24 advances `M2-WS06-E02-F03` for the bounded Customer Google Drive and
OneDrive journey. The Connections overview now projects one canonical safe
operational status for every registered provider: **Not configured**,
**Status available**, **Attention required** or **Status unavailable**. Every
projection carries the time at which the connection owner was checked.

Operational status is derived from the current owner response after the same
session, Customer and capability authorization used by P2-20 and P2-21. It is
not a browser-supplied status, a live provider ping, permission approval,
Consent, import completion or outage declaration. Unknown or failed owner
responses fail closed to **Status unavailable** without hiding the other
provider.

Integration Gateway records a safe structured telemetry event containing only
the canonical provider code and projected connection, permission and health
states. It does not record tenant, actor, Customer, account, connection,
folder, file, credential, token, raw response, failure payload or Consent
detail. Customer Web rejects inconsistent health/state combinations rather
than displaying them.

Generalized metrics retention, service-level objectives, administrator health
dashboards, proactive notifications, adaptive circuit administration, physical
provider execution, installed-browser, retained LOCAL and live acceptance are
deferred.
