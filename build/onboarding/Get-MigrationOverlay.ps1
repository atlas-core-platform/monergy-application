[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$scope = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/governance/onboarding-scope-lock.json') -Raw | ConvertFrom-Json
$expected = @('services/customer-identity/migrations/0003_customer_ownership.sql', 'services/consent/migrations/0001_customer_consent.sql')
function Assert-OnboardingOverlay([pscustomobject]$Value) {
    if ($Value.revision -cne 'AR-002' -or $Value.increment -cne 'REL-01-SETUP-01' -or
        $Value.status -cne 'IMPLEMENTATION_CANDIDATE_LOCAL_CI' -or $Value.stageGateAdvancement -ne $false -or
        $Value.productionDeployment -ne $false -or @($Value.migrations).Count -ne 2 -or
        (@($Value.migrations.path | Sort-Object) -join '|') -cne (@($expected | Sort-Object) -join '|')) {
        throw 'Invalid LOCAL/UAT onboarding migration scope.'
    }
    foreach ($entry in $Value.migrations) {
        $path = Join-Path $RepositoryRoot ([string]$entry.path)
        if (-not (Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne [string]$entry.sha256) {
            throw 'Onboarding migration source hash mismatch.'
        }
    }
}
Assert-OnboardingOverlay $scope
if ($SelfTest) {
    foreach ($field in @('path','sha256')) {
        $copy = $scope | ConvertTo-Json -Depth 10 | ConvertFrom-Json
        $copy.migrations[0].$field = 'invalid'
        $rejected = $false
        try { Assert-OnboardingOverlay $copy } catch { $rejected = $true }
        if (-not $rejected) { throw 'Onboarding migration mutation was accepted.' }
    }
    foreach ($field in @('stageGateAdvancement','productionDeployment')) {
        $copy = $scope | ConvertTo-Json -Depth 10 | ConvertFrom-Json
        $copy.$field = $true
        $rejected = $false
        try { Assert-OnboardingOverlay $copy } catch { $rejected = $true }
        if (-not $rejected) { throw 'Onboarding scope advancement was accepted.' }
    }
    Write-Output 'Onboarding migration scope: 1 positive and 4 negative checks passed.'
} else {
    foreach ($entry in $scope.migrations) { [IO.Path]::GetFullPath((Join-Path $RepositoryRoot ([string]$entry.path))) }
}
