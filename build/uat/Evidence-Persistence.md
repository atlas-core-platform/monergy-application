# Durable Evidence during LOCAL onboarding

The optional durable path retains scanned original bytes in a versioned S3-compatible bucket and Evidence metadata in a separate PostgreSQL database for each configured tenant. Upload, provider import, vault, version replacement, monitoring and processing use the same owner/access boundaries. No production environment is enabled by this LOCAL option.

Supply a private JSON file using `-EvidencePersistenceConfigurationFile`. Its shape is:

```json
{
  "RuntimeConnections": {
    "T001": "Host=host.docker.internal;Port=5432;Database=monergy_evidence_t001;Username=evidence_runtime;Password=YOUR_SECRET",
    "T002": "Host=host.docker.internal;Port=5432;Database=monergy_evidence_t002;Username=evidence_runtime;Password=YOUR_SECRET"
  },
  "S3": {
    "Endpoint": "http://host.docker.internal:9000",
    "Bucket": "monergy-evidence",
    "AccessKey": "YOUR_ACCESS_KEY",
    "SecretKey": "YOUR_SECRET_KEY"
  }
}
```

Use actual operator-provided settings. Connections are resolved from inside the runtime container. Configure only tenants provisioned for this installation; unconfigured tenants fail closed. Two tenants cannot use the same host/port/database. The storage bucket may be shared; opaque operation keys, owner metadata and current tenant/customer checks control access. Storage credentials are server-side only.

Before enabling, provision each tenant's Evidence database using the existing Evidence migrations and least-privilege grants in `services/evidence/migrations` and `build/d09/bootstrap-postgres.sh`, and enable bucket versioning. The runtime checks schema availability and bucket versioning in readiness. It does not create databases, create buckets, change bucket policy/versioning, or migrate existing temporary originals. S3 permissions must allow bucket-versioning reads, object metadata, original/receipt writes and exact-version reads for the configured prefix. Originals use conditional creation; scan receipts use conditional updates. Retention and key custody remain operator-owned decisions.

```powershell
.\build\uat\Invoke-MonergyUat.ps1 -Action Start -Profile durable -EnableOnboarding -EvidencePersistenceConfigurationFile E:\Private\monergy-evidence.json
```

The protected profile retains this configuration for later starts; omission of the flag does not silently switch it back to temporary storage. Existing temporary profiles remain supported. The flag is an explicit storage-mode choice, not a data migration. Do not reset a retained profile to change credentials.

Original bytes are stored unchanged. A separate scan receipt retains SHA-256, original object version, scan metadata and provider location. Rescanning the same operation may refresh the receipt but cannot replace its original bytes or source identity. An interrupted receipt write can be retried. Processing reads the registered exact object version and verifies length/hash; missing or corrupt content never falls back to the latest object. Cloud imports and browser uploads accept only the exact returned operation reference.

Validation in T1-110 uses S3 protocol and owner/authority doubles. Live S3/PostgreSQL, migrations/grants/restart, live ClamAV and complete authenticated UAT are not claimed. Document Intelligence and downstream product adapters are still separate persistence work; durable Evidence alone does not imply an entirely durable processing pipeline.
