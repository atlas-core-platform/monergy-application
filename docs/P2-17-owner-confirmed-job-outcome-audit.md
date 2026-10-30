# P2-17 — Owner-confirmed report-job recovery outcomes

P2-17 completes the next bounded part of `M2-WS09-E02-F01` by preserving the governed Job Management event name in the Organization Administrator Audit Log.

The existing `JOB_RECOVERY` request and result remain Product Gateway boundary evidence. `HTTP_SUCCEEDED` proves only that the synchronous recovery request returned successfully. It does not prove that the job ran or that Reporting produced a report.

Job Management remains the lifecycle owner and already emits the governed `JobStarted`, `JobCompleted`, and `JobFailed` events (`CID-058` through `CID-060`) through its durable outbox to Audit. Audit now exposes the exact governed event name as the domain record's safe outcome meaning, while preserving the event's source contract and processing-job reference. Searching the exact job reference therefore returns both the Gateway boundary and the later owner event without joining on private request content.

The ledger remains tenant-bound, append-only, metadata-only, and non-enumerating. It does not retain the administrator's recovery reason, operation identifier, report intent or payload, credentials, financial values, documents, or Consent decision details.

This increment does not add a new event contract, duplicate Job Management state, or claim physical acceptance. Physical PostgreSQL, installed-browser, and retained LOCAL acceptance remain deployment gates.
