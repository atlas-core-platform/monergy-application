[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CandidateManifestPath,
    [Parameter(Mandatory)][ValidateSet('LOCAL', 'CI_EPHEMERAL')][string]$ExecutionZone,
    [Parameter(Mandatory)][ValidateSet('DEV', 'QA', 'UAT')][string]$TargetEnvironmentReference,
    [Parameter(Mandatory)][string]$ConfigurationReference,
    [Parameter(Mandatory)][string]$ConfigurationRevision,
    [string]$OutputRoot = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) '.artifacts/d16/promotions')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'ImmutableArtifact.Common.psm1') -Force

if ([string]::IsNullOrWhiteSpace($ConfigurationReference) -or [string]::IsNullOrWhiteSpace($ConfigurationRevision)) {
    throw 'Versioned non-secret configuration reference and revision are mandatory.'
}
if ($ConfigurationReference -notmatch '^[0-9A-Za-z][0-9A-Za-z._/-]{0,255}$' -or
    $ConfigurationReference -match '(^|/)\.\.(/|$)' -or
    $ConfigurationRevision -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]{0,127}$') {
    throw 'Configuration must be supplied as bounded versioned references, not embedded values.'
}
$candidate = Assert-D16Candidate $CandidateManifestPath
$identityMaterial = @(
    $candidate.ManifestSha256,
    $candidate.ArtifactDigest,
    $ExecutionZone,
    $TargetEnvironmentReference,
    $ConfigurationReference,
    $ConfigurationRevision
) -join "`n"
$promotionHash = Get-D16TextSha256 $identityMaterial
$promotionIdentity = "sha256:$promotionHash"
$record = [ordered]@{
    schemaVersion = '1.0.0'
    recordType = 'IMMUTABLE_ARTIFACT_REFERENCE_PROMOTION'
    status = 'DRY_RUN_REFERENCE_ONLY'
    promotionIdentity = $promotionIdentity
    executionZone = $ExecutionZone
    targetEnvironmentReference = $TargetEnvironmentReference
    authorizationState = 'AUTHORIZED_FOR_LOCAL_CI_REFERENCE'
    candidateManifest = [ordered]@{
        identity = $candidate.Manifest.candidateIdentity
        sha256 = $candidate.ManifestSha256
    }
    artifact = [ordered]@{
        identity = $candidate.Manifest.candidateIdentity
        releaseIdentity = $candidate.Manifest.releaseIdentity
        component = $candidate.Manifest.component
        version = $candidate.Manifest.version
        digest = $candidate.ArtifactDigest
        sourceRevision = $candidate.Manifest.source.revision
    }
    securityEvidence = [ordered]@{
        sbomSha256 = $candidate.Manifest.evidence.sbom.sha256
        vulnerabilitySha256 = $candidate.Manifest.evidence.vulnerability.sha256
        provenanceSha256 = $candidate.Manifest.evidence.provenance.sha256
    }
    configuration = [ordered]@{ reference = $ConfigurationReference; revision = $ConfigurationRevision }
    integrity = [ordered]@{
        artifactDigestVerified = $true
        candidateManifestVerified = $true
        provenanceVerified = $true
    }
    rebuildInvoked = $false
    nonClaims = [ordered]@{ published = $false; deployed = $false; environmentSuccess = $false }
}

$outputRoot = [IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$recordPath = Join-Path $outputRoot "$promotionHash.json"
$recordJson = $record | ConvertTo-Json -Depth 30
if (Test-Path -LiteralPath $recordPath) {
    $existingJson = Get-Content -LiteralPath $recordPath -Raw -Encoding utf8
    if ($existingJson -cne $recordJson) {
        throw 'Deterministic promotion identity already exists with different content; it will not be overwritten.'
    }
}
else {
    [IO.File]::WriteAllText($recordPath, $recordJson, [Text.UTF8Encoding]::new($false))
}
$null = Assert-D16Promotion $recordPath $CandidateManifestPath
Write-Output $recordPath
