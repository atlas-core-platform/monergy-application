[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReleaseIdentity,
    [Parameter(Mandatory)][string]$Component,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$SourceRevision,
    [Parameter(Mandatory)][string]$SourceTree,
    [Parameter(Mandatory)][string]$PipelineIdentity,
    [Parameter(Mandatory)][string]$BuildInvocationIdentity,
    [Parameter(Mandatory)][string]$BuiltAtUtc,
    [Parameter(Mandatory)][string]$ArtifactPath,
    [Parameter(Mandatory)][string]$SbomPath,
    [Parameter(Mandatory)][string]$VulnerabilityEvidencePath,
    [string]$ArtifactMediaType = 'application/octet-stream',
    [string]$ArtifactStoreRoot = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) '.artifacts/d16/candidates')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'ImmutableArtifact.Common.psm1') -Force

if ($ReleaseIdentity -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]{0,127}$' -or
    $Component -notmatch '^[0-9a-z][0-9a-z.-]{0,127}$') {
    throw 'Release and component identities must be stable path-safe identifiers.'
}
$mutableAliases = @('latest', 'main', 'master', 'head', 'dev', 'qa', 'uat', 'production', 'prod')
if ($Version -notmatch '^[0-9A-Za-z][0-9A-Za-z._+-]{0,127}$' -or $mutableAliases -ccontains $Version.ToLowerInvariant()) {
    throw 'Artifact version must be immutable and cannot be a mutable branch or environment label.'
}
$SourceRevision = $SourceRevision.ToLowerInvariant()
$SourceTree = $SourceTree.ToLowerInvariant()
if ($SourceRevision -notmatch '^[0-9a-f]{40}$' -or $SourceTree -notmatch '^[0-9a-f]{40}$') {
    throw 'Source revision and source tree must be exact Git identities.'
}
$sourceRepositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$git = (Get-Command git -ErrorAction Stop).Source
$previousErrorPreference = $ErrorActionPreference
$ErrorActionPreference = 'SilentlyContinue'
try {
    $resolvedRepositoryOutput = @(& $git -C $sourceRepositoryRoot rev-parse --show-toplevel 2>$null)
    $repositoryExitCode = $LASTEXITCODE
    $resolvedCommitOutput = @(& $git -C $sourceRepositoryRoot rev-parse --verify "$SourceRevision^{commit}" 2>$null)
    $commitExitCode = $LASTEXITCODE
    $resolvedCommit = if ($resolvedCommitOutput.Count -eq 1) { ([string]$resolvedCommitOutput[0]).Trim().ToLowerInvariant() } else { $null }
    if ($commitExitCode -eq 0 -and $null -ne $resolvedCommit) {
        $resolvedTreeOutput = @(& $git -C $sourceRepositoryRoot rev-parse --verify "$resolvedCommit^{tree}" 2>$null)
        $treeExitCode = $LASTEXITCODE
    }
    else {
        $resolvedTreeOutput = @()
        $treeExitCode = 1
    }
}
finally {
    $ErrorActionPreference = $previousErrorPreference
}
$resolvedRepository = if ($resolvedRepositoryOutput.Count -eq 1) {
    [IO.Path]::GetFullPath(([string]$resolvedRepositoryOutput[0]).Trim())
}
else { $null }
$resolvedTree = if ($resolvedTreeOutput.Count -eq 1) { ([string]$resolvedTreeOutput[0]).Trim().ToLowerInvariant() } else { $null }
$pathComparison = if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
    [StringComparison]::OrdinalIgnoreCase
}
else { [StringComparison]::Ordinal }
if ($repositoryExitCode -ne 0 -or $null -eq $resolvedRepository -or
    -not $resolvedRepository.Equals($sourceRepositoryRoot, $pathComparison)) {
    throw 'D16 source identity could not be verified against the local monergy-application Git repository.'
}
if ($commitExitCode -ne 0 -or $null -eq $resolvedCommit -or $resolvedCommit -cne $SourceRevision) {
    throw 'Supplied source revision does not resolve to that exact commit in the local monergy-application repository.'
}
if ($treeExitCode -ne 0 -or $null -eq $resolvedTree -or $resolvedTree -cne $SourceTree) {
    throw 'Supplied source tree does not equal the exact Git tree resolved for the supplied source revision.'
}
$normalizedBuiltAtUtc = ConvertTo-D16UtcTimestamp $BuiltAtUtc 'Requested build UTC timestamp'
if ([string]::IsNullOrWhiteSpace($PipelineIdentity) -or
    [string]::IsNullOrWhiteSpace($BuildInvocationIdentity) -or
    [string]::IsNullOrWhiteSpace($ArtifactMediaType)) {
    throw 'Build identity, invocation, UTC build time, and artifact media type are mandatory.'
}
foreach ($inputPath in @($ArtifactPath, $SbomPath, $VulnerabilityEvidencePath)) {
    if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf)) { throw "Required build material '$inputPath' is absent." }
}
try {
    $sbom = Get-Content -LiteralPath $SbomPath -Raw -Encoding utf8 | ConvertFrom-Json
    $vulnerability = Get-Content -LiteralPath $VulnerabilityEvidencePath -Raw -Encoding utf8 | ConvertFrom-Json
}
catch {
    throw "Security evidence is malformed JSON: $($_.Exception.Message)"
}
if ($sbom.bomFormat -cne 'CycloneDX' -or [string]::IsNullOrWhiteSpace([string]$sbom.specVersion)) {
    throw 'A CycloneDX SBOM with a specification version is mandatory.'
}
if ($vulnerability.result -cne 'PASS') { throw 'Passing vulnerability evidence is mandatory.' }

