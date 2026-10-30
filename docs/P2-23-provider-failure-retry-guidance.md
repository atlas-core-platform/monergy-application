# P2-23 — Provider failure and retry guidance

P2-23 advances `M2-WS06-E02-F02`, `MON-ERR-002` and the existing Google Drive
and OneDrive Customer journeys. Provider rate limits now return the governed
60-second manual retry delay. Customer Web distinguishes rate limiting,
expired/reduced provider permission, changed files/connections and temporarily
unavailable owners using safe business language.

No browser operation is automatically retried. A rate-limited exact import may
retain its canonical connection/file identity for a later manual retry.
Definitive permission, file-version, validation or authorization failures clear
that pending intent and require a current refresh or reconnection. Ambiguous
unavailability continues to use P2-22 exact-import confirmation.

The server records only provider label, governed failure code, HTTP status and
retryability through the application logging/monitoring surface. It does not
record tenant, actor, customer, file, credential, token, raw provider response
or Consent detail. The canonical Integration Gateway retry budget continues to
re-check authorization and Consent between attempts, publishes one terminal
failure result when exhausted, and does not automatically retry provider rate
limits.

Generalized notification delivery, adaptive backoff, multi-provider circuit
administration, physical provider execution, installed-browser, retained LOCAL
and live-provider acceptance remain pending.
