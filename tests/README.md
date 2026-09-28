# Verification

`integration-gateway/Monergy.IntegrationGateway.Tests` is the isolated MWP-03-D06
candidate suite. It exercises only deterministic `LOCAL`/`CI_EPHEMERAL`
reference adapters and makes no provider-sandbox or Production claim.

`search-retrieval/Monergy.SearchRetrieval.Tests` is the isolated MWP-03-D07
behavior and canonical contract/schema suite for authorized derived search,
deterministic semantic ranking, rebuildability, provenance and access isolation.

`reporting/Monergy.Reporting.Tests` is the isolated MWP-03-D08 suite for the
authorized basic report lifecycle, deterministic export, authoritative source
references, customer isolation, provenance/lineage continuity, and LOCAL/CI-only
reference adapters. It does not evidence formal report packs, advice, AI, provider
compatibility, persistent infrastructure, or deployment.

D02 adds xUnit architecture/boundary tests. D03 provides 27 tests in the
independent `Monergy.Vs02.Tests` project. D04 provides 38 authority and exact
contract tests in `tests/financial-profile/Monergy.FinancialProfile.Tests`.
Vitest/React Testing Library component and accessibility tests and Playwright
keyboard/reference-flow scenarios retain the accepted frontend regression.
Producer/consumer compatibility preserves D03 14/14 and adds D04 7/7. The
five-boundary vertical slice is evidenced at D07 `SIMULATOR` level.

D04 coverage includes all governed Financial Profile categories, lifecycle,
revision/history, provenance, governed idempotency identity and payload-conflict
handling, customer isolation, Audit propagation, strict producer/consumer
serialization, malformed input per contract, and the Financial Goal
no-methodology boundary. The seven architecture-boundary tests include a
negative dependency rule that prevents the Financial Profile infrastructure
adapter from owning CID-036 contract semantics.

Tests must use public service contracts and must not bypass service boundaries
through cross-service persistence access.
