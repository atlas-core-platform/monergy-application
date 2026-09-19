[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$SelfTest,
    [switch]$RequireBehavior
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Test-Scope {
    param($Scope)
    $ids = 'M2-WS05-E01-F01|M2-WS05-E01-F02|M2-WS05-E01-F03|M2-WS05-E02-F01|M2-WS05-E02-F02|M2-WS05-E02-F03'
    $contracts = 'CID-037|CID-038|CID-039|CID-040|CID-041'
    return (($Scope.features.id -join '|') -ceq $ids -and
        ($Scope.contracts.id -join '|') -ceq $contracts -and
        $Scope.status -ceq 'CANDIDATE_PENDING_CTO_REVIEW' -and
        $Scope.evidenceLevel -ceq 'SIMULATOR_CANDIDATE' -and
        @($Scope.features | Where-Object { $_.owner -cne 'Financial Rules Service' -or $_.readiness -cne 'READY' }).Count -eq 0 -and
        ($Scope.consumedContracts.id -join '|') -ceq 'CID-032|CID-033')
}
function Test-Boundary {
    param([string]$Text)
    $Text -notmatch 'IFinancialProfileRepository|InMemoryFinancialProfileRepository|IAuditEvidenceRepository|SELECT\s+.+FROM|EntityFrameworkCore'
}
function Test-Evidence {
    param($Evidence)
    $Evidence.total -gt 0 -and $Evidence.failed -eq 0 -and $Evidence.notExecuted -eq 0 -and $Evidence.passed -eq $Evidence.total
}
function Test-RequiredBehavior {
    param([string[]]$Names)
    $required = @('PersistentOrUnknownZonesCannotActivateAnyReferenceAdapter', 'HistoricalAccessFailsClosedWithoutFallback', 'HistoricalReproductionRejectsChangedOriginalDefinition')
    @($required | Where-Object { $needle = $_; @($Names | Where-Object { $_.Contains($needle) }).Count -eq 0 }).Count -eq 0
}
$scopePath = Join-Path $RepositoryRoot 'build/governance/d05-scope-lock.json'
$scope = Get-Content -LiteralPath $scopePath -Raw -Encoding utf8 | ConvertFrom-Json
if ($SelfTest) {
    $checks = [ordered]@{}
    $checks['valid candidate'] = Test-Scope $scope
    foreach ($mutation in @('missing-feature', 'duplicate-contract', 'fabricated-acceptance', 'overstated-evidence', 'foreign-owner')) {
        $copy = $scope | ConvertTo-Json -Depth 30 | ConvertFrom-Json
        switch ($mutation) {
            'missing-feature' { $copy.features = @($copy.features | Select-Object -Skip 1) }
            'duplicate-contract' { $copy.contracts[1].id = $copy.contracts[0].id }
            'fabricated-acceptance' { $copy.status = 'ACCEPTED_COMPLETE' }
            'overstated-evidence' { $copy.evidenceLevel = 'PRODUCTION_COMPATIBILITY' }
            'foreign-owner' { $copy.features[0].owner = 'Financial Profile Service' }
        }
        $checks[$mutation] = -not (Test-Scope $copy)
    }
    $checks['foreign persistence rejected'] = -not (Test-Boundary 'IFinancialProfileRepository')
    $checks['failed behavior rejected'] = -not (Test-Evidence ([pscustomobject]@{total=1;passed=0;failed=1;notExecuted=0}))
    $checks['unexecuted behavior rejected'] = -not (Test-Evidence ([pscustomobject]@{total=1;passed=0;failed=0;notExecuted=1}))
    $allBehavior = @('PersistentOrUnknownZonesCannotActivateAnyReferenceAdapter', 'HistoricalAccessFailsClosedWithoutFallback', 'HistoricalReproductionRejectsChangedOriginalDefinition')
    $checks['missing fixture-escape evidence rejected'] = -not (Test-RequiredBehavior @($allBehavior | Select-Object -Skip 1))
    $checks['missing lineage-drift evidence rejected'] = -not (Test-RequiredBehavior @($allBehavior | Where-Object { $_ -ne 'HistoricalAccessFailsClosedWithoutFallback' }))
    $checks['missing original-rule drift evidence rejected'] = -not (Test-RequiredBehavior @($allBehavior | Select-Object -First 2))
    foreach ($entry in $checks.GetEnumerator()) {
        Write-Output "SELF-TEST $(if ($entry.Value) { 'PASS' } else { 'FAIL' }): $($entry.Key)"
    }
    if (@($checks.Values | Where-Object { -not $_ }).Count) { throw 'D05 negative self-tests failed.' }
    Write-Output "D05 self-tests passed: $($checks.Count)/$($checks.Count)."
    exit 0
}
$checks = [ordered]@{}
$checks['Exact candidate scope, ownership and consumed contracts'] = Test-Scope $scope
$checks['Starting application baseline pinned'] = $scope.startingCommits.application -ceq '9aae295846f0dd1ca01f9d7f99f0233cd53c7626'
$checks['Starting architecture branch explicitly authorized'] = $scope.startingCommits.architecture -ceq '866c05122a0823d38dcaf243d3164b5b2b0947d9'
$checks['Requirements baseline remains pinned'] = $scope.startingCommits.architectureRequirements -ceq '5e7fb1cc9a56cc7b0411640bbb63c13c02c83657'
$checks['Stage gates unchanged'] = $scope.stageGates.'SG-01' -ceq 'READY' -and $scope.stageGates.'SG-02' -ceq 'CONDITIONALLY_READY' -and $scope.stageGates.'SG-03' -ceq 'BLOCKED' -and $scope.stageGates.'SG-04' -ceq 'BLOCKED'
$checks['Methodology remains client dependent'] = 'OD-08' -cin $scope.decisionDependencies -and 'C-10' -cin $scope.decisionDependencies
$allSource = @(Get-ChildItem (Join-Path $RepositoryRoot 'services/financial-rules') -Recurse -Filter '*.cs' | Where-Object FullName -NotMatch '[\\/](bin|obj)[\\/]' | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
$checks['Rules has no foreign persistence access'] = Test-Boundary $allSource
$application = Get-Content (Join-Path $RepositoryRoot 'services/financial-rules/Application/FinancialRulesApplication.cs') -Raw
$adapter = Get-Content (Join-Path $RepositoryRoot 'services/financial-rules/Infrastructure/ReferenceCalculationAdapters.cs') -Raw
$checks['Application owns both outcome decisions'] = $application.Contains('"CID-040"') -and $application.Contains('"CID-041"') -and -not $adapter.Contains('new DomainEvent')
$checks['Atomic adapter consumes application transition'] = $adapter.Contains('applicationTransition()') -and $adapter.Contains('lock (sync)') -and $adapter.Contains('requests.Add') -and $adapter.Contains('outbox.Add')
$checks['Exact input revision and immutable provenance retained'] = $application.Contains('fact.Revision') -and $application.Contains('ReadProvenanceAsync') -and $application.Contains('DefinitionHash')
$checks['Historical reads and replay reauthorize'] = $application.Contains('ValidateHistoricalAccessAsync') -and $application.Contains('calculation.rule.drift') -and $application.Contains('calculation.history.drift')
$checks['Reference environment guard used'] = ([regex]::Matches($adapter, 'ReferenceAdapterGuard.EnsureAllowed')).Count -eq 3
$schema = Get-Content (Join-Path $RepositoryRoot 'contracts/schemas/financial-rules.schema.json') -Raw | ConvertFrom-Json
$checks['Wire schema separates three requests and two events'] = $schema.oneOf.Count -eq 5 -and @($schema.oneOf | Where-Object additionalProperties).Count -eq 0
$checks['Owned test project exists'] = Test-Path (Join-Path $RepositoryRoot 'tests/financial-rules/Monergy.FinancialRules.Tests/Monergy.FinancialRules.Tests.csproj')
$manifest = Get-Content (Join-Path $RepositoryRoot 'repository.manifest.json') -Raw | ConvertFrom-Json
$checks['Prior acceptance is preserved separately'] = $manifest.d04Status -ceq 'ACCEPTED_COMPLETE' -and $manifest.d05Status -ceq 'CANDIDATE_PENDING_CTO_REVIEW' -and $manifest.deploymentState -ceq 'NOT_DEPLOYED'
$checks['No frontend scope added'] = $manifest.d05FrontendBusinessChange -ceq 'NONE_REQUIRED_BY_D05_FEATURE_SCOPE'

# Behavioral claims come from the actual test-run evidence, never source-string presence.
$trxPath = Join-Path $RepositoryRoot '.artifacts/tests/Monergy.FinancialRules.Tests.trx'
$behavior = 'NOT_RUN'
$contracts = @()
if (Test-Path $trxPath) {
    [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
    $counters = $trx.TestRun.ResultSummary.Counters
    $checks['Executed D05 suite has no failed or unexecuted cases'] = Test-Evidence $counters
    $results = @($trx.TestRun.Results.UnitTestResult)
    $checks['Fixture escape, lineage drift and original-rule drift actually tested'] = Test-RequiredBehavior @($results | Where-Object outcome -ceq 'Passed' | Select-Object -ExpandProperty testName)
    $evidenceMap = [ordered]@{
        'CID-037' = @('ExecuteReadAndExplainPreserveExactAuthorityLineage', 'InvalidCommandsAreRejectedBeforeCommit', 'ConcurrentRetryConvergesToOneAtomicResultAndEvent', 'CurrentPolicyAppliesToExecuteReadExplainReplayAndReproduction')
        'CID-038' = @('ExecuteReadAndExplainPreserveExactAuthorityLineage', 'QueriesValidateContractAndPayloadBeforeReading', 'HistoricalAccessFailsClosedWithoutFallback', 'DifferentAuthorizedCustomerCannotReadAnotherCustomersResult')
        'CID-039' = @('ExecuteReadAndExplainPreserveExactAuthorityLineage', 'QueriesValidateContractAndPayloadBeforeReading', 'HistoricalReproductionNeverUsesLatestFactsOrLatestRule', 'HistoricalAccessFailsClosedWithoutFallback')
        'CID-040' = @('CompletedAndFailedEventsAreAtomicReplaySafeAndIndependentlyAuditable(failure: False)', 'BothEventConsumersRejectMalformedOwnershipVersionAndLineage(failed: False)')
        'CID-041' = @('CompletedAndFailedEventsAreAtomicReplaySafeAndIndependentlyAuditable(failure: True)', 'BothEventConsumersRejectMalformedOwnershipVersionAndLineage(failed: True)', 'NumericFailureCreatesOnlyFailedOutcomeNeverFinancialTruth')
    }
    foreach ($cid in $evidenceMap.Keys) {
        $matched = @($results | Where-Object { $name = $_.testName; @($evidenceMap[$cid] | Where-Object { $name.Contains($_) }).Count -gt 0 })
        $allNamed = @($evidenceMap[$cid] | Where-Object { $needle = $_; @($matched | Where-Object { $_.testName.Contains($needle) }).Count -eq 0 }).Count -eq 0
        $pass = $allNamed -and @($matched | Where-Object outcome -cne 'Passed').Count -eq 0
        $checks["$cid named behavioral evidence"] = $pass
        $contracts += [pscustomobject]@{id=$cid;result=$(if($pass){'PASS'}else{'FAIL'});tests=@($matched.testName)}
    }
    $checks['Actual owner end-to-end lineage executed'] = @($results | Where-Object { $_.testName.Contains('ActualOwnersConnectEvidenceValidatedFactsFinancialTruthCalculationLineageAndAudit') -and $_.outcome -ceq 'Passed' }).Count -eq 1
    $behavior = if (Test-Evidence $counters) { 'PASS' } else { 'FAIL' }
} elseif ($RequireBehavior) {
    $checks['Executed test evidence required'] = $false
}
foreach ($entry in $checks.GetEnumerator()) { Write-Output "[$(if($entry.Value){'PASS'}else{'FAIL'})] $($entry.Key)" }
$failed = @($checks.Values | Where-Object { -not $_ })
$evidenceRoot = Join-Path $RepositoryRoot '.artifacts/d05'
New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null
[ordered]@{
    deliverable='MWP-03-D05';status='CANDIDATE_PENDING_CTO_REVIEW';evidenceLevel='SIMULATOR'
    sourceCommit=(& git -C $RepositoryRoot rev-parse HEAD).Trim()
    behavior=$behavior;checks=$checks;contractEvidence=$contracts
    testEvidence=if(Test-Path $trxPath){[ordered]@{path='.artifacts/tests/Monergy.FinancialRules.Tests.trx';sha256=(Get-FileHash $trxPath -Algorithm SHA256).Hash;total=[int]$counters.total;passed=[int]$counters.passed}}else{$null}
} | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $evidenceRoot 'verification.json') -Encoding utf8
if ($failed.Count) { throw "D05 verification failed: $($checks.Count-$failed.Count)/$($checks.Count)." }
Write-Output "D05 verification passed: $($checks.Count)/$($checks.Count); behavior $behavior."
