[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = Split-Path -Parent $PSScriptRoot }

function Test-ExactSet([object[]]$Actual, [object[]]$Expected) {
    (@($Actual | Sort-Object) -join '|') -ceq (@($Expected | Sort-Object) -join '|') -and $Actual.Count -eq $Expected.Count
}
function Test-Scope($Scope) {
    $features = @('M2-WS07-E01-F01','M2-WS07-E01-F02','M2-WS07-E01-F03','M2-WS07-E02-F01','M2-WS07-E02-F03')
    return ((Test-ExactSet @($Scope.features.id) $features) -and
        @($Scope.features | Where-Object { $_.owner -cne 'Search & Retrieval Service' -or $_.readiness -cne 'READY' }).Count -eq 0 -and
        (Test-ExactSet @($Scope.newlyRealizedContracts) @('CID-042','CID-043','CID-044','CID-046')) -and
        (Test-ExactSet @($Scope.consumedContracts) @('CID-007','CID-023','CID-024','CID-033','CID-034','CID-035','CID-036','CID-039')) -and
        (Test-ExactSet @($Scope.excludedFeatures) @('M2-WS07-E02-F02','M2-WS07-E03-F01','M2-WS07-E03-F02','M2-WS07-E03-F03')) -and
        $Scope.status -ceq 'CANDIDATE_PENDING_CTO_REVIEW' -and $Scope.evidenceLevel -ceq 'SIMULATOR' -and
        $Scope.searchAuthority -ceq 'DERIVED_REBUILDABLE' -and $Scope.providerSelection -ceq 'NONE' -and
        $Scope.physicalPersistenceOrBroker -ceq 'NOT_SELECTED' -and $Scope.outstandingDecisions.'OD-14' -ceq 'UNRESOLVED' -and
        $Scope.stageGates.'SG-01' -ceq 'READY' -and $Scope.stageGates.'SG-02' -ceq 'CONDITIONALLY_READY' -and
        $Scope.stageGates.'SG-03' -ceq 'BLOCKED' -and $Scope.stageGates.'SG-04' -ceq 'BLOCKED' -and
        $Scope.architectureIntegrity.'R1-R7' -ceq 'FROZEN_UNCHANGED' -and $Scope.architectureIntegrity.R8 -ceq 'ABSENT')
}
function Test-Forbidden([string]$Text) {
    $Text -notmatch '(?i)Elasticsearch|OpenSearch|Pinecone|Weaviate|Qdrant|Azure\.Search|OpenAI|Azure\s*OpenAI|EntityFrameworkCore|SqlConnection|Npgsql|MongoClient|Kafka|RabbitMQ|ServiceBusClient|M2-WS07-E03'
}

$scope = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/governance/d07-scope-lock.json') -Raw | ConvertFrom-Json
if ($SelfTest) {
    $checks = [ordered]@{ 'valid candidate' = Test-Scope $scope }
    $copy = $scope | ConvertTo-Json -Depth 20 | ConvertFrom-Json; $copy.features += [pscustomobject]@{id='M2-WS07-E02-F02';name='Excluded';owner='Search & Retrieval Service';readiness='READY'}; $checks['sixth Feature rejected'] = -not (Test-Scope $copy)
    $copy = $scope | ConvertTo-Json -Depth 20 | ConvertFrom-Json; $copy.features = @($copy.features | Select-Object -Skip 1); $checks['missing Feature rejected'] = -not (Test-Scope $copy)
    $copy = $scope | ConvertTo-Json -Depth 20 | ConvertFrom-Json; $copy.evidenceLevel = 'PRODUCTION'; $checks['Production claim rejected'] = -not (Test-Scope $copy)
    $copy = $scope | ConvertTo-Json -Depth 20 | ConvertFrom-Json; $copy.searchAuthority = 'AUTHORITATIVE'; $checks['search authority claim rejected'] = -not (Test-Scope $copy)
    $copy = $scope | ConvertTo-Json -Depth 20 | ConvertFrom-Json; $copy.outstandingDecisions.'OD-14' = 'RESOLVED'; $checks['OD-14 resolution rejected'] = -not (Test-Scope $copy)
    $checks['provider product rejected'] = -not (Test-Forbidden 'Elasticsearch client')
    $checks['AI provider rejected'] = -not (Test-Forbidden 'OpenAI embeddings')
    foreach ($entry in $checks.GetEnumerator()) { Write-Output "SELF-TEST $(if($entry.Value){'PASS'}else{'FAIL'}): $($entry.Key)" }
    if (@($checks.Values | Where-Object { -not $_ }).Count) { throw 'D07 negative self-tests failed.' }
    Write-Output "D07 self-tests passed: $($checks.Count)/$($checks.Count)."; exit 0
}

