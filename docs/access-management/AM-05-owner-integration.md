# AM-05 — Tenant-aware Customer & Identity and Audit integration

## Authority and scope

This governed integration implements AR-001 at Architecture main `d4d2653ad97808872357865fca4a486611d40fd4`, following Application main `45ccd9ebd1f693ca07a3976f3094b87604f6a0b7` and Access Management AM-04 main `b8eada015d05a300d0d2cfca57b44f4c6b2a0b22`. User authorization covers implementation, PR pushes and verified merges. D01–D16 remain historically accepted at their recorded baselines; no stage gate advances and this is not D17 or complete service acceptance.

The actual Customer & Identity service now owns tenant-scoped durable sessions and AM invalidation consumption. The actual Audit service records CID-070 in its canonical evidence ledger. Access Management calls C&I for canonical session context; C&I reads current membership through AM's workload-authenticated HTTP port. Neither service accesses the other's database. This is real cross-service LOCAL/CI integration with synthetic authentication references, not a Production identity provider or deployment.

## Contracts and current-state checks

`TenantAccessContracts.cs` adds a tenant-aware CID-007 session projection and CID-068/069/070 event/receipt types. Legacy customer-scoped D13/D14 envelopes and behavior remain available in their original reference composition. A legacy context cannot substitute for the tenant-aware path. Tenant is never inferred from CustomerId or email. The AM-04 envelope and language-independent hash are preserved, with the same independently computed test vector.

C&I authenticates a tenant-bound reference through trusted operator configuration, reads active membership/current subject version from AM, creates a session in its own tenant database, and confirms membership again before returning it. Session tokens use 256 random bits and only SHA-256 hashes are stored. They are not credentials managed by Access Management. Each validation checks expiry, irreversible revocation, subject watermark and current AM membership/version; dependency failure produces no trusted context. Every protected AM request still evaluates its own current policy and administrative authority. Tenant Admin has no implicit business-data access.

Cross-service atomicity is not claimed. The post-creation membership check narrows the issuance race; mandatory fresh validation and AM authorization govern subsequent requests. Events are an additional durable invalidation path, not an ALLOW cache. No downstream stale-ALLOW fallback or lease is introduced.

## Durable owner effects

C&I uses a tenant-specific database with an immutable tenant/schema binding. CID-068/069 authorization deliveries advance monotonic policy/subject watermarks. CID-069 session deliveries revoke only sessions of the same tenant/actor established under a lower subject version. An older event cannot roll back a watermark, revoke a newer session, or reverse revocation. A database trigger forbids clearing revocation. Session identity, subject version and expiry cannot be updated by runtime credentials. An inbox receipt and its effects commit in one transaction.

Audit accepts CID-070 only through the tenant-aware typed port, extending CID-061 without allowing the legacy flat envelope to discard tenant or initiator identity. The canonical `audit.evidence` row, legacy inbox, complete bounded access-event envelope and bound receipt commit together. The source namespace in the canonical ledger is `am-<source UUID>`; the original UUID remains in the access envelope/receipt. Duplicate content returns the original evidence reference; altered content under the same event identity conflicts. Receipt failure rolls back all evidence. Runtime cannot update or delete evidence or receipts.

## Migration and credentials

`customer-identity/migrations/0001_tenant_access_sessions.sql` is C&I-owned. Audit adds `0003_tenant_access_evidence.sql`; existing Audit migrations are unchanged. Binding rows are installed by owner provisioning, never accepted from a request. D09/D10 Audit composition remains compatible without a tenant binding; the new typed consumer requires it. Migration tooling admits C&I as a new AR-001 cohort. Architecture tests enforce its owner boundary; D09/D13/D14 forward checks explicitly recognize the single additive C&I migration while preserving historical source checks.

CI provisions two C&I databases and two Audit databases, separate from the two AM tenant databases. Each runtime has tenant/database-scoped credentials, no DDL, no cross-database connection permission, and no owner credentials. Test orchestration alone holds owner credentials. Each service process receives only its own runtime bindings and required peer workload tokens. Workload tokens are separate for membership reads, C&I context validation, C&I events and Audit events; they are never caller-selected destinations or query-string credentials.

## Composition and verification

Owner integration requires `Monergy:AccessIntegration:Enabled=true`, `Monergy:ExecutionZone=LOCAL` or `CI_EPHEMERAL`, and Development HTTP hosts. It is exclusive with old reference/physical provider selections. All peer HTTP roots are loopback-only, with bounded request/response size, five-second client timeout, redirects/proxy disabled and sanitized dependency errors. The Audit LOCAL host wraps the Audit-owned application/repository; C&I runs its actual service host. No real account, identity provider, cloud resource or Production workload is created.

The dedicated workflow runs protocol/application and PostgreSQL failure tests, then starts actual AM, C&I and Audit processes. It verifies canonical-actor enforcement, no legacy-header fallback, real AM ALLOW/DENY, disablement before event delivery, tenant isolation, durable invalidation, canonical Audit evidence, lost acknowledgement/retry, duplicate/conflicting receipt handling, owner outages, process restart and Production rejection. Existing impact-aware platform gates run as well.

For reproducible private cross-repository verification, `build/am05/fixtures` pins a credential-free AM source snapshot from an exact verified Git commit. Its archive SHA-256 and every Git blob hash are checked before extraction into ignored test artifacts. It is a verification dependency only; no AM domain authority is copied into Application runtime projects. Its manifest records the exact source and passing source run. Updating that dependency requires a reviewed source/manifest change. No cross-repository PAT or Production credential is introduced.

## Remaining boundaries

Authentication is still a deterministic LOCAL/CI reference, not a selected Production IdP/MFA implementation. Identity creation/import provisioning remains the AM-03 reference port. Customer/business resource bindings, Consent and other protected services have not yet adopted this tenant session projection. Admin UI, Control Plane routing, Production workload identity/transport/deployment, capacity and recovery acceptance remain pending. System Expert acceptance and SG-02/03/04 status are not advanced by these merges.
