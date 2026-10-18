[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$scope = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/governance/t1-53-account-agreements-scope-lock.json') -Raw | ConvertFrom-Json
if ($scope.schemaVersion -cne '1.0.0' -or $scope.increment -cne 'T1-53' -or
    $scope.applicationBase -cne '499a68ff91d6fd2488465c2b87edd6fbb08cb3f8' -or
    $scope.status -cne 'IMPLEMENTATION_CANDIDATE_LOCAL_CI' -or
    $scope.stageGateAdvancement -ne $false -or $scope.productionDeployment -ne $false -or
    @($scope.migrations).Count -ne 1 -or
    $scope.migrations[0].path -cne 'services/customer-identity/migrations/0007_customer_account_agreements.sql') {
    throw 'Invalid T1-53 owner migration scope.'
}
$path = Join-Path $RepositoryRoot $scope.migrations[0].path
if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $scope.migrations[0].sha256) {
    throw 'T1-53 migration source hash mismatch.'
}
if ($SelfTest) { Write-Output 'T1-53 additive owner migration identity verified.' }
else { [IO.Path]::GetFullPath($path) }
