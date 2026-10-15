# Service Boundaries

`catalog.json` is the ownership inventory for the twelve deployable R3 services.
Each folder contains an independently compilable .NET 10 executable boundary
and an independent Linux OCI definition. Nine boundaries use ASP.NET Core HTTP
primary hosts. Document Intelligence, Job Management and Audit retain Worker
Service primary hosts. Document Intelligence and Job Management may expose
bounded workload-only secondary HTTP adapters while retaining their asynchronous
processing responsibilities and one authoritative business-state container.
Seven non-participating boundaries retain health/startup behavior only. D03
adds bounded implementation accepted at `SIMULATOR` to Evidence, Document
Intelligence, Financial Profile, Job Management and Audit. Those services share
only the governed contract assembly and technical platform; no service imports
another service implementation.

Each service remains an independent source, artifact, persistence, identity and
migration boundary even if future physical infrastructure is consolidated.

The `migrations/` folders remain deliberately empty. D03 introduces no
database technology, schema, migration, shared migration authority, or
application-startup migration behavior. Its in-memory repositories, fixture
extractor and append-only audit store are non-production reference adapters
guarded to LOCAL/CI execution zones.

D04 expands only Financial Profile Service and the already-governed Audit
consumer. Financial Profile now owns provider-neutral current profile/fact
queries, thirteen child financial-object categories, explicit revisions,
immutable provenance history, idempotent normalization and CID-034–CID-036
reference outbox events. Audit remains append-only and consumes only its
canonical CID-034/CID-035 event families. No service imports another service
implementation.
