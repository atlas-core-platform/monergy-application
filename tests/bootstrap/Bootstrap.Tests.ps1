[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [switch]$SchemaCompatibilityOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$migrationRoot = Join-Path $RepositoryRoot 'build/uat/dependencies/source/tools/Monergy.AccessManagement.Migrations/migrations'
$migrationVersions = @(Get-ChildItem -LiteralPath $migrationRoot -File -Filter '*.sql' |
    ForEach-Object {
        if ($_.Name -notmatch '^(?<version>\d{4})_.+\.sql$') {
            throw "Embedded Access Management migration has a non-canonical name: $($_.Name)"
        }
        [int]$Matches.version
    } | Sort-Object)
if ($migrationVersions.Count -eq 0) { throw 'Embedded Access Management migration inventory is empty.' }

$expectedInventory = @(1..$migrationVersions[-1])
if (($migrationVersions -join ',') -cne ($expectedInventory -join ',')) {
    throw "Embedded Access Management migrations are not contiguous: $($migrationVersions -join ', ')."
}

$onboardingSource = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/Monergy.DatabaseMigrator/LocalUatOnboarding.cs') -Raw
$expectationMatches = [regex]::Matches($onboardingSource, 'ExpectedAccessManagementSchemaVersion\s*=\s*(?<version>\d+)\s*;')
if ($expectationMatches.Count -ne 1) {
    throw "Expected exactly one LOCAL-UAT Access Management schema compatibility constant; found $($expectationMatches.Count)."
}

$applicationExpectation = [int]$expectationMatches[0].Groups['version'].Value
$embeddedSchemaVersion = $migrationVersions[-1]
if ($applicationExpectation -ne $embeddedSchemaVersion) {
    throw "LOCAL-UAT expects Access Management schema $applicationExpectation, but the pinned embedded migration inventory ends at $embeddedSchemaVersion."
}
Write-Output "LOCAL-UAT Access Management schema compatibility passed: $applicationExpectation."

if ($SchemaCompatibilityOnly) { exit 0 }

& (Join-Path $RepositoryRoot 'build/verify-bootstrap.ps1') -RepositoryRoot $RepositoryRoot -SelfTest
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& (Join-Path $RepositoryRoot 'build/verify-bootstrap.ps1') -RepositoryRoot $RepositoryRoot
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Output 'Bootstrap tests passed.'
