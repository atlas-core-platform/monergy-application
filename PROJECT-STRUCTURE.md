# Monergy Application Repository Structure — D13 Customer Identity Candidate

The D05 application root has exactly six governed responsibility areas:

```text
monergy-application/
├── .github/              GitHub Actions and CODEOWNERS governance
├── apps/                 Reserved customer and administration web applications
├── services/             Twelve independently owned R3 service boundaries
├── contracts/            Transport-neutral D03 contract implementation boundary
├── shared/platform/      Reusable technical platform concerns only
├── tests/                Contract, integration, end-to-end, and bootstrap evidence
└── build/                Local/CI bootstrap, policy, release, and supply-chain logic
```

`.github/` realizes hosted repository governance and technology-specific CI; it
does not add a seventh application responsibility. Root policy/toolchain files do
not add application responsibilities. `.artifacts/` and `.toolcache/` are ignored
local evidence/tool homes.

D02 provides the accepted toolchain foundation. D03 adds:

- a transport-neutral `Monergy.Contracts` assembly and closed VS-02 JSON schema;
- executable application/domain ports in Evidence, Document Intelligence,
  Financial Profile, Job Management, and Audit;
- LOCAL/CI-only in-memory, fixture and append-only reference adapters;
- `build/governance/vs02-scope-lock.json` with exact 8-Feature, 14-contract,
  and 5-service scope;
- `apps/customer-web/src/vs02/` as a separately loaded, clearly labeled
  reference experience;
- `tests/vs02/` and frontend component/accessibility/Playwright evidence;
- `build/measure-frontend-bundle.mjs` for reproducible production-bundle size
  and lazy-route isolation evidence;
- `build/verify-vs02.ps1` with deterministic checks and negative self-tests.

D04 adds without changing the six-area topology:

- `build/governance/d04-scope-lock.json` for the exact seven Features,
  CID-030–CID-036, two participating boundaries, Business Objects, and exact
  DP/EP/NFR/TQ obligations;
- `contracts/Monergy.Contracts/D04ContractCatalog.cs` and
  `contracts/schemas/financial-profile-authority.schema.json` as a separate
  seven-contract surface that does not widen the accepted D03 catalog;
- Financial Profile aggregate/query behavior, thirteen governed child object
  categories, explicit revisions, immutable fact/provenance history, and a
  LOCAL/CI-only reference outbox;
- permanent domain, lifecycle, isolation, idempotency, audit, serialization,
  malformed-input, and D03 regression tests, with D04-owned coverage isolated
  in `tests/financial-profile/Monergy.FinancialProfile.Tests`;
- `build/verify-financial-profile-authority.ps1` with deterministic checks and
  negative self-tests.
- `build/governance/d04-acceptance.json` with the approved candidate,
  workflow/artifact hashes, exact contract overlap, remediation outcomes,
  security findings and unchanged stage gates.

D02 assets retained include:

## D05 implementation and application acceptance

- `services/financial-rules/Domain/EngineeringRules.cs`: typed engineering fixtures, not client methodology.
- `services/financial-rules/Application/`: rule/input/access/persistence ports and owner-controlled execution, queries and historical reproduction.
- `services/financial-rules/Infrastructure/`: LOCAL/CI-only atomic in-memory repository, immutable registry, explicit synthetic policy grants, governed Financial Profile transport and post-commit outbox dispatch.
- `FinancialRulesRegistration.cs` and `FinancialRulesEndpoints.cs`: gated composition and CID-037–CID-039 endpoints.
- `contracts/Monergy.Contracts/FinancialRulesContracts.cs` and `contracts/schemas/financial-rules.schema.json`: separate five-contract D05 surface.
- `tests/financial-rules/Monergy.FinancialRules.Tests/`: owned behavior, contract and actual-owner lineage evidence; no test moved out of D03/D04.
- `build/governance/d05-scope-lock.json` and `build/verify-financial-rules.ps1`: unchanged reviewed candidate scope and live behavioral verifier.
- `build/governance/d05-acceptance.json`: exact CTO-reviewed identities, accepted six Features/five new contracts, 22-contract program union, retained evidence, architecture dirty-file exception and unchanged readiness limits.
- `build/verify-d05-acceptance.ps1`: acceptance-record/negative checks and immutable business-tree verification; no architecture repository mutation.
- `build/governance/d05-frontend-lifecycle.md`: bounded closure-pending remediation diagnosis and retained failed evidence.
- `apps/customer-web/tests/componentLifecycle.ts`: VS-02 test-owned clock cleanup, not product code.
- `apps/customer-web/tests/FormLifecycle.diagnostic.tsx` and `tests/lifecycle.config.ts`: isolated before/after lifecycle diagnostic, separate from the seven-test frontend suite.
- `build/probe-frontend-lifecycle.mjs` and `build/frontend-lifecycle-reporter.mjs`: fail-fast twenty-execution Linux remediation evidence; the D05 branch's workflow gates full CI on it without retries.
- `services/financial-rules/README.md`: reference execution instructions and limits.