$artifactHash = Get-D16Sha256 $ArtifactPath
$sbomHash = Get-D16Sha256 $SbomPath
$vulnerabilityHash = Get-D16Sha256 $VulnerabilityEvidencePath
$artifactDigest = "sha256:$artifactHash"
$candidateIdentity = "$ReleaseIdentity/$Component@$Version#$artifactDigest"
$storeRoot = [IO.Path]::GetFullPath($ArtifactStoreRoot)
$capsuleRoot = Join-Path (Join-Path (Join-Path $storeRoot $ReleaseIdentity) $Component) $artifactHash
$manifestPath = Join-Path $capsuleRoot 'artifact-manifest.json'

if (Test-Path -LiteralPath $capsuleRoot) {
    $existing = Assert-D16Candidate $manifestPath
    if ($existing.Manifest.candidateIdentity -cne $candidateIdentity -or
        $existing.Manifest.source.revision -cne $SourceRevision -or
        $existing.Manifest.source.tree -cne $SourceTree -or
        $existing.Manifest.build.pipelineIdentity -cne $PipelineIdentity -or
        $existing.Manifest.build.invocationIdentity -cne $BuildInvocationIdentity -or
        $existing.BuiltAtUtc -cne $normalizedBuiltAtUtc -or
        $existing.Manifest.artifact.mediaType -cne $ArtifactMediaType -or
        $existing.Manifest.evidence.sbom.sha256 -cne $sbomHash -or
        $existing.Manifest.evidence.vulnerability.sha256 -cne $vulnerabilityHash) {
        throw 'Digest-keyed artifact capsule already exists with different identity or evidence; it will not be overwritten.'
    }
    Write-Output $manifestPath
    return
}

