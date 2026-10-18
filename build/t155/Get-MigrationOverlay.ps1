[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$scope = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/governance/t1-55-customer-date-of-birth-scope-lock.json') -Raw | ConvertFrom-Json
if ($scope.schemaVersion -cne '1.0.0' -or $scope.increment -cne 'T1-55' -or
    $scope.applicationBase -cne '755bc8f7bc5eccddbb0b7b816af0a69c0fd09a09' -or
    $scope.status -cne 'IMPLEMENTATION_CANDIDATE_LOCAL_CI' -or
    $scope.stageGateAdvancement -ne $false -or $scope.productionDeployment -ne $false -or
    @($scope.migrations).Count -ne 1 -or
    $scope.migrations[0].path -cne 'services/customer-identity/migrations/0008_customer_date_of_birth.sql') {
    throw 'Invalid T1-55 owner migration scope.'
}
$path = Join-Path $RepositoryRoot $scope.migrations[0].path
if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $scope.migrations[0].sha256) {
    throw 'T1-55 migration source hash mismatch.'
}
if ($SelfTest) { Write-Output 'T1-55 additive owner migration identity verified.' }
else { [IO.Path]::GetFullPath($path) }