## D06 Integration Gateway Core candidate

- `contracts/Monergy.Contracts/D06IntegrationGatewayContracts.cs` and `D06ContractCatalog.cs`: provider-neutral CID-015–CID-018 contract types and isolated catalog.
- `contracts/schemas/integration-gateway-core.schema.json`: closed D06 wire-contract schema.
- `services/integration-gateway/Application/`: orchestration and ports for access, connector execution, idempotency, events, health and telemetry.
- `services/integration-gateway/Domain/`: canonical attempt, operation, replay and derived-health states.
- `services/integration-gateway/Infrastructure/`: LOCAL/CI-only reference connector, registry, policy adapter, operation store, event sink, runtime state and telemetry.
- `services/integration-gateway/IntegrationGatewayRegistration.cs` and `IntegrationGatewayEndpoints.cs`: guarded composition and CID-015/CID-016 HTTP compatibility surface.
- `tests/integration-gateway/Monergy.IntegrationGateway.Tests/`: isolated D06 contract, security, replay, retry, failure, circuit, health and telemetry evidence.
- `build/governance/d06-scope-lock.json`: exact six-Feature, two-consumed/four-realized-contract candidate boundary.
- `build/verify-integration-gateway-core.ps1`: deterministic positive and negative D06 scope verifier.

## D09 persistent data foundation candidate

- `build/Monergy.DatabaseMigrator/`: DbUp orchestration for independently owned service migration folders; it contains no business schema definitions.
- `build/d09/`: pinned PostgreSQL 18.6 and SeaweedFS 4.47 LOCAL/CI composition, runtime-generated credentials, migration execution, and retained-volume durability orchestration.
- `build/governance/d09-scope-lock.json`: exact technology, five-service cohort, provider exclusions, stage gates, and architecture-integrity lock.
- `build/verify-persistent-data-foundation.ps1`: D09 deterministic verifier and 18 negative self-tests.
- `services/{evidence,financial-profile,financial-rules,reporting,audit}/migrations/`: ordered, immutable, service-owned PostgreSQL migrations.
- `services/*/Infrastructure/Postgres*.cs`: runtime-role adapters behind the accepted application ports. Reference adapters remain available for their governed execution mode.
- `services/evidence/Infrastructure/PostgresEvidenceAdapters.cs`: PostgreSQL Evidence metadata plus private, versioned S3-compatible byte storage with Monergy SHA-256 integrity.
- `tests/persistence/Monergy.Persistence.Tests/`: real PostgreSQL/S3 ownership, isolation, idempotency, outbox, append-only, reconstruction, and durability checks.

## Retained D02 assets

- `.dockerignore` for the controlled OCI build context;
- exact .NET/Node/pnpm/package pins and dependency locks at the root;
- twelve independent .NET service/worker projects and OCI definitions;
- `shared/platform/Monergy.Platform/` for vendor-neutral technical bootstrap;
- `shared/platform/frontend-ui/` for shared semantic tokens and Ant/Tailwind
  integration;
- the `apps/customer-web/` toolchain-verification shell;
- architecture, component/accessibility and browser smoke tests;
- build, supply-chain, release-manifest and deterministic verification scripts.
  The hosted OCI script emits twelve archive/digest/SBOM/scan rows without
  publishing images.

D09 introduces only the approved PostgreSQL and S3-compatible physical persistence technology for LOCAL / CI_EPHEMERAL. It does not select a Production hosting product, broker, search/vector provider, AI/model provider, cloud, orchestrator, or persistent-environment deployment. Runtime credentials are generated outside tracked source. Reference adapters remain governed evidence and are not Production adapters.

## D10 durable Job Management and Audit propagation candidate

