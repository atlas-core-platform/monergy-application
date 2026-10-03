[CmdletBinding()]
param([string]$RepositoryRoot, [switch]$SelfTest)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = Split-Path -Parent $PSScriptRoot }

function Test-ExactSet([object[]]$Actual, [object[]]$Expected) {
    (@($Actual | ForEach-Object { [string]$_ } | Sort-Object) -join '|') -ceq
        (@($Expected | ForEach-Object { [string]$_ } | Sort-Object) -join '|') -and $Actual.Count -eq $Expected.Count
}

function Test-Scope($Value) {
    $allContracts = @($Value.newlyRealizedContracts) + @($Value.compatibilityContracts) +
        @($Value.consumedContracts) + @($Value.referenceScenarioContracts)
    $Value.deliverable -ceq 'MWP-03-D12' -and
    $Value.status -ceq 'CANDIDATE_PENDING_CTO_REVIEW' -and
    $Value.evidenceLevel -ceq 'SIMULATOR' -and
    @($Value.features).Count -eq 1 -and
    $Value.features[0].id -ceq 'M2-WS03-E02-F03' -and
    $Value.features[0].owner -ceq 'Document Intelligence Service' -and
    $Value.features[0].readiness -ceq 'READY' -and
    (Test-ExactSet @($Value.newlyRealizedContracts) @('CID-026','CID-029')) -and
    (Test-ExactSet @($Value.compatibilityContracts) @('CID-025','CID-027','CID-028')) -and
    (Test-ExactSet @($Value.consumedContracts) @('CID-007','CID-021','CID-022')) -and
    (Test-ExactSet @($Value.referenceScenarioContracts) @('CID-020','CID-055','CID-057','CID-061')) -and
    (Test-ExactSet $allContracts @('CID-007','CID-020','CID-021','CID-022','CID-025','CID-026','CID-027','CID-028','CID-029','CID-055','CID-057','CID-061')) -and
    $Value.directVerificationContractCount -eq 12 -and
    (Test-ExactSet @($Value.changedDomainServiceBoundaries) @('Document Intelligence Service')) -and
    (Test-ExactSet @($Value.executionZones) @('LOCAL','CI_EPHEMERAL')) -and
    $Value.persistence -ceq 'REFERENCE_IN_MEMORY_ONLY' -and
    $Value.providerSelection -ceq 'NONE' -and
    $Value.frontendChange -ceq 'NONE' -and
    $Value.d11.sourceDependency -ceq 'NONE' -and
    $Value.d11.status -ceq 'FROZEN_ACCEPTANCE_DEFERRED' -and
    $Value.d11.securityFailure -ceq 'RETAINED' -and
    $Value.stageGates.'SG-01' -ceq 'READY' -and
    $Value.stageGates.'SG-02' -ceq 'CONDITIONALLY_READY' -and
    $Value.stageGates.'SG-03' -ceq 'BLOCKED' -and
    $Value.stageGates.'SG-04' -ceq 'BLOCKED' -and
    $Value.architectureIntegrity.'R1-R7' -ceq 'FROZEN_UNCHANGED' -and
    $Value.architectureIntegrity.R8 -ceq 'ABSENT'
}

function Test-Forbidden([string]$Text) {
    $Text -notmatch '(?i)EntityFrameworkCore|SqlConnection|Npgsql|MongoClient|Kafka|RabbitMQ|ServiceBus|EventBridge|OpenAI|Azure\s*OpenAI|Tesseract|\bTextract\b|DocumentIntelligenceClient'
}

$scopePath = Join-Path $RepositoryRoot 'build/governance/d12-scope-lock.json'
$scope = Get-Content -LiteralPath $scopePath -Raw -Encoding utf8 | ConvertFrom-Json

