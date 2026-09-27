# Integration Gateway Core — D06 candidate

This boundary implements the six CTO-authorized MWP-03-D06 Features at
`SIMULATOR` evidence level. It owns provider-neutral execution, connector
registration, current authorization/consent enforcement, idempotent replay,
bounded retries, explicit failure classification, circuit state, derived
connector health and semantic telemetry.

`ReferenceProviderConnector` is deterministic and guarded by
`ReferenceAdapterGuard`; it is permitted only in `LOCAL` and `CI_EPHEMERAL`.
It performs no network access and does not select a provider. Provider payloads,
credentials and contract details remain behind `IProviderConnector`.

CID-015 and CID-016 are exposed as request endpoints. CID-017 and CID-018 are
published through the event sink boundary. The implementation consumes trusted
security context (CID-007) and a current purpose-bound consent decision
(CID-011) through distinct `IGatewayAuthorizationPolicy` and
`IConsentDecisionPort` boundaries; it does not implement identity or
consent ownership.

This candidate is not a sandbox, UAT or Production integration and does not
advance any stage gate or resolve OD-04, OD-05 or OD-06.
