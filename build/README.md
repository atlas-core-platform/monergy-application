# Build and Delivery Toolchain

MWP-03-D02 retains the D01 compatibility task while adding the approved pinned
.NET, Node, frontend, test, packaging and supply-chain toolchain.

`Invoke-Toolchain.ps1` exposes locked restore/install, format verification,
lint/type checking, build, unit/component/architecture/browser tests, independent
OCI packaging, SBOM, vulnerability and secret scans, candidate release-manifest
generation, and D01-D16 verifier tasks. `Invoke-Bootstrap.ps1` is a compatibility
facade for accepted D01 tasks. `ci/Get-CiImpact.ps1` and
`ci/Invoke-ImpactedVerification.ps1` provide the deterministic D15 selection and
focused execution boundary used by hosted CI.

D03 and D04 add their independent deterministic verifier tasks. D04 reads
`governance/d04-scope-lock.json`, checks exact Feature/contract/control IDs,
provider neutrality, reference-adapter restrictions, runtime realization,
permanent tests and the accepted SIMULATOR lifecycle. Both verifiers include
negative self-tests. `governance/d04-acceptance.json` pins the approved
candidate, hosted evidence, actual security findings and exact D03/D04 contract
overlap without claiming publication or deployment.

`hosted-oci-evidence.ps1` is the non-publishing Linux evidence path. It accepts
an explicit impacted service set, while an omitted set retains the complete
twelve-image capability. For every selected artifact it creates a Docker
archive, records the content-addressed image identity and archive hash,
generates a CycloneDX image SBOM, performs a high-severity fail-closed Grype
scan, and writes a deterministic evidence matrix.

Generated evidence is local and ignored. The candidate Product Release Manifest
is not a release. Packaging requires a Linux Docker engine and reports `BLOCKED`
when it is unavailable; no artifact is silently represented as built or scanned.

D16 adds a provider-neutral immutable capsule for an already-built application
artifact and a deterministic LOCAL/CI reference-promotion record. Exact source,
artifact digest, CycloneDX SBOM, passing vulnerability evidence and generated
build provenance are hash-linked. Promotion validates and references the same
payload without invoking a build, while versioned non-secret configuration stays
a separate reference. The mechanism cannot publish, deploy or target Production.
