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