if ($SelfTest) {
    $checks = [ordered]@{ 'valid D12 scope' = Test-Scope $scope }
    foreach ($mutation in @(
        @{ name='second Feature rejected'; path='features'; value=@($scope.features + [pscustomobject]@{id='M2-WS03-E02-F04';owner='Document Intelligence Service';readiness='READY'}) },
        @{ name='contract expansion rejected'; path='newlyRealizedContracts'; value=@($scope.newlyRealizedContracts + 'CID-030') },
        @{ name='acceptance claim rejected'; path='status'; value='ACCEPTED_COMPLETE' },
        @{ name='Production evidence rejected'; path='evidenceLevel'; value='PRODUCTION' },
        @{ name='persistent adapter rejected'; path='persistence'; value='POSTGRESQL' },
        @{ name='provider selection rejected'; path='providerSelection'; value='Azure AI Document Intelligence' },
        @{ name='frontend scope rejected'; path='frontendChange'; value='REPROCESS_BUTTON' },
        @{ name='D11 dependency rejected'; path='d11.sourceDependency'; value='REQUIRED' },
        @{ name='SG-02 advancement rejected'; path='stageGates.SG-02'; value='READY' },
        @{ name='R8 rejected'; path='architectureIntegrity.R8'; value='PRESENT' }
    )) {
        $copy = $scope | ConvertTo-Json -Depth 20 | ConvertFrom-Json
        $parts = $mutation.path.Split('.')
        if ($parts.Count -eq 1) {
            $copy.($parts[0]) = $mutation.value
        }
        else {
            $target = $copy
            foreach ($part in $parts[0..($parts.Count - 2)]) { $target = $target.$part }
            $target.($parts[-1]) = $mutation.value
        }
        $checks[$mutation.name] = -not (Test-Scope $copy)
    }
    foreach ($entry in $checks.GetEnumerator()) { Write-Output "SELF-TEST $(if ($entry.Value) { 'PASS' } else { 'FAIL' }): $($entry.Key)" }
    if (@($checks.Values | Where-Object { -not $_ }).Count) { throw 'D12 negative self-tests failed.' }
    Write-Output "D12 self-tests passed: $($checks.Count)/$($checks.Count) (positive controls: 1; negative mutations: 10)."
    exit 0
}

$checks = [ordered]@{}
$checks['Exact one-Feature D12 scope and unchanged governance'] = Test-Scope $scope
$catalogText = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'contracts/Monergy.Contracts/D12ContractCatalog.cs') -Raw -Encoding utf8
$catalogIds = @([regex]::Matches($catalogText, 'new\("(CID-\d{3})"') | ForEach-Object { $_.Groups[1].Value })
$checks['Exact twelve-family additive D12 catalog'] = Test-ExactSet $catalogIds @('CID-007','CID-020','CID-021','CID-022','CID-025','CID-026','CID-027','CID-028','CID-029','CID-055','CID-057','CID-061')
$legacyCatalog = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'contracts/Monergy.Contracts/Vs02ContractCatalog.cs') -Raw -Encoding utf8
$checks['Historical D03 fourteen-contract catalog remains isolated'] = ([regex]::Matches($legacyCatalog, 'new\("CID-\d{3}"')).Count -eq 14 -and -not $legacyCatalog.Contains('CID-026') -and -not $legacyCatalog.Contains('CID-029')
$schema = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'contracts/schemas/document-reprocessing.schema.json') -Raw -Encoding utf8 | ConvertFrom-Json
$securitySchema = $schema.'$defs'.security
$failureCategorySchema = $schema.'$defs'.documentProcessingFailed.properties.payload.properties.errorCategory
$checks['Closed CID-026 and CID-029 wire schema'] = $schema.oneOf.Count -eq 2 -and
    $schema.'$defs'.reprocessDocument.additionalProperties -eq $false -and
    $schema.'$defs'.documentProcessingFailed.additionalProperties -eq $false -and
    $securitySchema.additionalProperties -eq $false -and
    $securitySchema.properties.actor.additionalProperties -eq $false -and
    $securitySchema.properties.workload.additionalProperties -eq $false -and
    $securitySchema.properties.access.additionalProperties -eq $false -and
    (Test-ExactSet @($failureCategorySchema.enum) @('AccessDenied','ConsentRequired','ConsentExpired','ConsentRevoked','DependencyFailure','ProcessingFailed'))
