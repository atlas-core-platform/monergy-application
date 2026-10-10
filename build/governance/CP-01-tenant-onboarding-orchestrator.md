# CP-01 — Tenant Onboarding Orchestrator

Status: Candidate  
Authority: AR-002  
Source application main: `75d98f7d7e78efcbb4cd6c1544667b23206e0ddc`

## Purpose

Implement the first governed Platform Control Plane tenant-onboarding state machine without creating a fourteenth business service or selecting a Production cloud/database provider.

## Scope

- Durable LOCAL/CI Tenant Registry adapter with atomic file replacement.
- Idempotent onboarding request identity.
- Canonical lifecycle: REQUESTED → PROVISIONING → READY_FOR_ADMIN → ACTIVE.
- Exceptional PROVISIONING_FAILED behavior.
- Six ordered provisioning steps with stable operation IDs.
- Immutable successful step receipts.
- Resume from the first incomplete step.
- Activation blocked until every mandatory receipt exists.
- Activation also requires a tenant-matched setup receipt from the server-side `ITenantSetupReadiness` port; no configured adapter means activation is denied.
- Registry writes enforce immutable identity, ordered append-only receipts, allowed transitions, and optimistic version checks under a cross-instance file lease.
- Placement mode is metadata only; business services do not branch on shared/dedicated placement.

## Mandatory provisioning steps

1. PlacementResolved
2. ServiceDatabasesProvisioned
3. ServiceMigrationsApplied
4. InitialAdministratorIdentityProvisioned
5. AccessManagementBootstrapped
6. MandatoryReadinessValidated

## Failure and replay

A failed step records a bounded failure code and leaves prior successful receipts intact. Retrying the tenant skips completed steps. Replaying the original onboarding request with identical semantic content returns the same Tenant. Reusing the request ID with changed semantic content fails.

## Explicit non-goals

- No Production registry database selection.
- No AWS/Azure/GCP implementation.
- No secret values in the Tenant Registry.
- No direct cross-service SQL.
- No Production identity provider or invitation flow.
- No actual service-database provisioning adapter in CP-01.
- No Automatic transition to ACTIVE before Tenant Admin setup.
- No stage-gate advancement.

## Continuation integration boundary

The setup port is a guardrail, not a completed first-login journey. Its production and LOCAL/UAT adapters are not supplied by CP-01. The next implementation model and its missing Consent dependency are recorded in [Customer relationship and onboarding plan](Customer-relationship-onboarding-plan.md).

Local continuation evidence: build passes with analyzers enabled and 16 architecture tests pass, including setup/tenant rejection, immutable receipt/identity enforcement, cross-instance concurrency, and stale writes. Raw output: [CP-01 continuation evidence](evidence/cp01-continuation-20261010.txt). No provisioning, setup UI, or integrated Docker acceptance is claimed.
