[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$scope = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/governance/am06-scope-lock.json') -Raw | ConvertFrom-Json
function Assert-Overlay([pscustomobject]$Value) {
    if ($Value.revision -cne 'AR-001' -or $Value.increment -cne 'AM-06' -or
        $Value.applicationBase -cne '375e6058b0f488f74d4a0924feae4f70f6fe10fb' -or
        $Value.status -cne 'IMPLEMENTATION_CANDIDATE_LOCAL_CI' -or
        $Value.stageGateAdvancement -ne $false -or $Value.productionDeployment -ne $false -or
        @($Value.migrations).Count -ne 1 -or
        $Value.migrations[0].path -cne 'services/customer-identity/migrations/0002_tenant_identity_provisioning.sql') {
        throw 'Invalid AM-06 migration scope.'
    }
    $entry = $Value.migrations[0]
    $path = Join-Path $RepositoryRoot ([string]$entry.path)
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne [string]$entry.sha256) {
        throw 'AM-06 migration source hash mismatch.'
    }
}
Assert-Overlay $scope
if ($SelfTest) {
    foreach ($field in @('path','sha256')) {
        $copy = $scope | ConvertTo-Json -Depth 10 | ConvertFrom-Json
        $copy.migrations[0].$field = 'invalid'
        $rejected = $false
        try { Assert-Overlay $copy } catch { $rejected = $true }
        if (-not $rejected) { throw 'AM-06 migration mutation was accepted.' }
    }
    foreach ($field in @('stageGateAdvancement','productionDeployment')) {
        $copy = $scope | ConvertTo-Json -Depth 10 | ConvertFrom-Json
        $copy.$field = $true
        $rejected = $false
        try { Assert-Overlay $copy } catch { $rejected = $true }
        if (-not $rejected) { throw 'AM-06 scope advancement was accepted.' }
    }
    Write-Output 'AM-06 migration scope: 1 positive and 4 negative checks passed.'
} else {
    [IO.Path]::GetFullPath((Join-Path $RepositoryRoot ([string]$scope.migrations[0].path)))
}
