Set-StrictMode -Version Latest

function Get-D16Sha256 {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Integrity material '$Path' does not exist or is not a file."
    }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-D16TextSha256 {
    param([Parameter(Mandatory)][string]$Value)

    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value)))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Write-D16Json {
    param(
        [Parameter(Mandatory)][object]$Value,
        [Parameter(Mandatory)][string]$Path
    )

    $parent = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))
}

function Get-D16Property {
    param(
        [Parameter(Mandatory)][object]$Value,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Context
    )

    $property = $Value.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) {
        throw "$Context is missing mandatory '$Name' metadata."
    }
    return $property.Value
}

function Resolve-D16ContainedFile {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$RelativePath,
        [Parameter(Mandatory)][string]$Context
    )

    if ([string]::IsNullOrWhiteSpace($RelativePath) -or
        [IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath.Replace('\', '/') -match '(^|/)\.\.(/|$)') {
        throw "$Context uses an unsafe relative path."
    }

    $resolvedRoot = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $candidate = [IO.Path]::GetFullPath((Join-Path $resolvedRoot $RelativePath))
    $comparison = if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
        [StringComparison]::OrdinalIgnoreCase
    }
    else {
        [StringComparison]::Ordinal
    }
    if (-not $candidate.StartsWith("$resolvedRoot$([IO.Path]::DirectorySeparatorChar)", $comparison)) {
        throw "$Context resolves outside its immutable capsule."
    }
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "$Context material is absent."
    }
    return $candidate
}

function Test-D16StableVersion {
    param([Parameter(Mandatory)][string]$Version)

    $mutableAliases = @('latest', 'main', 'master', 'head', 'dev', 'qa', 'uat', 'production', 'prod')
    return $Version -match '^[0-9A-Za-z][0-9A-Za-z._+-]{0,127}$' -and
        $mutableAliases -cnotcontains $Version.ToLowerInvariant()
}

function Assert-D16EvidenceReference {
    param(
        [Parameter(Mandatory)][object]$Reference,
        [Parameter(Mandatory)][string]$CapsuleRoot,
        [Parameter(Mandatory)][string]$Name
    )

    $relativePath = [string](Get-D16Property $Reference 'path' "$Name evidence reference")
    $expectedHash = [string](Get-D16Property $Reference 'sha256' "$Name evidence reference")
    if ($expectedHash -notmatch '^[0-9a-f]{64}$') {
        throw "$Name evidence has an invalid SHA-256 identity."
    }
    $path = Resolve-D16ContainedFile $CapsuleRoot $relativePath "$Name evidence"
    if ((Get-D16Sha256 $path) -cne $expectedHash) {
        throw "$Name evidence integrity does not match its immutable identity."
    }
    return $path
}