New-Item -ItemType Directory -Path $storeRoot -Force | Out-Null
$stagingRoot = Join-Path $storeRoot ".staging-$([Guid]::NewGuid().ToString('N'))"
try {
    $artifactDirectory = Join-Path $stagingRoot 'artifact'
    $evidenceDirectory = Join-Path $stagingRoot 'evidence'
    New-Item -ItemType Directory -Path $artifactDirectory, $evidenceDirectory -Force | Out-Null
    $storedArtifactPath = Join-Path $artifactDirectory 'payload.bin'
    $storedSbomPath = Join-Path $evidenceDirectory 'sbom.cyclonedx.json'
    $storedVulnerabilityPath = Join-Path $evidenceDirectory 'vulnerability.json'
    $storedProvenancePath = Join-Path $evidenceDirectory 'provenance.json'
    Copy-Item -LiteralPath $ArtifactPath -Destination $storedArtifactPath
    Copy-Item -LiteralPath $SbomPath -Destination $storedSbomPath
    Copy-Item -LiteralPath $VulnerabilityEvidencePath -Destination $storedVulnerabilityPath
    if ((Get-D16Sha256 $storedArtifactPath) -cne $artifactHash -or
        (Get-D16Sha256 $storedSbomPath) -cne $sbomHash -or
        (Get-D16Sha256 $storedVulnerabilityPath) -cne $vulnerabilityHash) {
        throw 'Artifact or evidence changed while creating the immutable capsule.'
    }

    $provenance = [ordered]@{
        schemaVersion = '1.0.0'
        predicateType = 'MONERGY_D16_BUILD_PROVENANCE'
        candidateIdentity = $candidateIdentity
        subject = [ordered]@{ component = $Component; version = $Version; digest = $artifactDigest }
        source = [ordered]@{ repository = 'monergy-application'; revision = $SourceRevision; tree = $SourceTree }
        build = [ordered]@{
            pipelineIdentity = $PipelineIdentity
            invocationIdentity = $BuildInvocationIdentity
            builtAtUtc = $BuiltAtUtc
        }
        evidence = [ordered]@{ sbomSha256 = $sbomHash; vulnerabilitySha256 = $vulnerabilityHash }
    }
    Write-D16Json $provenance $storedProvenancePath
    $provenanceHash = Get-D16Sha256 $storedProvenancePath

    $manifest = [ordered]@{
        schemaVersion = '1.0.0'
        recordType = 'IMMUTABLE_ARTIFACT_CANDIDATE'
        status = 'CANDIDATE_LOCAL_CI_ONLY'
        releaseIdentity = $ReleaseIdentity
        component = $Component
        version = $Version
        candidateIdentity = $candidateIdentity
        source = [ordered]@{ repository = 'monergy-application'; revision = $SourceRevision; tree = $SourceTree }
        build = [ordered]@{
            pipelineIdentity = $PipelineIdentity
            invocationIdentity = $BuildInvocationIdentity
            builtAtUtc = $BuiltAtUtc
        }
        artifact = [ordered]@{
            path = 'artifact/payload.bin'
            originalFileName = [IO.Path]::GetFileName($ArtifactPath)
            mediaType = $ArtifactMediaType
            sizeBytes = (Get-Item -LiteralPath $storedArtifactPath).Length
            digest = $artifactDigest
        }
        evidence = [ordered]@{
            sbom = [ordered]@{ path = 'evidence/sbom.cyclonedx.json'; sha256 = $sbomHash }
            vulnerability = [ordered]@{ path = 'evidence/vulnerability.json'; sha256 = $vulnerabilityHash }
            provenance = [ordered]@{ path = 'evidence/provenance.json'; sha256 = $provenanceHash }
        }
        promotionPolicy = [ordered]@{
            mode = 'DRY_RUN_REFERENCE_ONLY'
            authorizationState = 'AUTHORIZED_FOR_LOCAL_CI_REFERENCE'
            productionAllowed = $false
        }
        publication = [ordered]@{ published = $false; deployed = $false }
        environmentConfigurationEmbedded = $false
    }
    Write-D16Json $manifest (Join-Path $stagingRoot 'artifact-manifest.json')
    $null = Assert-D16Candidate (Join-Path $stagingRoot 'artifact-manifest.json')

    $capsuleParent = Split-Path -Parent $capsuleRoot
    New-Item -ItemType Directory -Path $capsuleParent -Force | Out-Null
    Move-Item -LiteralPath $stagingRoot -Destination $capsuleRoot
    $stagingRoot = $null
}
finally {
    if ($null -ne $stagingRoot -and (Test-Path -LiteralPath $stagingRoot)) {
        $resolvedStore = [IO.Path]::GetFullPath($storeRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
        $resolvedStaging = [IO.Path]::GetFullPath($stagingRoot)
        if ($resolvedStaging.StartsWith("$resolvedStore$([IO.Path]::DirectorySeparatorChar).staging-", [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
        }
    }
}

$null = Assert-D16Candidate $manifestPath
Write-Output $manifestPath
