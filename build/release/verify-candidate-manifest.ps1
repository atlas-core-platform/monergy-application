[CmdletBinding()]
param([string]$RepositoryRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))

$ErrorActionPreference = 'Stop'
$path = Join-Path $RepositoryRoot '.artifacts/release/product-release-manifest.json'
if (-not (Test-Path -LiteralPath $path)) { throw 'Candidate release manifest is absent.' }
$manifest = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
if ($manifest.status -cne 'D05_ACCEPTED_SIMULATOR_EVIDENCE_NOT_A_RELEASE') { throw 'Manifest status is not the governed D05 acceptance-evidence state.' }
if ($manifest.releaseIdentity -cne 'MWP-03-D05-financial-rules-calculation-lineage-acceptance') { throw 'Manifest release identity is not the governed D05 acceptance identity.' }
if ($manifest.mergeAuthorization -cne 'NOT_AUTHORIZED') { throw 'Application acceptance is not exact-commit merge approval.' }
$acceptanceHash = (Get-FileHash (Join-Path $RepositoryRoot 'build/governance/d05-acceptance.json') -Algorithm SHA256).Hash.ToLowerInvariant()
if ($manifest.acceptanceRecord.path -cne 'build/governance/d05-acceptance.json' -or $manifest.acceptanceRecord.sha256 -cne $acceptanceHash) { throw 'Acceptance record identity does not match the current source.' }
if (@($manifest.componentArtifacts).Count -ne 13) { throw 'Manifest must identify 12 services and one frontend bundle.' }
$projectCount = @(Get-ChildItem -LiteralPath $RepositoryRoot -Recurse -File -Filter '*.csproj' | Where-Object { $_.FullName -notmatch '[\\/](?:bin|obj|\.toolcache)[\\/]' }).Count
if (@($manifest.dependencyLocks.nugetLocks).Count -ne $projectCount) { throw 'Manifest must identify one NuGet lock file for every project.' }
if ($manifest.published -or $manifest.deployed) { throw 'D05 acceptance evidence cannot claim publication or deployment.' }
if ($manifest.sourceTree -notmatch '^(?:WORKTREE_UNCOMMITTED|COMMIT_TREE):[0-9a-f]{40,64}$') { throw 'Candidate source-tree identity is invalid.' }
$ociComponents = @($manifest.componentArtifacts | Where-Object kind -ceq 'OCI_IMAGE')
if ($env:GITHUB_ACTIONS -ceq 'true') {
    if ($manifest.repositoryCommit -cne $env:GITHUB_SHA) { throw 'Hosted manifest commit does not match GITHUB_SHA.' }
    if ($manifest.sourceTree -notmatch '^COMMIT_TREE:[0-9a-f]{40}$') { throw 'Hosted manifest must identify the committed Git tree.' }
    if (@($ociComponents | Where-Object status -ceq 'BUILT_HOSTED_NOT_PUBLISHED').Count -ne 12) { throw 'Hosted manifest must record 12 built OCI artifacts.' }
    if (@($ociComponents | Where-Object { $_.digest -notmatch '^sha256:[0-9a-f]{64}$' }).Count -ne 0) { throw 'Hosted manifest contains an invalid OCI digest.' }
    if (@($ociComponents | Where-Object { $_.sbom.result -cne 'PASS' -or $_.sbom.sha256 -notmatch '^[0-9a-f]{64}$' }).Count -ne 0) { throw 'Hosted manifest must record 12 passing image SBOMs.' }
    if (@($ociComponents | Where-Object { $_.vulnerabilityScan.result -cne 'PASS' -or $_.vulnerabilityScan.sha256 -notmatch '^[0-9a-f]{64}$' }).Count -ne 0) { throw 'Hosted manifest must record 12 passing image scans.' }
    if ($manifest.ociEvidenceMatrix.sha256 -notmatch '^[0-9a-f]{64}$') { throw 'Hosted OCI evidence-matrix hash is absent.' }
}
Write-Output "D05 acceptance Product Release Manifest PASS: 13 components, $projectCount dependency locks, unpublished and undeployed."
