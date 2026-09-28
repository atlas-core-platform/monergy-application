[CmdletBinding()]
param([string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

try {
    $docker = (Get-Command docker -ErrorAction Stop).Source
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        # Docker Desktop can emit benign host-capability warnings on stderr even
        # when the Linux engine is healthy. Availability is governed by exit code.
        $ErrorActionPreference = 'Continue'
        $null = & $docker info 2>&1
        $dockerExitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($dockerExitCode -ne 0) {
        Write-Output 'BLOCKED: Docker Linux engine is unavailable; no OCI build is represented as PASS.'
        exit 2
    }
} catch {
    Write-Output 'BLOCKED: Docker Linux engine is unavailable; no OCI build is represented as PASS.'
    exit 2
}

$artifactRoot = Join-Path $RepositoryRoot '.artifacts/oci'
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
$services = (Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/catalog.json') -Raw | ConvertFrom-Json).services
$results = [System.Collections.Generic.List[object]]::new()
foreach ($service in $services) {
    $tag = "ghcr.io/atlas-core-platform/$($service.artifact):d03-candidate-local"
    & docker build --file (Join-Path $RepositoryRoot "services/$($service.id)/Dockerfile") --tag $tag $RepositoryRoot
    if ($LASTEXITCODE -ne 0) { throw "OCI build failed for $($service.id)." }
    $digest = (& docker image inspect $tag --format '{{.Id}}').Trim()
    $results.Add([pscustomobject]@{ service = $service.id; image = $tag; localDigest = $digest; published = $false }) | Out-Null
}
$results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $artifactRoot 'images.json') -Encoding utf8
Write-Output "OCI packaging PASS: $($results.Count)/$($services.Count) independent Linux images built; none published."
