[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$scope = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/governance/t1-44-customer-profile-scope-lock.json') -Raw | ConvertFrom-Json
if ($scope.schemaVersion -cne '1.0.0' -or $scope.increment -cne 'T1-44' -or
    $scope.applicationBase -cne 'fdd31a8a9ee9707d4b920fdce8161fe29fb646c0' -or
    $scope.status -cne 'IMPLEMENTATION_CANDIDATE_LOCAL_CI' -or
    $scope.stageGateAdvancement -ne $false -or $scope.productionDeployment -ne $false -or
    @($scope.migrations).Count -ne 1 -or
    $scope.migrations[0].path -cne 'services/customer-identity/migrations/0005_self_customer_profile.sql') {
    throw 'Invalid T1-44 owner migration scope.'
}
$path = Join-Path $RepositoryRoot $scope.migrations[0].path
if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $scope.migrations[0].sha256) {
    throw 'T1-44 migration source hash mismatch.'
}
if ($SelfTest) { Write-Output 'T1-44 additive owner migration identity verified.' }
else { [IO.Path]::GetFullPath($path) }
