# Contract Boundary

The taxonomy folders remain the accepted D03 classification homes. MWP-03-D03
adds `Monergy.Contracts` and `schemas/vs02-contracts.schema.json` for exactly
CID-020, CID-021, CID-022, CID-023, CID-024, CID-025, CID-027, CID-028, CID-031,
CID-033, CID-034, CID-035, CID-055 and CID-057 at contract version `1.0.0`.

Cross-service behavior must use published contracts. A consumer must never
import another service's domain or persistence implementation merely to reuse a
type.

The shared envelope carries trusted actor, workload, authorization, purpose,
customer, correlation, causation and idempotency context. Provider payloads and
provider-specific types are excluded.

MWP-03-D04 adds a separate `D04ContractCatalog` and
`schemas/financial-profile-authority.schema.json` for exactly CID-030 through
CID-036. Contract version remains `1.0.0`. The accepted fourteen-contract D03
catalog/schema is intentionally unchanged so D03 remains independently
regression-verifiable.

MWP-03-D12 adds a separate `D12ContractCatalog` and
`schemas/document-reprocessing.schema.json`. Its direct verification scope has
exactly twelve existing logical families; only CID-026 `ReprocessDocument` and
CID-029 `DocumentProcessingFailed` are newly realized. The accepted D03
fourteen-contract catalog and schema remain unchanged. Contract version remains
`1.0.0`, and the D12 reason vocabulary is bounded to `FAILED_PROCESSING` and
`UPDATED_EVIDENCE`.
