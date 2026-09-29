[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [switch]$SelfTest,
    [switch]$D09ForwardRegression
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = Split-Path -Parent $PSScriptRoot }

function Test-ExactSet([object[]]$Actual, [object[]]$Expected) {
    (@($Actual | Sort-Object) -join '|') -ceq (@($Expected | Sort-Object) -join '|') -and $Actual.Count -eq $Expected.Count
}
function Test-Scope($Scope) {
    $features = @('M2-WS08-E01-F02','M2-WS08-E02-F01','M2-WS08-E02-F03')
    $excluded = @('M2-WS08-E01-F01','M2-WS08-E01-F03','M2-WS08-E02-F02','M2-WS08-E03-F01','M2-WS08-E03-F02','M2-WS08-E03-F03')
    return (Test-ExactSet @($Scope.features.id) $features) -and
        @($Scope.features | Where-Object { $_.owner -cne 'Reporting Service' -or $_.readiness -cne 'READY' }).Count -eq 0 -and
        (Test-ExactSet @($Scope.newlyRealizedContracts) @('CID-051','CID-052','CID-053')) -and
        (Test-ExactSet @($Scope.consumedContracts) @('CID-022','CID-030','CID-033','CID-038','CID-039','CID-048','CID-061')) -and
        (Test-ExactSet @($Scope.excludedFeatures) $excluded) -and
        $Scope.status -ceq 'CANDIDATE_PENDING_CTO_REVIEW' -and $Scope.evidenceLevel -ceq 'SIMULATOR' -and
        (Test-ExactSet @($Scope.executionZones) @('LOCAL','CI_EPHEMERAL')) -and
        $Scope.providerSelection -ceq 'NONE' -and $Scope.physicalPersistenceObjectStoreOrBroker -ceq 'NOT_SELECTED' -and
        $Scope.aiExecution -ceq 'NOT_IMPLEMENTED' -and
        @($Scope.outstandingDecisions.PSObject.Properties | Where-Object Value -cne 'UNRESOLVED').Count -eq 0 -and
        (Test-ExactSet @($Scope.outstandingDecisions.PSObject.Properties.Name) @('C-10','C-11','C-14','OD-08')) -and
        $Scope.stageGates.'SG-01' -ceq 'READY' -and $Scope.stageGates.'SG-02' -ceq 'CONDITIONALLY_READY' -and
        $Scope.stageGates.'SG-03' -ceq 'BLOCKED' -and $Scope.stageGates.'SG-04' -ceq 'BLOCKED' -and
        $Scope.architectureIntegrity.'R1-R7' -ceq 'FROZEN_UNCHANGED' -and $Scope.architectureIntegrity.R8 -ceq 'ABSENT'
}
function Test-Forbidden([string]$Text) {
    $Text -notmatch '(?i)EntityFrameworkCore|Npgsql|SqlConnection|MongoClient|Elasticsearch|OpenSearch|Pinecone|Azure\.Storage|Amazon\.S3|Kafka|RabbitMQ|ServiceBusClient|OpenAI|Azure\s*OpenAI|M2-WS08-E02-F02|M2-WS08-E03'
}

$scope = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/governance/d08-scope-lock.json') -Raw | ConvertFrom-Json
if ($SelfTest) {
    $checks = [ordered]@{ 'valid candidate' = Test-Scope $scope }
    $copy = $scope | ConvertTo-Json -Depth 20 | ConvertFrom-Json; $copy.features += [pscustomobject]@{id='M2-WS08-E01-F01';name='Excluded';owner='Reporting Service';readiness='READY'}; $checks['conditional Feature rejected'] = -not (Test-Scope $copy)
    $copy = $scope | ConvertTo-Json -Depth 20 | ConvertFrom-Json; $copy.newlyRealizedContracts += 'CID-054'; $checks['CID-054 rejected'] = -not (Test-Scope $copy)
    $copy = $scope | ConvertTo-Json -Depth 20 | ConvertFrom-Json; $copy.evidenceLevel = 'PRODUCTION'; $checks['Production claim rejected'] = -not (Test-Scope $copy)
    $copy = $scope | ConvertTo-Json -Depth 20 | ConvertFrom-Json; $copy.outstandingDecisions.'C-11' = 'RESOLVED'; $checks['C-11 resolution rejected'] = -not (Test-Scope $copy)
    $checks['database selection rejected'] = -not (Test-Forbidden 'EntityFrameworkCore provider')
    $checks['object store selection rejected'] = -not (Test-Forbidden 'Amazon.S3 client')
    $checks['AI provider rejected'] = -not (Test-Forbidden 'OpenAI client')
    foreach ($entry in $checks.GetEnumerator()) { Write-Output "SELF-TEST $(if($entry.Value){'PASS'}else{'FAIL'}): $($entry.Key)" }
    if (@($checks.Values | Where-Object { -not $_ }).Count) { throw 'D08 negative self-tests failed.' }
    Write-Output "D08 self-tests passed: $($checks.Count)/$($checks.Count)."; exit 0
}