$contracts = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'contracts/Monergy.Contracts/D12DocumentReprocessingContracts.cs') -Raw -Encoding utf8
$checks['Bounded reason and typed result/failure contracts'] = @('FailedProcessing','UpdatedEvidence','ReprocessDocumentResult','DocumentProcessingFailedPayload') | ForEach-Object { $contracts.Contains($_) } | Where-Object { -not $_ } | Measure-Object | Select-Object -ExpandProperty Count | ForEach-Object { $_ -eq 0 }
$application = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/document-intelligence/Application/DocumentProcessingApplication.cs') -Raw -Encoding utf8
$checks['Eligibility and exact Evidence identity checks are owner controlled'] = @('ValidateEligibility','MatchesRequestedSource','PredecessorSnapshot','reprocessing.reason.ineligible') | ForEach-Object { $application.Contains($_) } | Where-Object { -not $_ } | Measure-Object | Select-Object -ExpandProperty Count | ForEach-Object { $_ -eq 0 }
$checks['Submission replay status and extraction policy are explicit'] = ([regex]::Matches($application, 'executionPolicy\.EvaluateAsync')).Count -ge 5 -and $application.Contains('GetByIdempotencyAsync') -and $application.Contains('PayloadFingerprint')
$checks['Success and failure terminal intents are typed'] = $application.Contains('DomainEvent<ValidatedSourceFactsProducedPayload>') -and $application.Contains('DomainEvent<DocumentProcessingFailedPayload>') -and $application.Contains('CommitTerminalAsync')
$repository = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/document-intelligence/Infrastructure/ReferenceDocumentProcessingAdapters.cs') -Raw -Encoding utf8
$checks['Atomic typed idempotency predecessor and immutable terminal protection exist'] = @('lock (sync)','Dictionary<IdempotencyIdentity','MatchesSnapshot','FreezeFacts','FreezeEvent','PayloadFingerprint','ActiveAttemptId','Invalid or stale terminal processing attempt','failNextTerminalCommit') | ForEach-Object { $repository.Contains($_) } | Where-Object { -not $_ } | Measure-Object | Select-Object -ExpandProperty Count | ForEach-Object { $_ -eq 0 }
$matrix = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/governance/d12-scenario-matrix.json') -Raw -Encoding utf8 | ConvertFrom-Json
$checks['All twelve scenario groups classify and name focused evidence'] = $matrix.status -ceq 'CANDIDATE_EVIDENCE_RECONCILED' -and
    (Test-ExactSet @($matrix.scenarios.id) @('D12-T01','D12-T02','D12-T03','D12-T04','D12-T05','D12-T06','D12-T07','D12-T08','D12-T09','D12-T10','D12-T11','D12-T12')) -and
    @($matrix.scenarios | Where-Object { @($_.tests).Count -eq 0 -or @($_.evidenceTypes).Count -eq 0 }).Count -eq 0
$testRoot = Join-Path $RepositoryRoot 'tests/document-reprocessing/Monergy.DocumentReprocessing.Tests'
$testText = @(Get-ChildItem -LiteralPath $testRoot -Filter '*.cs' | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw -Encoding utf8 }) -join "`n"
$checks['Required access race immutability wire job and lineage evidence exists'] = @('CrossCustomer','Concurrent','ExecutionTimePolicy','SameCustomerReplayAndStatus','ConsentRevokedDuringEvidenceRead','ChangedPredecessorDuringEvidencePreflight','DistinctScopedKeyTuples','CommittedFactsAndTerminalEventsAreImmutable','SerializedCid026Wire','TimedOutClaimWithCancellation','ExactSourceAndProcessingLineage','CancellationAfterAdmission') | ForEach-Object { $testText.Contains($_) } | Where-Object { -not $_ } | Measure-Object | Select-Object -ExpandProperty Count | ForEach-Object { $_ -eq 0 }
$sourceText = $application + "`n" + $repository + "`n" + $testText
$checks['No database provider OCR AI broker or D11 dependency introduced'] = Test-Forbidden $sourceText
$checks['Document Intelligence has no persistence migration'] = @(Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'services/document-intelligence') -Recurse -File -Filter '*.sql').Count -eq 0
$manifest = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'repository.manifest.json') -Raw -Encoding utf8 | ConvertFrom-Json
$checks['Repository manifest records bounded D12 candidate'] = $manifest.d12Status -ceq 'CANDIDATE_PENDING_CTO_REVIEW' -and $manifest.d12FeatureState -ceq 'IMPLEMENTATION_CANDIDATE_SIMULATOR_1_OF_1' -and $manifest.d12ContractState -ceq 'DIRECT_SCOPE_12_REALIZED_2_CANDIDATE' -and $manifest.deploymentState -ceq 'NOT_DEPLOYED'

foreach ($entry in $checks.GetEnumerator()) { Write-Output "[$(if ($entry.Value) { 'PASS' } else { 'FAIL' })] $($entry.Key)" }
$failed = @($checks.Values | Where-Object { -not $_ })
if ($failed.Count) { throw "D12 verification failed: $($checks.Count - $failed.Count)/$($checks.Count)." }
Write-Output "D12 verification passed: $($checks.Count)/$($checks.Count); candidate only."
