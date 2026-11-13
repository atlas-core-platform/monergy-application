# Service Boundaries

`catalog.json` preserves the twelve deployable R3 services and adds the
post-baseline Application & Case Service introduced by P3-01. The current
inventory therefore contains thirteen independently deployable services.
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

Service-owned `migrations/` folders remain isolated by owner. D03 introduced no
database technology or shared migration authority; later accepted increments
added owner-specific physical persistence. P3-02 adds the first Application &
Case PostgreSQL migration and bounded runtime adapter for LOCAL/CI while keeping
migration execution outside application startup. P3-03 advances that owner schema
for Financial Advisor engagement history without introducing a shared database or
moving Access Management, Customer Identity or Consent authority into the service.

D04 expands only Financial Profile Service and the already-governed Audit
consumer. Financial Profile now owns provider-neutral current profile/fact
queries, thirteen child financial-object categories, explicit revisions,
immutable provenance history, idempotent normalization and CID-034–CID-036
reference outbox events. Audit remains append-only and consumes only its
canonical CID-034/CID-035 event families. No service imports another service
implementation.
