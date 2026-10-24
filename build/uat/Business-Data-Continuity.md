# UAT continuity — Evidence and Financial Profile are separate owner stores

**Scope:** Development LOCAL/CI only. This is an opt-in test configuration, not a Production storage decision and not a migration of records created in the temporary reference stores.

## Cause of the ID-A03 restart failure (24 October 2026)

The retained onboarding (`t105`) Compose project has four named volumes: PostgreSQL tenant/control data, Drive, onboarding configuration and ClamAV signatures. The default launcher sets `Monergy:ReferenceAdapters=true` for **Evidence** and **Financial Profile**. Those services register `InMemoryEvidenceRepository`, `ReferenceEvidenceContentStore` and `InMemoryFinancialProfileRepository`. They store customer Evidence and reviewed facts in the service process. Docker recreating those processes necessarily loses their temporary state even while the four named volumes survive.

Therefore: `/health/ready=200`, healthy PostgreSQL and retained named volumes **do not prove retention** of Evidence or Financial Profile. The lost salary-slip document/fact is consistent with the configured ephemeral reference adapters; it is *not evidence that a retained SQL row or volume was erased*.

## Opt-in retained two-owner mode

An operator can provision **both** supported physical owner stores on a **new named onboarding profile**:

1. Evidence: prepare each tenant's migrated, least-privilege Evidence PostgreSQL database and a versioned S3-compatible bucket, as documented in `build/uat/Evidence-Persistence.md`. Store private Evidence settings outside the repository and pass `-EvidencePersistenceConfigurationFile`.
2. Financial Profile: prepare a migrated, least-privilege PostgreSQL Financial Profile database for the existing `PostgresFinancialProfileRepository` using its existing migrations. Store the runtime connection in a private JSON document matching this **shape**:

```json
{ "RuntimeConnection": "Host=YOUR_DB;Database=YOUR_FINANCIAL_PROFILE_DATABASE;Username=YOUR_RUNTIME_USER;Password=YOUR_SECRET" }
```

These are placeholders, **not** functioning credentials or a migration instruction. Use only a database that has been explicitly provisioned for the LOCAL UAT profile, not the retained business/control database without its own approved schema/grants. The existing financial repository uses a single configured runtime database, so a full multitenant/multiregion production-isolation design is still separate.

3. Prepare a fresh named profile using the two protected private files and the guard:

```powershell
.\build\uat\Invoke-MonergyUat.ps1 -Action Prepare -Profile retained-test -Port 4191 -EnableOnboarding `
  -EvidencePersistenceConfigurationFile E:\Private\evidence-storage.json `
  -FinancialPersistenceConfigurationFile E:\Private\financial-profile-storage.json `
  -RequireRetainedEvidenceAndFinancialProfile
```

4. Run Start and Verify with the *same* named profile and guard after provisioning and checking owner readiness:

```powershell
.\build\uat\Invoke-MonergyUat.ps1 -Action Start -Profile retained-test -RequireRetainedEvidenceAndFinancialProfile
.\build\uat\Invoke-MonergyUat.ps1 -Action Verify -Profile retained-test -RequireRetainedEvidenceAndFinancialProfile
```

The launcher stores selected configurations in the protected profile directory, turns off reference adapters for the corresponding services and selects the already-implemented physical adapters. A later Start without the file arguments retains the protected selection. The `-RequireRetainedEvidenceAndFinancialProfile` switch checks **both** owners are selected, and Verify also validates effective service configuration. It **does not** test contents or automatically provision SQL/S3. Such live proof remains an explicit UAT gate.

**Important:** Do not add this option to the current `t105` profile. Switching an existing temporary Financial Profile in place is rejected because its historical in-process records cannot be migrated. Do not copy the current profile's credentials into a new one or rebind an existing database/volume to another instance. The existing `t105` data and its four named volumes remain unchanged.

## Required acceptance

- Run `build/uat/Test-LegacyProfiles.ps1` (no actual Docker required) to verify temporary-mode fail-closed, rejection of in-place conversions, correct protected physical configuration and subsequent non-fallback.
- Run both owner migrations and least-privilege checks against explicitly provided backing stores; confirm Evidence scanner/metadata and bucket versioning. Confirm Financial Profile repository can write/read through its runtime grant.
- In the **new** profile, perform genuine customer upload → ClamAV → OCR → explicit reviewed profile promotion. Record exact customer, document version, SHA-256, financial fact ID/revision and source.
- Recreate only the applicable Docker service containers (without `down -v`, volume removal or reset). Verify the *same* Evidence bytes/version and *same* financial fact/revision are retrievable, plus Advisor read under current permissions, relationship and newly granted customer Consent.
- Test expiry/revocation/tenant/customer mismatch and no financial disclosure; check 13/13 aggregate readiness and ClamAV active.
- Preserve the current `t105` as a **temporary** profile. Any new durable-profile test fixtures must be clearly synthetic and recorded as test business history. Review/audit acceptance remains separate.

**Not addressed by this bounded opt-in:** Job Management, Document Intelligence and other reference adapters may remain ephemeral; this is **not** full end-to-end processing/history durability. ID-A03 full continuity acceptance must not be claimed until those owner boundaries are separately audited or accepted within an expressly narrower restart test.
