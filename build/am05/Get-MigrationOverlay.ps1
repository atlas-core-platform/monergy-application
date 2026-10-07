[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$scopePath = Join-Path $RepositoryRoot 'build/governance/am05-scope-lock.json'
if (-not (Test-Path -LiteralPath $scopePath)) { throw 'AM-05 migration scope is required.' }
$scope = Get-Content -LiteralPath $scopePath -Raw -Encoding utf8 | ConvertFrom-Json
$expected = @(
    'services/customer-identity/migrations/0001_tenant_access_sessions.sql',
    'services/audit/migrations/0003_tenant_access_evidence.sql'
)

function Assert-Overlay {
    param([pscustomobject]$Value)
    if ($Value.schemaVersion -cne '1.0.0' -or $Value.revision -cne 'AR-001' -or
        $Value.architectureMain -cne 'd4d2653ad97808872357865fca4a486611d40fd4' -or
        $Value.applicationBase -cne '45ccd9ebd1f693ca07a3976f3094b87604f6a0b7' -or
        $Value.status -cne 'IMPLEMENTATION_CANDIDATE_LOCAL_CI' -or
        $Value.stageGateAdvancement -ne $false -or $Value.productionDeployment -ne $false -or
        @($Value.migrations).Count -ne 2 -or
        (@($Value.migrations.path | Sort-Object) -join '|') -cne (@($expected | Sort-Object) -join '|')) {
        throw 'Invalid AM-05 migration scope.'
    }
    foreach ($entry in $Value.migrations) {
        $path = Join-Path $RepositoryRoot ([string]$entry.path)
        if (-not (Test-Path -LiteralPath $path) -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne [string]$entry.sha256) {
            throw 'AM-05 migration does not match its reviewed source hash.'
        }
    }
}

Assert-Overlay $scope
$am06Migrations = @(& (Join-Path $RepositoryRoot 'build/am06/Get-MigrationOverlay.ps1') -RepositoryRoot $RepositoryRoot -SelfTest:$SelfTest)
if ($SelfTest) {
    $mutations = @(
        @{ field='revision'; value='OTHER' },
        @{ field='stageGateAdvancement'; value=$true },
        @{ field='productionDeployment'; value=$true },
        @{ field='status'; value='ACCEPTED' }
    )
    foreach ($mutation in $mutations) {
        $copy = $scope | ConvertTo-Json -Depth 10 | ConvertFrom-Json
        $copy.($mutation.field) = $mutation.value
        $rejected = $false
        try { Assert-Overlay $copy } catch { $rejected = $true }
        if (-not $rejected) { throw 'Migration-scope negative test was not rejected.' }
    }
    foreach ($field in @('path', 'sha256')) {
        $copy = $scope | ConvertTo-Json -Depth 10 | ConvertFrom-Json
        $copy.migrations[0].$field = 'invalid'
        $rejected = $false
        try { Assert-Overlay $copy } catch { $rejected = $true }
        if (-not $rejected) { throw 'Migration identity mutation was not rejected.' }
    }
    Write-Output 'AM-05 migration scope: 1 positive and 6 negative checks passed.'
    exit 0
}
# Historical verifiers retain their exact original cohorts/counts. Only these
# reviewed additive owner migrations are accounted for by AM-05's separate gate.
foreach ($entry in $scope.migrations) { [IO.Path]::GetFullPath((Join-Path $RepositoryRoot ([string]$entry.path))) }
foreach ($path in $am06Migrations) { $path }
