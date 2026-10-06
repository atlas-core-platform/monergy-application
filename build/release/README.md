# Immutable artifact and reference promotion

The D16 flow registers an already-built application artifact once, copies it
into a digest-keyed local/CI capsule, and promotes only the verified capsule
identity. It does not run an application build, publish an artifact, deploy an
environment, or implement Production promotion.

## Create and verify a candidate

The payload, CycloneDX SBOM and passing vulnerability record must already exist.
Supply the exact source commit and Git tree that produced the payload, plus the
trusted build-pipeline and invocation identities:

```powershell
$manifest = ./build/release/New-ImmutableArtifact.ps1 `
  -ReleaseIdentity 'release-2026.10.06-01' `
  -Component 'monergy-reporting' `
  -Version '2026.10.6+build.1' `
  -SourceRevision '<40-character-commit>' `
  -SourceTree '<40-character-tree>' `
  -PipelineIdentity '<trusted-pipeline-identity>' `
  -BuildInvocationIdentity '<immutable-build-run-identity>' `
  -BuiltAtUtc '2026-10-06T09:00:00Z' `
  -ArtifactPath '<already-built-artifact-file>' `
  -SbomPath '<cyclonedx-sbom-file>' `
  -VulnerabilityEvidencePath '<passing-vulnerability-record>'

./build/release/Test-ImmutableArtifact.ps1 -ManifestPath $manifest
```

The capsule is stored under `.artifacts/d16/candidates` by release, component
and SHA-256 digest. Repeating the exact registration is idempotent. A collision
with different source, build or evidence metadata fails closed and is never
overwritten. Candidate creation resolves the supplied revision as a commit in
the local `monergy-application` repository and requires its exact resolved Git
tree to equal the supplied tree; neither identity is inferred or replaced.

## Create and verify a reference promotion

Configuration is supplied as a separate versioned reference. The executable
zone is limited to `LOCAL` or `CI_EPHEMERAL`; target references are limited to
`DEV`, `QA` or `UAT` and do not claim deployment success:

```powershell
$promotion = ./build/release/Invoke-ReferencePromotion.ps1 `
  -CandidateManifestPath $manifest `
  -ExecutionZone 'CI_EPHEMERAL' `
  -TargetEnvironmentReference 'QA' `
  -ConfigurationReference 'configuration/qa' `
  -ConfigurationRevision '<immutable-configuration-revision>'

./build/release/Test-ReferencePromotion.ps1 `
  -PromotionPath $promotion `
  -CandidateManifestPath $manifest
```

The promotion identity is deterministic over the candidate-manifest hash,
artifact digest, execution zone, target reference and configuration reference.
The promotion record retains exact source, SBOM, vulnerability and provenance
hashes and records `rebuildInvoked`, publication, deployment and environment
success as false.
