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
