# Docker UAT #23: Consent row mapping correction

Date: 2026-10-10 UTC. Application PR #43, bounded LOCAL/UAT scope.

## Hosted evidence

[Docker UAT #23](https://github.com/atlas-core-platform/monergy-application/actions/runs/38069669340)
tested `b8eb94bf70541086d755fce7ae89201e26d8b3b0`.

- Windows profile checks passed.
- The non-root image-build storage probe passed with directory mode `1777`.
- Bootstrap completed successfully, including mounted-volume probes and owner
  initialization. Repeated bootstrap during restart checks also succeeded.
- Existing owner/process acceptance passed **128 checks across 13 boundaries**.
- The new onboarding suite failed on the first Consent list request with HTTP 500. Browser acceptance was therefore skipped.

The Consent log identified the failure in `PostgresCustomerConsent.ListAsync`:
Dapper could not find a matching `GrantRow` constructor for the reader's column
types, including `System.Array capabilityids`. The positional row constructor
expected `string[]`. The error occurs while constructing the parser even when
the list is empty.

## Correction

The internal database row now uses a parameterless class with typed properties,
following the existing repository adapters. This allows Dapper to assign the
actual `string[]` value without requiring an exact constructor match to the
provider's `System.Array` column metadata. Public Consent DTOs, timestamps,
database schema, queries, authorization, transaction boundaries and package pins
remain unchanged.

The row type is internal and visible to the existing owner test assembly only
through `InternalsVisibleTo`; no product endpoint or public model is added.

The Docker acceptance script now explicitly checks empty Consent lists and
readback after grant/revocation, including identity, capability list, expiry,
version and nullable revocation time.

## Local regression evidence

Three new tests use the real pinned Dapper parser and an in-memory reader with
the exact column names/types reported by Npgsql in #23. Before changing the row
mapping, all three reproduced the same constructor exception as the hosted run.
After the correction, all three passed within the **46/46** passing non-physical
owner integration tests.

| Case                                         | Before                | After |
| -------------------------------------------- | --------------------- | ----- |
| Empty list with `System.Array` metadata      | Constructor exception | Pass  |
| Active grant, capabilities and UTC expiry    | Constructor exception | Pass  |
| Revoked grant with populated revocation time | Constructor exception | Pass  |

Command: `dotnet test tests/persistence/Monergy.AccessIntegration.Tests -c Release -p:RestoreLockedMode=true -m:1 -p:UseSharedCompilation=false --filter 'Category!=Physical'`.
The tests built with analyzers enabled. Changed C# whitespace, Python syntax and
diff whitespace checks passed. These reader-level tests do not claim a local
PostgreSQL or Docker run; this environment has no Docker engine.

## Remaining gate

Run a new Docker UAT workflow on `delivery/onboarding-integrated-uat` after this
correction. Re-running #23 would retain the old commit. Full onboarding and
browser acceptance remain unproven until the new hosted run succeeds. Continuous
verification #145 is green on the earlier `11cf037` candidate; final verification
must cover the eventual accepted commit. No merge or Production release is
performed by this correction.
