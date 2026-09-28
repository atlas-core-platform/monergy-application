[CmdletBinding()]
param([string]$RepositoryRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-FileHashOrState {
    param([string]$Path)
    if (Test-Path -LiteralPath $Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
    'NOT_RUN'
}

$artifactRoot = Join-Path $RepositoryRoot '.artifacts/release'
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
$versions = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/toolchain/versions.json') -Raw | ConvertFrom-Json
$services = (Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/catalog.json') -Raw | ConvertFrom-Json).services
$sourceRevision = (& git -C $RepositoryRoot rev-parse HEAD).Trim()
$committedTree = (& git -C $RepositoryRoot rev-parse 'HEAD^{tree}').Trim()
$trackedDiff = (& git -C $RepositoryRoot diff --binary HEAD) -join "`n"
$untracked = @(& git -C $RepositoryRoot ls-files --others --exclude-standard | Sort-Object)
$untrackedEvidence = @($untracked | ForEach-Object { "$_`:$((Get-FileHash -LiteralPath (Join-Path $RepositoryRoot $_) -Algorithm SHA256).Hash)" }) -join "`n"
$sourceTreeMaterial = "$trackedDiff`n$untrackedEvidence"
$sourceTree = if ([string]::IsNullOrWhiteSpace($sourceTreeMaterial)) {
    "COMMIT_TREE:$committedTree"
} else {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $sourceTreeHash = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($sourceTreeMaterial)))).Replace('-', '').ToLowerInvariant()
    } finally {
        $sha.Dispose()
    }
    "WORKTREE_UNCOMMITTED:$sourceTreeHash"
}
$ociEvidencePath = Join-Path $RepositoryRoot '.artifacts/oci/images.json'
$ociEvidence = if (Test-Path -LiteralPath $ociEvidencePath) { @(Get-Content -LiteralPath $ociEvidencePath -Raw | ConvertFrom-Json) } else { @() }

$components = foreach ($service in $services) {
    $image = @($ociEvidence | Where-Object service -ceq $service.id | Select-Object -First 1)
    $imagePassed = $image.Count -eq 1 -and
        $image[0].PSObject.Properties.Name -contains 'buildResult' -and
        $image[0].buildResult -ceq 'PASS'
    [pscustomobject]@{
        name = $service.artifact
        kind = 'OCI_IMAGE'
        source = "services/$($service.id)"
        status = if ($imagePassed -and $env:GITHUB_ACTIONS -ceq 'true') { 'BUILT_HOSTED_NOT_PUBLISHED' } elseif ($image.Count -eq 1) { 'BUILT_LOCAL_NOT_PUBLISHED' } else { 'NOT_RUN' }
        digest = if ($imagePassed) { $image[0].digest } elseif ($image.Count -eq 1) { $image[0].localDigest } else { $null }
        archiveSha256 = if ($imagePassed) { $image[0].archiveSha256 } else { $null }
        sbom = if ($imagePassed) {
            [pscustomobject]@{ result = $image[0].sbomResult; path = $image[0].sbomPath; sha256 = $image[0].sbomSha256 }
        } else { $null }
        vulnerabilityScan = if ($imagePassed) {
            [pscustomobject]@{ result = $image[0].scanResult; path = $image[0].scanPath; sha256 = $image[0].scanSha256 }
        } else { $null }
    }
}
$components += [pscustomobject]@{
    name = 'monergy-customer-web'
    kind = 'STATIC_WEB_BUNDLE'
    source = 'apps/customer-web'
    status = if (Test-Path -LiteralPath (Join-Path $RepositoryRoot 'apps/customer-web/dist')) { 'BUILT_LOCAL_NOT_PUBLISHED' } else { 'NOT_RUN' }
    digest = $null
}

$manifest = [ordered]@{
    schemaVersion = '1.0.0'
    status = 'D05_ACCEPTED_SIMULATOR_EVIDENCE_NOT_A_RELEASE'
    releaseIdentity = 'MWP-03-D05-financial-rules-calculation-lineage-acceptance'
    acceptanceRecord = [ordered]@{ path = 'build/governance/d05-acceptance.json'; sha256 = Get-FileHashOrState (Join-Path $RepositoryRoot 'build/governance/d05-acceptance.json') }
    mergeAuthorization = 'NOT_AUTHORIZED'
    repositoryCommit = $sourceRevision
    sourceTree = $sourceTree
    runtimeVersions = $versions
    dependencyLocks = [ordered]@{
        pnpm = Get-FileHashOrState (Join-Path $RepositoryRoot 'pnpm-lock.yaml')
        nugetLocks = @(Get-ChildItem -LiteralPath $RepositoryRoot -Recurse -File -Filter 'packages.lock.json' | Where-Object { $_.FullName -notmatch '[\\/](?:bin|obj|\.toolcache)[\\/]' } | Sort-Object FullName | ForEach-Object {
            [pscustomobject]@{ path = $_.FullName.Substring($RepositoryRoot.Length + 1).Replace('\\', '/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
        })
    }
    componentArtifacts = @($components)
    ociEvidenceMatrix = [ordered]@{
        path = '.artifacts/oci/evidence-matrix.json'
        sha256 = Get-FileHashOrState (Join-Path $RepositoryRoot '.artifacts/oci/evidence-matrix.json')
    }
    sbom = [ordered]@{ path = '.artifacts/security/repository.cyclonedx.json'; sha256 = Get-FileHashOrState (Join-Path $RepositoryRoot '.artifacts/security/repository.cyclonedx.json') }
    scanners = [ordered]@{
        syft = $versions.syft; grype = $versions.grype; gitleaks = $versions.gitleaks
        vulnerabilityEvidence = Get-FileHashOrState (Join-Path $RepositoryRoot '.artifacts/security/vulnerability-status.json')
        secretEvidence = Get-FileHashOrState (Join-Path $RepositoryRoot '.artifacts/security/secret-status.json')
    }
    ciRun = if ($env:GITHUB_RUN_ID) { $env:GITHUB_RUN_ID } else { 'LOCAL' }
    published = $false
    deployed = $false
}
$manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $artifactRoot 'product-release-manifest.json') -Encoding utf8
Write-Output 'D05 application acceptance Product Release Manifest generated as evidence; it is not a release, merge approval or publication.'
