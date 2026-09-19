# Build and Delivery Toolchain

MWP-03-D02 retains the D01 compatibility task while adding the approved pinned
.NET, Node, frontend, test, packaging and supply-chain toolchain.

`Invoke-Toolchain.ps1` exposes locked restore/install, format verification,
lint/type checking, build, unit/component/architecture/browser tests, independent
OCI packaging, SBOM, vulnerability and secret scans, candidate release-manifest
generation, and D01/D02 verification. `Invoke-Bootstrap.ps1` is a compatibility
facade for accepted D01 tasks.

D03 and D04 add their independent deterministic verifier tasks. D04 reads
`governance/d04-scope-lock.json`, checks exact Feature/contract/control IDs,
provider neutrality, reference-adapter restrictions, runtime realization,
permanent tests and truthful candidate lifecycle. Both verifiers include
negative self-tests.

`hosted-oci-evidence.ps1` is the non-publishing Linux evidence path. For each
of the twelve service artifacts it creates a Docker archive, records the
content-addressed image identity and archive hash, generates a CycloneDX image
SBOM, performs a high-severity fail-closed Grype scan, and writes a deterministic
12-row matrix consumed by the candidate Product Release Manifest.

Generated evidence is local and ignored. The candidate Product Release Manifest
is not a release. Packaging requires a Linux Docker engine and reports `BLOCKED`
when it is unavailable; no artifact is silently represented as built or scanned.
