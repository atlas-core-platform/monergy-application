[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [switch]$SchemaCompatibilityOnly,
    [switch]$RuntimePrivilegeOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$bootstrapSource = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/Monergy.DatabaseMigrator/LocalUatBootstrap.cs') -Raw
$expectedProvenanceGrant = 'GRANT INSERT ON am.access_request_events,am.access_request_permission_grants TO {runtime};'
if (-not $bootstrapSource.Contains($expectedProvenanceGrant)) {
    throw 'LOCAL-UAT does not grant the AM runtime INSERT on immutable access-request provenance.'
}
if ($bootstrapSource -match 'GRANT\s+(?:[^;]*(?:UPDATE|DELETE|TRUNCATE)[^;]*)\s+ON\s+[^;]*access_request_permission_grants') {
    throw 'LOCAL-UAT grants a prohibited mutation privilege on immutable access-request provenance.'
}
foreach ($privilege in @('SELECT', 'INSERT', 'UPDATE', 'DELETE', 'TRUNCATE')) {
    if (-not $bootstrapSource.Contains("has_table_privilege(current_user,'am.access_request_permission_grants','$privilege')")) {
        throw "LOCAL-UAT does not verify the AM runtime $privilege privilege on access-request provenance."
    }
}
$embeddedAccessRequestSource = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/uat/dependencies/source/src/Monergy.AccessManagement/AccessRequests.cs') -Raw
$runtimeWritePattern = '(?im)\b(?:INSERT\s+INTO|UPDATE|DELETE\s+FROM)\s+am\.(?<table>[a-z0-9_]+)'
$flowWriteTables = @([regex]::Matches($embeddedAccessRequestSource, $runtimeWritePattern) |
    ForEach-Object { $_.Groups['table'].Value } | Sort-Object -Unique)
$probeWriteTables = @([regex]::Matches($bootstrapSource, $runtimeWritePattern) |
    ForEach-Object { $_.Groups['table'].Value } | Sort-Object -Unique)
$unprobedFlowTables = @($flowWriteTables | Where-Object { $_ -notin $probeWriteTables })
if ($unprobedFlowTables.Count -ne 0) {
    throw "LOCAL-UAT does not exercise runtime writes used by the embedded AM access-request flow: $($unprobedFlowTables -join ', ')."
}
if (-not $embeddedAccessRequestSource.Contains('INSERT INTO am.access_request_permission_grants (') -or
    -not $bootstrapSource.Contains('LOCAL_AM_RUNTIME_PROVENANCE_INSERT_FAILED') -or
    -not $bootstrapSource.Contains('await transaction.RollbackAsync()')) {
    throw 'LOCAL-UAT does not exercise the embedded AM fulfillment provenance write through its deployed runtime role.'
}
Write-Output "LOCAL-UAT AM runtime provenance privilege contract passed: SELECT/INSERT allowed; UPDATE/DELETE/TRUNCATE denied; $($flowWriteTables.Count) flow tables covered by the transactional probe."

if ($RuntimePrivilegeOnly) { exit 0 }

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