function Assert-D16Candidate {
    param([Parameter(Mandatory)][string]$ManifestPath)

    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        throw "Immutable artifact manifest '$ManifestPath' is absent."
    }
    $manifestPath = [IO.Path]::GetFullPath($ManifestPath)
    $capsuleRoot = Split-Path -Parent $manifestPath
    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding utf8 | ConvertFrom-Json
    }
    catch {
        throw "Immutable artifact manifest is malformed JSON: $($_.Exception.Message)"
    }

    if ((Get-D16Property $manifest 'schemaVersion' 'Artifact manifest') -cne '1.0.0' -or
        (Get-D16Property $manifest 'recordType' 'Artifact manifest') -cne 'IMMUTABLE_ARTIFACT_CANDIDATE' -or
        (Get-D16Property $manifest 'status' 'Artifact manifest') -cne 'CANDIDATE_LOCAL_CI_ONLY') {
        throw 'Artifact manifest schema, record type, or candidate state is not governed.'
    }

    $releaseIdentity = [string](Get-D16Property $manifest 'releaseIdentity' 'Artifact manifest')
    $component = [string](Get-D16Property $manifest 'component' 'Artifact manifest')
    $version = [string](Get-D16Property $manifest 'version' 'Artifact manifest')
    $candidateIdentity = [string](Get-D16Property $manifest 'candidateIdentity' 'Artifact manifest')
    if ($releaseIdentity -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]{0,127}$' -or
        $component -notmatch '^[0-9a-z][0-9a-z.-]{0,127}$' -or
        -not (Test-D16StableVersion $version)) {
        throw 'Artifact release, component, or immutable version identity is invalid.'
    }

    $source = Get-D16Property $manifest 'source' 'Artifact manifest'
    $sourceRevision = [string](Get-D16Property $source 'revision' 'Artifact source')
    $sourceTree = [string](Get-D16Property $source 'tree' 'Artifact source')
    if ($source.repository -cne 'monergy-application' -or
        $sourceRevision -notmatch '^[0-9a-f]{40}$' -or
        $sourceTree -notmatch '^[0-9a-f]{40}$') {
        throw 'Artifact source repository, revision, or tree identity is invalid.'
    }

    $build = Get-D16Property $manifest 'build' 'Artifact manifest'
    if ([string]::IsNullOrWhiteSpace([string](Get-D16Property $build 'pipelineIdentity' 'Artifact build')) -or
        [string]::IsNullOrWhiteSpace([string](Get-D16Property $build 'invocationIdentity' 'Artifact build')) -or
        [string](Get-D16Property $build 'builtAtUtc' 'Artifact build') -notmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z$') {
        throw 'Artifact build provenance metadata is incomplete.'
    }

    $artifact = Get-D16Property $manifest 'artifact' 'Artifact manifest'
    $artifactDigest = [string](Get-D16Property $artifact 'digest' 'Artifact identity')
    if ($artifactDigest -notmatch '^sha256:[0-9a-f]{64}$') {
        throw 'Artifact digest is not an immutable SHA-256 identity.'
    }
    $artifactPath = Resolve-D16ContainedFile $capsuleRoot ([string](Get-D16Property $artifact 'path' 'Artifact identity')) 'Artifact payload'
    $actualArtifactHash = Get-D16Sha256 $artifactPath
    if ($artifactDigest -cne "sha256:$actualArtifactHash") {
        throw 'Artifact payload was altered or its digest is mismatched.'
    }
    if ([long](Get-D16Property $artifact 'sizeBytes' 'Artifact identity') -ne (Get-Item -LiteralPath $artifactPath).Length) {
        throw 'Artifact payload size is mismatched.'
    }
    $expectedCandidateIdentity = "$releaseIdentity/$component@$version#$artifactDigest"
    if ($candidateIdentity -cne $expectedCandidateIdentity) {
        throw 'Candidate identity is not bound to release, component, version, and digest.'
    }

    $evidence = Get-D16Property $manifest 'evidence' 'Artifact manifest'
    $sbomReference = Get-D16Property $evidence 'sbom' 'Artifact evidence'
    $vulnerabilityReference = Get-D16Property $evidence 'vulnerability' 'Artifact evidence'
    $provenanceReference = Get-D16Property $evidence 'provenance' 'Artifact evidence'
    $sbomPath = Assert-D16EvidenceReference $sbomReference $capsuleRoot 'SBOM'
    $vulnerabilityPath = Assert-D16EvidenceReference $vulnerabilityReference $capsuleRoot 'Vulnerability'
    $provenancePath = Assert-D16EvidenceReference $provenanceReference $capsuleRoot 'Provenance'

    try {
        $sbom = Get-Content -LiteralPath $sbomPath -Raw -Encoding utf8 | ConvertFrom-Json
        $vulnerability = Get-Content -LiteralPath $vulnerabilityPath -Raw -Encoding utf8 | ConvertFrom-Json
        $provenance = Get-Content -LiteralPath $provenancePath -Raw -Encoding utf8 | ConvertFrom-Json
    }
    catch {
        throw "Artifact security or provenance evidence is malformed JSON: $($_.Exception.Message)"
    }
    if ($sbom.bomFormat -cne 'CycloneDX' -or [string]::IsNullOrWhiteSpace([string]$sbom.specVersion)) {
        throw 'Artifact SBOM is not identified CycloneDX evidence.'
    }
    if ($vulnerability.result -cne 'PASS') {
        throw 'Artifact vulnerability evidence is absent or not passing.'
    }
    if ($provenance.schemaVersion -cne '1.0.0' -or
        $provenance.predicateType -cne 'MONERGY_D16_BUILD_PROVENANCE' -or
        $provenance.candidateIdentity -cne $candidateIdentity -or
        $provenance.subject.component -cne $component -or
        $provenance.subject.version -cne $version -or
        $provenance.subject.digest -cne $artifactDigest -or
        $provenance.source.repository -cne $source.repository -or
        $provenance.source.revision -cne $sourceRevision -or
        $provenance.source.tree -cne $sourceTree -or
        $provenance.build.pipelineIdentity -cne $build.pipelineIdentity -or
        $provenance.build.invocationIdentity -cne $build.invocationIdentity -or
        $provenance.build.builtAtUtc -cne $build.builtAtUtc -or
        $provenance.evidence.sbomSha256 -cne $sbomReference.sha256 -or
        $provenance.evidence.vulnerabilitySha256 -cne $vulnerabilityReference.sha256) {
        throw 'Artifact provenance does not match the artifact, source, build, or security evidence identity.'
    }

    $policy = Get-D16Property $manifest 'promotionPolicy' 'Artifact manifest'
    if ($policy.mode -cne 'DRY_RUN_REFERENCE_ONLY' -or
        $policy.authorizationState -cne 'AUTHORIZED_FOR_LOCAL_CI_REFERENCE' -or
        $policy.productionAllowed -ne $false) {
        throw 'Artifact is not in the authorized local/CI reference-promotion state.'
    }
    $publication = Get-D16Property $manifest 'publication' 'Artifact manifest'
    if ($publication.published -ne $false -or $publication.deployed -ne $false -or
        (Get-D16Property $manifest 'environmentConfigurationEmbedded' 'Artifact manifest') -ne $false) {
        throw 'Artifact candidate makes a publication, deployment, or embedded-configuration claim.'
    }
    if ($null -ne $manifest.PSObject.Properties['targetEnvironment'] -or
        $null -ne $manifest.PSObject.Properties['configurationReference']) {
        throw 'Environment configuration must not be embedded in artifact identity metadata.'
    }

    return [pscustomobject]@{
        Manifest = $manifest
        ManifestPath = $manifestPath
        ManifestSha256 = Get-D16Sha256 $manifestPath
        ArtifactPath = $artifactPath
        ArtifactDigest = $artifactDigest
        SbomPath = $sbomPath
        VulnerabilityPath = $vulnerabilityPath
        ProvenancePath = $provenancePath
    }
}