$checks = [ordered]@{}
$checks['Exact five-Feature candidate scope'] = Test-Scope $scope
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'services/search-retrieval') -Recurse -File | Where-Object { $_.Extension -in '.cs','.csproj','.md' -and $_.FullName -notmatch '[\/](bin|obj)[\/]' })
$sourceText = @($sourceFiles | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
$checks['No provider, AI, database or broker selected'] = Test-Forbidden $sourceText
$checks['Derived authority and source references are explicit'] = $sourceText.Contains('DERIVED_REBUILDABLE') -and $sourceText.Contains('AuthoritativeOwner') -and $sourceText.Contains('SearchSourceReference')
$checks['Authorization applies before shared lexical/semantic index query'] = $sourceText.Contains('ISearchAuthorizationPolicy') -and $sourceText.Contains('AuthorizeAsync') -and $sourceText.Contains('RequiredAuthorizationContextId')
$checks['Deterministic rebuild and semantic representation exist'] = $sourceText.Contains('Rebuild(') -and $sourceText.Contains('SHA256.HashData') -and $sourceText.Contains('Cosine')
$catalog = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'contracts/Monergy.Contracts/D07ContractCatalog.cs') -Raw
$checks['Exact D07 contract catalog'] = ([regex]::Matches($catalog, '"CID-0(?:42|43|44|46)"')).Count -eq 4
$schema = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'contracts/schemas/authorized-search.schema.json') -Raw | ConvertFrom-Json
$checks['Closed four-contract wire schema'] = $schema.oneOf.Count -eq 4 -and $schema.unevaluatedProperties -eq $false
$checks['Owned D07 tests and visible search route exist'] = (Test-Path -LiteralPath (Join-Path $RepositoryRoot 'tests/search-retrieval/Monergy.SearchRetrieval.Tests/Monergy.SearchRetrieval.Tests.csproj')) -and (Get-Content -LiteralPath (Join-Path $RepositoryRoot 'apps/customer-web/src/App.tsx') -Raw).Contains("'/search'")
$manifest = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'repository.manifest.json') -Raw | ConvertFrom-Json
$checks['Manifest keeps D07 candidate and stage gates bounded'] = $manifest.d07Status -ceq 'CANDIDATE_PENDING_CTO_REVIEW' -and $manifest.d07FeatureState -ceq 'IMPLEMENTATION_CANDIDATE_SIMULATOR_5_OF_5' -and $manifest.d07ContractState -ceq 'CONSUMED_8_REALIZED_4_CANDIDATE' -and $manifest.d07OutstandingDecision -ceq 'OD-14_UNRESOLVED' -and $manifest.deploymentState -ceq 'NOT_DEPLOYED'
foreach ($entry in $checks.GetEnumerator()) { Write-Output "[$(if($entry.Value){'PASS'}else{'FAIL'})] $($entry.Key)" }
$failed = @($checks.Values | Where-Object { -not $_ }); if ($failed.Count) { throw "D07 verification failed: $($checks.Count-$failed.Count)/$($checks.Count)." }
Write-Output "D07 verification passed: $($checks.Count)/$($checks.Count); candidate only."
