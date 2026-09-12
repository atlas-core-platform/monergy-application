# Service Boundaries

`catalog.json` is the bootstrap ownership inventory for the twelve deployable R3
services. Each folder is an independent source, artifact, persistence, identity,
and migration boundary even if future physical infrastructure is consolidated.

The `migrations/` folders are deliberately empty scaffolds. D01 introduces no
database technology, schema, migration, shared migration authority, or
application-startup migration behavior.

