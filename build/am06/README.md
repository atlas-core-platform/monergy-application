# AM-06 — Durable tenant identity onboarding

This LOCAL/CI increment follows Application main
`375e6058b0f488f74d4a0924feae4f70f6fe10fb` and AR-001. It completes the identity
owner port used by staged Access Management imports. D01–D16 acceptance history
and stage-gate decisions remain unchanged.

## Identity ownership and atomicity

C&I exposes `POST /internal/v1/tenant-identities/provision` only in the selected
Development LOCAL/CI owner composition and only when a separate
`IdentityProvisioningToken` is configured. Session-context and event credentials
cannot substitute for this token. The trusted tenant header must match the
request. Requests reject extra fields, invalid or non-normalized emails,
out-of-range rows, and invalid identifiers/keys.

The C&I-owned additive migration `0002_tenant_identity_provisioning.sql` creates
tenant principals, a provisioning schema marker and immutable operation
receipts. One transaction creates or reuses a tenant-local email identity and
inserts the receipt. The database enforces unique normalized email, unique
idempotency key, and unique import/row. Concurrent/restarted retries return the
same actor. A changed request under the same key or changed key for the same row
conflicts. The same email in another tenant resolves independently.

Runtime credentials can read and insert the required rows but cannot rewrite or
delete identities or receipts, change the schema/tenant anchor, or use another
tenant database. Provisioning uses the existing validated C&I connection boundary;
no service reads another service's SQL. Migration 0001 remains unchanged. A separate
AM-06 hash-locked overlay extends the historical verifiers' additive migration
accounting without weakening their original cohorts or changing AM-05's manifest.

Identity creation grants no AM membership and establishes no authentication or
session. Access Management preserves its full-file validation, current admin and
revision checks, pending intents, receipt persistence and single-transaction batch
activation. Failed/cancelled imports can leave C&I identities; those records confer
no access. Restaging the email safely reuses the same C&I actor.

## Verification

The owner workflow retains AM-05 session/Audit verification and adds PostgreSQL
concurrency, tenant isolation, key/content conflict, receipt rollback and runtime
privilege tests. Real HTTP processes exercise receipt loss after commit, process
restart, all-or-none activation, replay, wrong credentials/tenant, cancellation,
and safe restaging. A fault flag is honored only in `CI_EPHEMERAL` and drops one
provisioning acknowledgement after the transaction commits.

The existing `build/am05` runner now composes AM-05 and AM-06. Its immutable AM
verification dependency is updated to the tested AM-06 source; archive and per-file
Git hashes still verify before extraction. Existing Production rejection, outbox,
Audit and revocation checks remain mandatory. Automatic CI runs only after merge.

## UI direction and remaining scope

The platform owner requested a single Monergy workspace experience across the
Application, Access Management and System Expert. Access administration belongs
inside the Application shell, using shared tokens/components and keyboard-accessible
drawers, cards, collapsible navigation/panels, file drop zones and reduced-motion
behavior. System Expert remains separate tooling with the same design standards.

Production IdP/MFA, invitation delivery, lifecycle/erasure policy, Production
placement/transport/deployment and broader Consent/business-resource adoption remain
pending. Manual member lookup and initial-admin bootstrap retain their separately
bounded reference ports. The new durable onboarding path uses C&I-owned identities.
