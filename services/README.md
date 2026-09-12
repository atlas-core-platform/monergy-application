# Service Boundaries

`catalog.json` is the ownership inventory for the twelve deployable R3 services.
Each folder now contains an independently compilable .NET 10 executable scaffold
and an independent Linux OCI definition. Eight boundaries use ASP.NET Core HTTP
hosts; Document Intelligence, Reporting, Job Management and Audit use Worker
Service hosts because their accepted primary responsibilities are asynchronous.
Only health/startup behavior exists.

Each service remains an independent source, artifact, persistence, identity and
migration boundary even if future physical infrastructure is consolidated.

The `migrations/` folders remain deliberately empty. D02 introduces no
database technology, schema, migration, shared migration authority, or
application-startup migration behavior. No business endpoint, command, event or
workflow is implemented.
