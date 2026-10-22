# T1-110 — Durable scanned Evidence intake

Date: 2026-10-22. Connects product upload/provider import and original-byte processing reads to the existing physical Evidence boundary. P1-05/08/09 remain Partial; this closes a durable-originals integration gap, not complete file-layout coverage or the downstream durable pipeline.

## Delivered behavior

An operator-configured onboarding profile can use a distinct PostgreSQL Evidence database per tenant and a versioned S3-compatible bucket. Existing upload, vault, replacement, monitoring and current tenant/customer/capability/Consent checks remain. Missing/foreign tenant bindings reject before database access. Two tenant bindings cannot use the same host/port/database. Product persistence requires verified scan evidence even for legacy content references; engineering D09 behavior remains separate.

The scanner admits only supported bounded original bytes. S3 conditional creation stores originals unchanged once per operation. A separate conditional scan receipt binds the exact object version, SHA-256, content length/type, fresh malware metadata and provider source location. Retry cannot rebind bytes/type/source; an interrupted receipt write can recover without rewriting the original. Replayed scanning may refresh the receipt. Registration records the original version and scan proof; processing reads that registered exact version and verifies length/hash, without fallback to a newer object. Missing/corrupt/malformed storage fails closed.

Browser and Google/OneDrive callers accept only their exact operation reference, in temporary or durable form. Existing callers and temporary profiles remain supported. No document or financial ownership authority is moved to storage. No UI/package/schema/migration change.

## Configuration and readiness

`-EvidencePersistenceConfigurationFile` accepts RuntimeConnections by tenant plus S3 Endpoint/Bucket/AccessKey/SecretKey. The private protected profile retains this selection across later starts. No credential/policy is invented, printed or placed in browser configuration. Runtime readiness checks all configured Evidence schemas and bucket versioning alongside the existing scanner check. Runtime does not create databases/buckets or change bucket versioning/policies. Existing Evidence migrations/grants apply to each configured tenant database. See [configuration](../uat/Evidence-Persistence.md).

This opt-in does not migrate existing temporary originals or enable production. Document Intelligence, Financial Profile and downstream product persistence remain separate work. Operator storage provisioning, lifecycle/retention/key custody and live validation remain open.

## Verification

745 backend checks pass:594 Evidence/VS02 non-physical/non-native-OCR and151 Integration Gateway/AI. New checks cover restart/replay retention, exact-version reads despite a newer object, source/content conflicts, lost receipt recovery, disabled versioning, invalid scans, corruption/missing data, conditional conflicts, actual intake/register/processing HTTP with authority/protocol doubles, exact operation references and tenant routing denial. Gateway builds cleanly and starts in both product modes. PowerShell legacy/default/onboarding/profile migration and durable opt-in/repeated-start checks pass with Docker/readiness doubles. No development GitHub Actions.

Live S3/PostgreSQL, actual migrations/grants/restart, live ClamAV/native OCR and full authenticated UAT are not claimed. Local physical-fixture setup could not be completed: no Docker/PostgreSQL executable; package installation could not initialize in the execution environment; the public object-store binary URL returned unavailable. This does not prevent implementation-level verification above.

## Tracker

17 Complete,12 Partial,0 Pending; after paired merges110 numbered units =109 implementations+1 governance closure. Preserve original151 IDs,81 features,126 Phase1 requirements, acceptance through T1-06 and approvedForExpertIngestion=false. External inputs remain operator-configured during onboarding; no new product decision is required for this unit.
