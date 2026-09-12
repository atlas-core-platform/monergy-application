# Shared Platform

This boundary is limited to reusable technical capabilities such as correlation,
OpenTelemetry bootstrap, safe errors, contract serialization, security-context
plumbing and the bounded frontend UI foundation.
It must not own authoritative Customer, Consent, Evidence, Financial Profile,
Calculation, Report, Job, or Audit models.

`Monergy.Platform` configures structured logging, traces and metrics through
vendor-neutral OpenTelemetry SDKs and OTLP export. `frontend-ui` owns semantic
tokens and Ant Design/Tailwind theme integration; business components remain in
application capability boundaries.
