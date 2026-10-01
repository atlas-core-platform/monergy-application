# Persisted reporting LOCAL profile

`Invoke-MonergyLocal.ps1` owns the bounded `persisted-reporting` developer profile introduced by MWP-03-D11.
It is synthetic LOCAL/CI evidence, not a shared environment or Production deployment.

Run from the repository root:

```powershell
./build/local/Invoke-MonergyLocal.ps1 -Action Prepare -Profile persisted-reporting
./build/local/Invoke-MonergyLocal.ps1 -Action Start -Profile persisted-reporting
./build/local/Invoke-MonergyLocal.ps1 -Action Seed -Profile persisted-reporting
./build/local/Invoke-MonergyLocal.ps1 -Action Verify -Profile persisted-reporting
./build/local/Invoke-MonergyLocal.ps1 -Action Status -Profile persisted-reporting -Json
./build/local/Invoke-MonergyLocal.ps1 -Action Stop -Profile persisted-reporting
```

`Prepare` is the only action that restores dependencies, provisions the distinct D11 stores, runs service-owned
migrations and builds the application. Bootstrap, migration and runtime children receive explicit allowlisted
environments: a migrator receives only its owner connection through `MONERGY_MIGRATION_CONNECTION`, each service
receives only its own runtime credentials, and Customer Web receives no database, object-store or Audit secret.
`Start` never migrates or seeds and rejects a stale source or Release-build identity. `Seed` is explicit,
repeatable and uses two named synthetic customers through Evidence, Financial Profile and Financial Rules owner
operations. `Stop` acts only on journal entries whose PID, executable and precise start identity still match. It
requests authenticated graceful shutdown before a bounded forced fallback. PostgreSQL/S3 providers and both
retained volumes remain available; stopped applications are reported separately from retained providers.

The first run is `Prepare` → `Start` → `Seed` → `Verify`. Later sessions normally need only `Start` and `Stop`.
The `/reports` experience is available at <http://127.0.0.1:5173/reports> while running.

State and per-user secrets are under the ignored `.artifacts/d11/persisted-reporting` directory. The directory is
restricted before secret creation. On Windows secret ACL application is checked for success; on Unix secret files
receive mode `0600`. No credential is written to the committed profile descriptor, normal command result, runtime
URL, report export or browser diagnostics. Ownership journals are written atomically as each process is launched;
unresolved or mismatched identities are retained and block a false `STOPPED` result.

The secret state, PostgreSQL volume and SeaweedFS volume form one retained-state set. A first Prepare is allowed only
when all three are absent; a retained restart requires all three. Any partial combination is reported as
`BLOCKED_RECOVERY_REQUIRED` without generating credentials, creating a missing retained volume or deleting data.
Prepared and runtime evidence use `canonical-file-set-v1`: repository-relative `/` paths, ordinal case-sensitive
ordering, decimal byte lengths, lowercase raw-file SHA-256 values, UTF-8 without BOM and LF-delimited records with a
final LF.

`READY_FOR_PROFILE` means more than live PIDs: the controller verifies exact owned process identities, PostgreSQL,
the object store, both retained volumes, every health endpoint, a bounded owner-contract read and the prepared
source/build identity. Operational HTTP calls are bounded and failures are classified. `Verify` requires an exact
cross-customer `403`/`AccessDenied`, hashes the returned export bytes and waits for the matching Reporting event in
Audit; a timeout is reported as unknown/degraded rather than delivered.

Focused controller and transport evidence can be produced with:

```powershell
./build/local/Test-MonergyLocalProcessIsolation.ps1
./build/local/Test-MonergyLocalPreparedIdentity.ps1
./build/local/Test-MonergyLocalRecovery.ps1
./build/local/Invoke-D11RuntimeEvidence.ps1 -Profile persisted-reporting
```

The runtime evidence command writes reviewed, non-secret JSON beneath
`.artifacts/d11/remediation-r2/<platform>/`. Hosted Linux execution is wired in the D11 workflow lane, including
finally-owned cleanup and an explicit safe artifact allowlist; it remains `NOT_RUN` until a separately authorized
commit/push/PR triggers that lane.

The profile binds only to loopback and comprises Customer Web, Evidence, Financial Profile, Financial Rules,
Reporting, and a LOCAL-only Audit composition host. It does not run Customer Identity, Consent, AI, Document
Intelligence, Job Management or Search. Security grants, fixture consent and HTTP event transport are reference
controls limited to LOCAL/CI_EPHEMERAL. No Production provider or deployment claim is made.
