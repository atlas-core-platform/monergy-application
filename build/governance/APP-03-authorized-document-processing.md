# APP-03 — Authorized document processing and source-fact review

## Decision

APP-03 connects the accepted APP-02 Evidence library to the existing Job Management and Document Intelligence owners for the LOCAL/UAT candidate. A customer may deliberately schedule processing for an exact, verified text evidence version and review candidate source facts with lineage.

This slice does not implement OCR, a production AI provider, financial advice, or financial-profile mutation.

## Owned path

1. Customer Workspace resolves the authenticated customer and obtains a current `document-intelligence.document.process` decision from Access Management.
2. The gateway resolves the selected version from the Evidence owner; the browser cannot supply a customer, evidence identity, or source reference.
3. Job Management accepts an idempotent, customer-scoped durable job and re-evaluates authority at execution time.
4. Document Intelligence independently re-evaluates execution authority and requests the exact immutable content version from Evidence over a separate workload credential.
5. The reference extractor accepts structured UTF-8 text only and emits candidate income/expense facts with source location, confidence, evidence/version identity, and extractor/validator versions.
6. The product reads job and processing status through the same owner chain. Raw content and content references are not returned to the browser.
7. Job Management delivers CID-058/CID-059/CID-060 outbox events to the tenant-bound Audit owner and marks them dispatched only after validating the canonical receipt.

Access Management remains the policy owner. Customer self-access requires ownership and the processing capability; it does not fabricate self-consent. Advisor execution remains governed by the existing relationship-and-Consent evaluation and is not exposed by this customer UI slice.

## Security and isolation

- The protected shell and existing session validation remain unchanged.
- Browser input is limited to an opaque operation ID and a route document-version ID. Customer and evidence bindings are server-resolved.
- Gateway, Job Management, Document Intelligence, Evidence, and Job-to-Audit delivery use distinct LOCAL/UAT workload credentials.
- Tenant identity is bound to the trusted workload identity and checked at every internal hop.
- Job Management and Document Intelligence each fail closed when current authorization cannot be confirmed.
- Evidence resolves the exact customer-owned version and verifies the stored source hash before extraction.
- Idempotent replay preserves the first request time and rejects changed intent.
- Tenant Administrator, cross-customer, unsupported-content, unauthorized, missing-source, and owner-unavailable paths fail closed.

## User experience

Processing is a progressive action within Evidence, not a new navigation domain. The provenance drawer exposes **Review processing** only for supported evidence and authorized users. A separate review drawer explains the execution boundary before scheduling, presents owner progress, and labels all extracted values as non-authoritative candidates.

The UI is keyboard operable, responsive, compatible with reduced motion, and does not imply that a candidate changed the financial profile.

## LOCAL/UAT composition

The local profile upgrades through the Access Management administration API by retaining the existing `evidence-read` permission and adding a narrowly scoped `document-processing` permission to the default customer role. Existing tenant policy is not replaced. Runtime content, jobs, processing records, and facts remain ephemeral.

Document Intelligence and Job Management retain their asynchronous domain responsibilities but use ASP.NET Core Web hosts for the authenticated workload-only execute and status control planes required by this integrated slice. This is a hosting-shape change, not an ownership transfer; neither service exposes these controls directly to the browser.

The Docker acceptance proves:

- authentication and capability denial;
- customer and evidence isolation;
- scheduling and idempotent replay;
- execution-time reauthorization by both owners;
- exact Evidence version resolution;
- successful structured-text extraction with lineage;
- status visibility and response minimization;
- canonical tenant-bound Job Audit delivery with duplicate-safe owner receipts;
- stable component and browser journeys.

Hosted acceptance uses the single manually dispatched **Candidate readiness** workflow. It evaluates the complete branch delta from the current `main` merge base and selects the owner-integration and Docker UAT lanes for this candidate. After merge, only lightweight candidate-identity integrity runs; the application suite is not duplicated.

## Explicit exclusions

- Production deployment or production acceptance.
- OCR, image/PDF extraction, or a production AI/model provider.
- Automatic normalization or financial-profile updates.
- Advice, recommendations, or inferred financial truth.
- Advisor-facing processing UI.
- Durable business storage, production queues, HA/DR, or managed workload identity.

Those remain separately governed deliverables.
