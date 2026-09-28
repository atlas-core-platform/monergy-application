# Authorized Search & Provenance Retrieval — D07 candidate

This boundary implements the five CTO-authorized MWP-03-D07 Features at
`SIMULATOR` evidence level for `LOCAL` and `CI_EPHEMERAL` only.

The in-memory document, financial and deterministic semantic indexes are
`DERIVED_REBUILDABLE`. Evidence Service, Financial Profile Service and Financial
Rules Service remain authoritative; every result retains source, provenance and,
where applicable, calculation-lineage references.

CID-042, CID-043 and CID-044 are exposed as authorized query endpoints. CID-046
is the derived-index refresh job boundary. The implementation consumes CID-007,
CID-023, CID-024, CID-033, CID-034, CID-035, CID-036 and CID-039 without taking
ownership of their meaning.

The semantic representation is deterministic reference behavior, not a
production embedding-quality claim. No AI/model, provider, database, broker,
physical search store or vector product is selected.