function Assert-D16Promotion {
    param(
        [Parameter(Mandatory)][string]$PromotionPath,
        [Parameter(Mandatory)][string]$CandidateManifestPath
    )

    $candidate = Assert-D16Candidate $CandidateManifestPath
    if (-not (Test-Path -LiteralPath $PromotionPath -PathType Leaf)) {
        throw "Reference-promotion record '$PromotionPath' is absent."
    }
    try {
        $record = Get-Content -LiteralPath $PromotionPath -Raw -Encoding utf8 | ConvertFrom-Json
    }
    catch {
        throw "Reference-promotion record is malformed JSON: $($_.Exception.Message)"
    }
    if ((Get-D16Property $record 'schemaVersion' 'Promotion record') -cne '1.0.0' -or
        (Get-D16Property $record 'recordType' 'Promotion record') -cne 'IMMUTABLE_ARTIFACT_REFERENCE_PROMOTION' -or
        (Get-D16Property $record 'status' 'Promotion record') -cne 'DRY_RUN_REFERENCE_ONLY') {
        throw 'Reference-promotion record schema, type, or non-claim state is invalid.'
    }
    if ($record.executionZone -notin @('LOCAL', 'CI_EPHEMERAL') -or
        $record.targetEnvironmentReference -notin @('DEV', 'QA', 'UAT') -or
        $record.authorizationState -cne 'AUTHORIZED_FOR_LOCAL_CI_REFERENCE') {
        throw 'Reference promotion has an unauthorized execution zone, target, or state.'
    }

    $candidateReference = Get-D16Property $record 'candidateManifest' 'Promotion record'
    $artifactReference = Get-D16Property $record 'artifact' 'Promotion record'
    $securityEvidence = Get-D16Property $record 'securityEvidence' 'Promotion record'
    $configuration = Get-D16Property $record 'configuration' 'Promotion record'
    $integrity = Get-D16Property $record 'integrity' 'Promotion record'
    $nonClaims = Get-D16Property $record 'nonClaims' 'Promotion record'
    if ($candidateReference.identity -cne $candidate.Manifest.candidateIdentity -or
        $candidateReference.sha256 -cne $candidate.ManifestSha256 -or
        $artifactReference.identity -cne $candidate.Manifest.candidateIdentity -or
        $artifactReference.releaseIdentity -cne $candidate.Manifest.releaseIdentity -or
        $artifactReference.component -cne $candidate.Manifest.component -or
        $artifactReference.version -cne $candidate.Manifest.version -or
        $artifactReference.digest -cne $candidate.ArtifactDigest -or
        $artifactReference.sourceRevision -cne $candidate.Manifest.source.revision -or
        $securityEvidence.sbomSha256 -cne $candidate.Manifest.evidence.sbom.sha256 -or
        $securityEvidence.vulnerabilitySha256 -cne $candidate.Manifest.evidence.vulnerability.sha256 -or
        $securityEvidence.provenanceSha256 -cne $candidate.Manifest.evidence.provenance.sha256) {
        throw 'Promotion does not reference the exact candidate artifact, source, or evidence identities.'
    }
    if ([string]::IsNullOrWhiteSpace([string](Get-D16Property $configuration 'reference' 'Promotion configuration')) -or
        [string]::IsNullOrWhiteSpace([string](Get-D16Property $configuration 'revision' 'Promotion configuration'))) {
        throw 'Promotion configuration reference is incomplete.'
    }
    if ($configuration.reference -notmatch '^[0-9A-Za-z][0-9A-Za-z._/-]{0,255}$' -or
        $configuration.reference -match '(^|/)\.\.(/|$)' -or
        $configuration.revision -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]{0,127}$') {
        throw 'Promotion configuration must use bounded versioned references, not embedded values.'
    }
    if ($integrity.artifactDigestVerified -ne $true -or
        $integrity.candidateManifestVerified -ne $true -or
        $integrity.provenanceVerified -ne $true -or
        $record.rebuildInvoked -ne $false -or
        $nonClaims.published -ne $false -or
        $nonClaims.deployed -ne $false -or
        $nonClaims.environmentSuccess -ne $false) {
        throw 'Promotion integrity proof or required publication/deployment non-claims are invalid.'
    }
    if ($null -ne $artifactReference.PSObject.Properties['configuration'] -or
        $null -ne $artifactReference.PSObject.Properties['environmentConfiguration']) {
        throw 'Promotion embedded environment configuration into artifact identity.'
    }

    $identityMaterial = @(
        $candidate.ManifestSha256,
        $candidate.ArtifactDigest,
        $record.executionZone,
        $record.targetEnvironmentReference,
        $configuration.reference,
        $configuration.revision
    ) -join "`n"
    $expectedPromotionIdentity = "sha256:$(Get-D16TextSha256 $identityMaterial)"
    if ((Get-D16Property $record 'promotionIdentity' 'Promotion record') -cne $expectedPromotionIdentity) {
        throw 'Promotion identity is not deterministic for the exact artifact and configuration references.'
    }

    return [pscustomobject]@{
        Record = $record
        RecordPath = [IO.Path]::GetFullPath($PromotionPath)
        Candidate = $candidate
    }
}

Export-ModuleMember -Function Get-D16Sha256, Get-D16TextSha256, Write-D16Json, Assert-D16Candidate, Assert-D16Promotion