$checks = [ordered]@{}
$checks['Exact three-Feature candidate scope'] = Test-Scope $scope
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'services/reporting') -Recurse -File | Where-Object {
    $_.Extension -in '.cs','.csproj','.md' -and $_.FullName -notmatch '[\/](bin|obj)[\/]' -and
    (-not $D09ForwardRegression -or ($_.Name -cne 'PostgresReportRepository.cs' -and $_.Extension -cne '.csproj'))
})
$sourceText = @($sourceFiles | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
$checks[$(if ($D09ForwardRegression) { 'No D08 provider, broker or AI selection introduced' } else { 'No provider, physical persistence, broker or AI selected' })] = Test-Forbidden $sourceText
$contractText = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'contracts/Monergy.Contracts/D08ReportingContracts.cs') -Raw
$checks['Reporting authority remains bounded'] = $sourceText.Contains('ReportSourceSnapshot') -and $contractText.Contains('ReportSourceReference') -and $sourceText.Contains('Financial Profile Service') -and $sourceText.Contains('Financial Rules Service') -and $sourceText -notmatch 'FinancialProfileRepository|FinancialRulesRepository|EvidenceRepository'
$checks['Authorization and customer isolation are server-side'] = $sourceText.Contains('IReportingAuthorizationPolicy') -and $sourceText.Contains('AuthorizeAsync') -and $sourceText.Contains('CustomerId')
$checks['CID-051 generation is idempotent and generation-time bounded'] = $sourceText.Contains('ReportOperationIdentity') -and $sourceText.Contains('GetOrCreateAsync') -and $sourceText.Contains('TimeProvider') -and $sourceText.Contains('report.GeneratedAt')
$checks['Provenance, lineage and audit compatibility are preserved'] = $sourceText.Contains('EvidenceReferences') -and $sourceText.Contains('FinancialProvenanceReferences') -and $sourceText.Contains('CalculationLineageReferences') -and $sourceText.Contains('AuditCompatibilityReferenceId')
$checks['Deterministic basic export exists'] = $sourceText.Contains('application/json') -and $sourceText.Contains('SHA256.HashData') -and $scope.exportFormat -ceq 'DETERMINISTIC_JSON_REFERENCE'
$catalog = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'contracts/Monergy.Contracts/D08ContractCatalog.cs') -Raw
$checks['Exact canonical Reporting contract catalog'] = ([regex]::Matches($catalog, '"CID-05[123]"')).Count -eq 3 -and -not $catalog.Contains('CID-054')
$endpointText = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/reporting/ReportingEndpoints.cs') -Raw
$checks['Only CID-051 and CID-052 request endpoints exposed'] = $endpointText.Contains('/contracts/cid-051/v1') -and $endpointText.Contains('/contracts/cid-052/v1') -and -not $endpointText.Contains('cid-054')
$frontend = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'apps/customer-web/src/reports/ReportsExperience.tsx') -Raw
$frontendApi = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'apps/customer-web/src/reports/referenceReportsApi.ts') -Raw
$checks['Product-visible reference route and truthful limits'] = $frontend.Contains('REFERENCE') -and $frontend.Contains('LOCAL / CI ONLY') -and $frontend -match 'not a formal report\s+pack' -and (Get-Content -LiteralPath (Join-Path $RepositoryRoot 'apps/customer-web/src/App.tsx') -Raw).Contains("'/reports'")
$checks['Each explicit CID-051 generation uses a fresh operation key'] = $frontendApi.Contains('generationIdempotencyKey') -and $frontendApi.Contains('crypto.randomUUID()') -and -not $frontendApi.Contains('reference-basic-report')
$tests = @(Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'tests/reporting') -Recurse -File -Filter '*.cs')
$testText = @($tests | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
$checks['Permanent behavior and cross-customer tests exist'] = $testText.Contains('CustomerCannotReadAnotherCustomersReport') -and $testText.Contains('SameIdempotencyIdentityReplaysCommittedReportWithoutRegeneration') -and $testText.Contains('GeneratedAndEventTimesUseTheCommittedGenerationOccurrence') -and $testText.Contains('ReferenceAdaptersFailClosed')
$manifest = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'repository.manifest.json') -Raw | ConvertFrom-Json
$checks['Manifest keeps D08 candidate and stage gates bounded'] = $manifest.d08Status -ceq 'CANDIDATE_PENDING_CTO_REVIEW' -and $manifest.d08FeatureState -ceq 'IMPLEMENTATION_CANDIDATE_SIMULATOR_3_OF_3' -and $manifest.d08ContractState -ceq 'CONSUMED_7_REALIZED_3_CANDIDATE' -and $manifest.deploymentState -ceq 'NOT_DEPLOYED'
foreach ($entry in $checks.GetEnumerator()) { Write-Output "[$(if($entry.Value){'PASS'}else{'FAIL'})] $($entry.Key)" }
$failed = @($checks.Values | Where-Object { -not $_ }); if ($failed.Count) { throw "D08 verification failed: $($checks.Count-$failed.Count)/$($checks.Count)." }
Write-Output "D08 verification passed: $($checks.Count)/$($checks.Count); candidate only."