- `build/d10/`: pinned LOCAL / CI_EPHEMERAL PostgreSQL composition, bootstrap, migration and retained-volume verification.
- `build/governance/d10-scope-lock.json`: exact four-Feature, eighteen-contract and unchanged-governance scope lock.
- `build/verify-durable-job-audit-propagation.ps1`: deterministic D10 verifier and negative self-tests.
- `services/job-management/migrations/`: Job Management-owned lifecycle, idempotency, execution-attempt and transactional-outbox authority.
- `shared/platform/Monergy.Platform/ReferenceEventTransport.cs`: provider-neutral LOCAL / CI event transport and delivery telemetry.
- `services/audit/migrations/0002_event_inbox.sql`: consumer deduplication separate from immutable Audit evidence.
- `tests/job-management/Monergy.JobManagement.Tests/`: deterministic lifecycle, restart, retry, cancellation, replay, least-privilege and Audit propagation evidence.

D10 does not select a Production broker, hosting provider, identity provider, orchestration platform or monitoring product. The reference transport is not Production messaging evidence; OD-15 remains unresolved and stage gates are unchanged.

## D12 document reprocessing candidate

- `contracts/Monergy.Contracts/D12DocumentReprocessingContracts.cs` and
  `D12ContractCatalog.cs`: additive CID-026/CID-029 types and the exact
  twelve-family direct verification catalog; the D03 catalog is unchanged.
- `contracts/schemas/document-reprocessing.schema.json`: closed version-1.0.0
  request/event schema.
- `services/document-intelligence/Application/DocumentProcessingApplication.cs`:
  owner-controlled intentional reprocessing, current-policy evaluation, exact
  Evidence identity/version checks, immutable predecessor snapshots, and typed
  success/failure terminal intent.
- `services/document-intelligence/Infrastructure/ReferenceDocumentProcessingAdapters.cs`:
  customer-scoped semantic idempotency, attempt identity, stale-attempt
  protection, and atomic in-memory state/outbox behavior for `LOCAL` and
  `CI_EPHEMERAL` only.
- `tests/document-reprocessing/Monergy.DocumentReprocessing.Tests/`: isolated
  executable evidence for D12-T01 through D12-T12.
- `build/governance/d12-scope-lock.json`, `d12-scenario-matrix.json`, and
  `build/verify-document-reprocessing.ps1`: candidate scope and deterministic
  positive/negative governance checks.

D12 adds no frontend, migration, database, provider, OCR/AI integration,
production transport, deployment claim, or D11 source dependency. At D12
acceptance time, D11 remained frozen with acceptance deferred and its recorded
security failure retained. Current governance records D11 as Accepted / Complete
after post-D12 reconciliation; this does not rewrite the D12-era evidence
statement.

## D13 provider-neutral Customer & Identity candidate

- `contracts/Monergy.Contracts/D13CustomerIdentityContracts.cs` and
  `D13ContractCatalog.cs`: additive CID-001 through CID-004 canonical DTOs and
  exact four-family catalog; the accepted D03 catalog remains isolated.
- `services/customer-identity/Application/`: provider port, authoritative
  Customer/KYC repository port, typed idempotency identities, safe telemetry,
  fail-closed owner application, and optimistic Customer revision semantics.
- `services/customer-identity/Infrastructure/ReferenceCustomerIdentityAdapters.cs`:
  deterministic guarded authentication adapter plus immutable owner-local
  in-memory Customer and KYC history for `LOCAL` / `CI_EPHEMERAL` only.
- `tests/customer-identity/Monergy.CustomerIdentity.Tests/`: executable D13-T01
  through D13-T09 behavior and structural evidence; D13-T10 is retained
  repository regression and quality evidence.
- `build/governance/d13-scope-lock.json`, `d13-scenario-matrix.json`,
  `build/verify-customer-identity-boundary.ps1`, and
  `build/d13/Get-D13SourceIdentity.ps1`: exact scope, negative self-tests, and
  reproducible `StringComparer.Ordinal` candidate identity.

D13 adds no physical Customer & Identity persistence, Production provider,
session lifecycle, CID-005/CID-006 event realization, CID-007 semantic change,
frontend, deployment, D11/D12 source change, or architecture acceptance claim.

See the catalogs and README files in each responsibility area for the maintained
boundary inventory.

`build/governance/github-free-governance-exception.md` records the accepted D01
compensating controls and expiry conditions for the private GitHub Free
repository.
