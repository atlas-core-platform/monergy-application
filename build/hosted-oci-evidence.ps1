[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [string[]]$ServiceId,
    [switch]$PlanOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Resolve-RequiredCommand {
    param([string]$Name)
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if (-not $command) { throw "Required command '$Name' is unavailable." }
    $command.Source
}

function Get-Sha256 {
    param([string]$Path)
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

$catalogServices = @((Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/catalog.json') -Raw | ConvertFrom-Json).services)
if ($catalogServices.Count -ne 12) { throw "Expected exactly 12 service boundaries; found $($catalogServices.Count)." }
$requested = @($ServiceId | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)
$services = @(if ($requested.Count -eq 0) {
    @($catalogServices)
} else {
    $unknown = @($requested | Where-Object { $_ -notin @($catalogServices.id) })
    if ($unknown.Count) { throw "Unknown OCI service selection: $($unknown -join ', ')." }
    @($catalogServices | Where-Object { $_.id -in $requested })
})
if ($services.Count -eq 0) { throw 'At least one OCI service must be selected.' }

if ($PlanOnly) {
    [pscustomobject][ordered]@{
        schemaVersion = '1.0.0'
        scope = if ($services.Count -eq $catalogServices.Count) { 'FULL' } else { 'IMPACTED' }
        availableServices = $catalogServices.Count
        selectedServices = @($services.id)
    } | ConvertTo-Json -Depth 4 -Compress
    return
}

$docker = Resolve-RequiredCommand 'docker'
$syft = Resolve-RequiredCommand 'syft'
$grype = Resolve-RequiredCommand 'grype'
$null = & $docker info
if ($LASTEXITCODE -ne 0) { throw 'Docker Linux engine is unavailable; hosted OCI evidence cannot pass.' }

$root = Join-Path $RepositoryRoot '.artifacts/oci'
$archiveRoot = Join-Path $root 'archives'
$sbomRoot = Join-Path $root 'sboms'
$scanRoot = Join-Path $root 'scans'
foreach ($path in @($root, $archiveRoot, $sbomRoot, $scanRoot)) {
    New-Item -ItemType Directory -Path $path -Force | Out-Null
}

$sourceIdentity = if ($env:GITHUB_SHA) { $env:GITHUB_SHA.Substring(0, 12) } else { 'local' }
$rows = [System.Collections.Generic.List[object]]::new()
foreach ($service in $services) {
    $image = "ghcr.io/atlas-core-platform/$($service.artifact):d03-$sourceIdentity"
    $dockerFile = Join-Path $RepositoryRoot "services/$($service.id)/Dockerfile"
    $archivePath = Join-Path $archiveRoot "$($service.id).docker.tar"
    $sbomPath = Join-Path $sbomRoot "$($service.id).cyclonedx.json"
    $scanPath = Join-Path $scanRoot "$($service.id).grype.json"

    & $docker build --file $dockerFile --tag $image $RepositoryRoot
    if ($LASTEXITCODE -ne 0) { throw "OCI build failed for $($service.id)." }
    $digest = (& $docker image inspect $image --format '{{.Id}}').Trim()
    if ($digest -notmatch '^sha256:[0-9a-f]{64}$') { throw "Invalid image digest for $($service.id): $digest" }

    & $docker save --output $archivePath $image
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $archivePath)) {
        throw "OCI archive creation failed for $($service.id)."
    }

    & $syft "docker-archive:$archivePath" --output "cyclonedx-json=$sbomPath"
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $sbomPath)) {
        throw "Image SBOM generation failed for $($service.id)."
    }

    & $grype "docker-archive:$archivePath" --output json --file $scanPath --fail-on high
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $scanPath)) {
        throw "Image vulnerability scan failed for $($service.id)."
    }

    $rows.Add([pscustomobject][ordered]@{
        service = $service.id
        image = $image
        digest = $digest
        archiveSha256 = Get-Sha256 $archivePath
        buildResult = 'PASS'
        sbomResult = 'PASS'
        sbomPath = ".artifacts/oci/sboms/$($service.id).cyclonedx.json"
        sbomSha256 = Get-Sha256 $sbomPath
        scanResult = 'PASS'
        scanPath = ".artifacts/oci/scans/$($service.id).grype.json"
        scanSha256 = Get-Sha256 $scanPath
        published = $false
    }) | Out-Null
}

$matrix = [ordered]@{
    schemaVersion = '1.0.0'
    sourceCommit = (& git -C $RepositoryRoot rev-parse HEAD).Trim()
    workflowRun = if ($env:GITHUB_RUN_ID) { $env:GITHUB_RUN_ID } else { 'LOCAL' }
    scope = if ($services.Count -eq $catalogServices.Count) { 'FULL' } else { 'IMPACTED' }
    availableServices = $catalogServices.Count
    expectedServices = $services.Count
    selectedServices = @($services.id)
    buildPass = @($rows | Where-Object buildResult -ceq 'PASS').Count
    sbomPass = @($rows | Where-Object sbomResult -ceq 'PASS').Count
    scanPass = @($rows | Where-Object scanResult -ceq 'PASS').Count
    digestCount = @($rows | Where-Object { $_.digest -match '^sha256:[0-9a-f]{64}$' }).Count
    published = $false
    rows = @($rows)
}

$rows | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $root 'images.json') -Encoding utf8
$matrix | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $root 'evidence-matrix.json') -Encoding utf8

if ($matrix.buildPass -ne $services.Count -or $matrix.sbomPass -ne $services.Count -or
    $matrix.scanPass -ne $services.Count -or $matrix.digestCount -ne $services.Count) {
    throw 'Hosted OCI evidence matrix is incomplete.'
}
Write-Output "Hosted OCI evidence PASS: scope=$($matrix.scope); builds $($matrix.buildPass)/$($services.Count); image SBOMs $($matrix.sbomPass)/$($services.Count); image scans $($matrix.scanPass)/$($services.Count); digests $($matrix.digestCount)/$($services.Count); published 0."
