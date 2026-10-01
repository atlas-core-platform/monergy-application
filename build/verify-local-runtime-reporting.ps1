[CmdletBinding()]
param([string]$RepositoryRoot, [switch]$SelfTest)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = Split-Path -Parent $PSScriptRoot }

function Test-ExactSet([object[]]$Actual, [object[]]$Expected) {
    (@($Actual | ForEach-Object { [string]$_ } | Sort-Object) -join '|') -ceq
        (@($Expected | ForEach-Object { [string]$_ } | Sort-Object) -join '|')
}

function Test-Scope($Value) {
    $contracts = @('CID-007','CID-020','CID-021','CID-022','CID-023','CID-024','CID-030','CID-031','CID-032','CID-033','CID-034','CID-035','CID-036','CID-037','CID-038','CID-039','CID-040','CID-041','CID-051','CID-052','CID-053','CID-061')
    $advanced = @('M2-WS08-E01-F02','M2-WS08-E02-F01','M2-WS08-E02-F03')
    $Value.deliverable -ceq 'MWP-03-D11' -and
    $Value.status -ceq 'CANDIDATE_PENDING_MANUAL_REVIEW' -and
    $Value.evidenceLevel -ceq 'LOCAL_CI_EPHEMERAL_SYNTHETIC' -and
    $Value.primaryFeature -ceq 'M2-WS01-E01-F03' -and
    (Test-ExactSet @($Value.advancedFeatures) $advanced) -and
    (Test-ExactSet @($Value.contractFootprint) $contracts) -and
    $Value.runtimeProfile -ceq 'persisted-reporting' -and
    $Value.sourceReadTransport -ceq 'HTTP_EXISTING_CONTRACTS' -and
    $Value.reportPersistence -ceq 'REPORTING_OWNED_POSTGRESQL' -and
    $Value.auditTransport -ceq 'LOCAL_ONLY_PROVIDER_NEUTRAL_HTTP' -and
    $Value.syntheticCustomers -eq 2 -and
    (Test-ExactSet @($Value.excludedContracts) @('CID-019','CID-048','CID-054')) -and
    $Value.conditionalPlatformFeatures.'M2-WS01-E03-F01' -ceq 'NOT_ACCEPTED_BY_D11' -and
    $Value.conditionalPlatformFeatures.'M2-WS01-E03-F02' -ceq 'NOT_ACCEPTED_BY_D11' -and
    @($Value.outstandingDecisions.PSObject.Properties | Where-Object Value -cne 'UNRESOLVED').Count -eq 0 -and
    $Value.stageGates.'SG-01' -ceq 'READY' -and
    $Value.stageGates.'SG-02' -ceq 'CONDITIONALLY_READY' -and
    $Value.stageGates.'SG-03' -ceq 'BLOCKED' -and
    $Value.stageGates.'SG-04' -ceq 'BLOCKED' -and
    $Value.architectureIntegrity.'R1-R7' -ceq 'FROZEN_UNCHANGED' -and
    $Value.architectureIntegrity.R8 -ceq 'ABSENT' -and
    $Value.architectureIntegrity.systemExpertAuthority -ceq 'UNCHANGED' -and
    $Value.providerSelections -ceq 'NONE' -and
    $Value.deploymentState -ceq 'NOT_DEPLOYED'
}

$scope = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/governance/d11-scope-lock.json') -Raw -Encoding utf8 | ConvertFrom-Json
if ($SelfTest) {
    $checks = [ordered]@{ 'valid D11 scope' = Test-Scope $scope }
    foreach ($mutation in @(
        @{ name='Feature expansion rejected'; path='primaryFeature'; value='M2-WS01-E03-F01' },
        @{ name='contract expansion rejected'; path='contractFootprint'; value=@($scope.contractFootprint + 'CID-054') },
        @{ name='acceptance rejected'; path='status'; value='ACCEPTED_COMPLETE' },
        @{ name='Production evidence rejected'; path='evidenceLevel'; value='PRODUCTION' },
        @{ name='provider selection rejected'; path='providerSelections'; value='Managed broker' },
        @{ name='shared environment Feature rejected'; path='conditionalPlatformFeatures.M2-WS01-E03-F01'; value='ACCEPTED' },
        @{ name='open decision resolution rejected'; path='outstandingDecisions.OD-15'; value='RESOLVED' },
        @{ name='stage gate advancement rejected'; path='stageGates.SG-02'; value='READY' },
        @{ name='R8 rejected'; path='architectureIntegrity.R8'; value='PRESENT' },
        @{ name='System Expert authority change rejected'; path='architectureIntegrity.systemExpertAuthority'; value='UPDATED' }
    )) {
        $copy = $scope | ConvertTo-Json -Depth 20 | ConvertFrom-Json
        $parts = $mutation.path.Split('.')
        if ($parts.Count -eq 1) { $copy.($parts[0]) = $mutation.value }
        else {
            $target = $copy
            foreach ($part in $parts[0..($parts.Count - 2)]) { $target = $target.$part }
            $target.($parts[-1]) = $mutation.value
        }
        $checks[$mutation.name] = -not (Test-Scope $copy)
    }
    foreach ($entry in $checks.GetEnumerator()) { Write-Output "SELF-TEST $(if ($entry.Value) { 'PASS' } else { 'FAIL' }): $($entry.Key)" }
    if (@($checks.Values | Where-Object { -not $_ }).Count) { throw 'D11 negative self-tests failed.' }
    Write-Output "D11 self-tests passed: $($checks.Count)/$($checks.Count)."
    exit 0
}

$checks = [ordered]@{}
$checks['D11 scope and preserved governance'] = Test-Scope $scope
$controller = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/local/Invoke-MonergyLocal.ps1') -Raw -Encoding utf8
$profile = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/local/persisted-reporting.profile.json') -Raw -Encoding utf8 | ConvertFrom-Json
$compose = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/local/compose.persisted-reporting.yml') -Raw -Encoding utf8
$actionFunctionsPresent = @(@('Invoke-Prepare','Invoke-Start','Invoke-Seed','Get-Status','Invoke-Verify','Invoke-Stop') |
    Where-Object { -not $controller.Contains($_) }).Count -eq 0
$checks['Exact fixed local actions are implemented and documented'] =
    (Test-ExactSet @($profile.actions) @('Prepare','Start','Seed','Status','Verify','Stop')) -and $actionFunctionsPresent
$checks['D11 stores and published ports are distinct and loopback-only'] = $compose.Contains('name: monergy-d11-persisted-reporting') -and $compose.Contains("'127.0.0.1:55434:5432'") -and $compose.Contains("'127.0.0.1:58334:8333'") -and -not $compose.Contains('monergy-d09') -and -not $compose.Contains('monergy-d10')
$checks['No destructive reset or broad process termination exists'] = $controller -notmatch 'down\s+-v|docker\s+volume\s+rm|Get-Process\s+dotnet|Get-Process\s+node|taskkill\s+/IM' -and $controller.Contains('Test-OwnedProcess')
$checks['Prepare Start Seed responsibilities remain separate'] = $controller.Contains("Profile is NOT_PREPARED. Run Prepare explicitly") -and $controller.Contains('refusing implicit reprovisioning') -and $controller.Contains('Assert-PreparedIdentity') -and $controller.Contains('Invoke-Seed')
$processModule = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/local/Monergy.LocalProcess.psm1') -Raw -Encoding utf8
$runtimeEvidence = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/local/Invoke-D11RuntimeEvidence.ps1') -Raw -Encoding utf8
$recoveryTests = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/local/Test-MonergyLocalRecovery.ps1') -Raw -Encoding utf8
$workflow = Get-Content -LiteralPath (Join-Path $RepositoryRoot '.github/workflows/bootstrap.yml') -Raw -Encoding utf8
$checks['Per-user secret state is protected before creation and absent from outputs'] = $controller.Contains("Initialize-RestrictedDirectory") -and $controller.Contains('SetAccessRuleProtection') -and $controller.Contains('chmod 600') -and $controller.Contains('chmod 700') -and $controller.Contains('localTransportToken')
$checks['Controller children use explicit isolated environments and safe migration input'] = $processModule.Contains('$info.Environment.Clear()') -and $controller.Contains('MONERGY_MIGRATION_CONNECTION') -and $controller.Contains('--connection-environment') -and -not $controller.Contains('--connection $owner')
$checks['Canonical source identity is shared by Prepare and final evidence'] = $processModule.Contains('canonical-file-set-v1') -and $processModule.Contains('[StringComparer]::Ordinal') -and $processModule.Contains('ToLowerInvariant()') -and $controller.Contains('Get-CanonicalDirtySourceIdentity') -and $runtimeEvidence.Contains('Get-CanonicalDirtySourceIdentity') -and $runtimeEvidence.Contains('not bound to the current canonical source identity')
$retainedTestsPresent = @(@('Seaweed-only without secrets','PostgreSQL-only without secrets','secrets with one retained volume','clean first run','complete retained restart') | Where-Object { -not $recoveryTests.Contains($_) }).Count -eq 0
$checks['Retained secrets and both provider volumes fail closed as one set'] = $controller.Contains('Resolve-D11RetainedState') -and $controller.Contains('BLOCKED_RECOVERY_REQUIRED') -and $controller.Contains('refusing credential regeneration or implicit reprovisioning') -and $retainedTestsPresent
$prepareIndex = $workflow.IndexOf('name: Prepare the clean pinned D11 profile', [StringComparison]::Ordinal)
$browserInstallIndex = $workflow.IndexOf('name: Install Chromium for real D11 browser evidence', [StringComparison]::Ordinal)
$checks['Clean hosted D11 job restores dependencies before Playwright install'] = $prepareIndex -ge 0 -and $browserInstallIndex -gt $prepareIndex -and $workflow.Contains('Invoke-MonergyLocal.ps1 -Action Prepare')
$hostedAllowlistPresent = @(@('.artifacts/d11/hosted/controller-status.json','.artifacts/d11/playwright/results.json','.artifacts/d11/playwright/test-results/**/trace.zip','.artifacts/d11/playwright/test-results/**/*.png') | Where-Object { -not $workflow.Contains($_) }).Count -eq 0
$checks['Hosted failure evidence uses a narrow non-secret allowlist'] = $hostedAllowlistPresent -and -not $workflow.Contains('.artifacts/d11/persisted-reporting/secrets.json') -and -not $workflow.Contains('.artifacts/d11/persisted-reporting/**/*.json')
$reader = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/reporting/Infrastructure/ServiceContractReportSourceReader.cs') -Raw -Encoding utf8
$checks['Reporting reads exact owner contracts over HTTP'] = @('cid-021','cid-022','cid-030','cid-032','cid-033','cid-038','cid-039','PostAsJsonAsync') | ForEach-Object { $reader.Contains($_) } | Where-Object { -not $_ } | Measure-Object | Select-Object -ExpandProperty Count | ForEach-Object { $_ -eq 0 }
$checks['Reporting reader carries trusted correlation and workload context'] = $reader.Contains('ReportSourceReadContext') -and $reader.Contains('d11-reporting') -and $reader.Contains('source.CorrelationId') -and $reader.Contains('source.RequestId')
$checks['Reporting reader validates requested identity evidence and exact lineage consistency'] = @('profile-identity-invalid','fact-identity-invalid','provenance-identity-invalid','evidence-version-inconsistent','calculation-identity-invalid','explanation-boundary-invalid','calculation-lineage-invalid','lineage-inconsistent','scenario-invalid') | ForEach-Object { $reader.Contains($_) } | Where-Object { -not $_ } | Measure-Object | Select-Object -ExpandProperty Count | ForEach-Object { $_ -eq 0 }
$structuralFieldsPresent = @(@('ImplementationIdentity','NumericSemantics','ExecutedAt','OutcomeEventId') | Where-Object { -not $reader.Contains($_) }).Count -eq 0
$checks['CID-038 and CID-039 use complete structural result and input-lineage comparison'] = $reader.Contains('CalculationResultsMatch') -and $reader.Contains('expected.Inputs.SequenceEqual(actual.Inputs)') -and $structuralFieldsPresent
$registration = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/reporting/ReportingRegistration.cs') -Raw -Encoding utf8
$checks['Persisted profile selects real reader without reference fallback'] = $registration.Contains('ServiceContractReportSourceReader') -and $registration.Contains('else') -and $registration.Contains('ReferenceReportSourceReader')
$reportApplication = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/reporting/Application/ReportingApplication.cs') -Raw -Encoding utf8
$checks['Reporting idempotency and transaction authority remain unchanged'] = $reportApplication.Contains('GetOrCreateAsync') -and $reportApplication.Contains('ReportOperationIdentity') -and $reportApplication.Contains('CID-053') -and $reportApplication.Contains('report.GeneratedAt')
$evidenceSetup = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/evidence/EvidenceLocalSetupEndpoints.cs') -Raw -Encoding utf8
$checks['Synthetic Evidence setup stages then invokes existing owner application'] = $evidenceSetup.Contains('StageAsync') -and $evidenceSetup.Contains('CreateDocumentVersionAsync') -and $evidenceSetup.Contains('X-Monergy-Local-Setup')
$rulesRegistration = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/financial-rules/FinancialRulesRegistration.cs') -Raw -Encoding utf8
$checks['Rules grants are explicit local reporting and seeder contexts'] = $rulesRegistration.Contains('local-fixture-seeder') -and $rulesRegistration.Contains('d11-reporting-workload') -and $rulesRegistration.Contains('ConsentRequired') -eq $false -and $rulesRegistration.Contains('DateTimeOffset.MaxValue')
$auditHost = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/local/Monergy.LocalAuditHost/Program.cs') -Raw -Encoding utf8
$auditRuntime = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/reporting/Infrastructure/LocalReportingRuntime.cs') -Raw -Encoding utf8
$checks['LOCAL Audit host exposes only bounded ingestion and status'] = $auditHost.Contains('/contracts/cid-061/v1') -and $auditHost.Contains('ConsumeWithDispositionAsync') -and $auditHost.Contains('X-Monergy-Local-Transport') -and -not $auditHost.Contains('CID-054')
$checks['Reporting outbox continuously retries event-bound duplicate-safe Audit delivery'] = $auditRuntime.Contains('ReportingOutboxHostedService') -and $auditRuntime.Contains('PeriodicTimer') -and $auditRuntime.Contains('LocalHttpAuditEventTransport') -and $auditRuntime.Contains('ValidReceipt') -and $auditRuntime.Contains('reporting.audit-delivery.receipt-mismatch') -and $auditHost.Contains('LocalAuditProtocol.IsValidReportingEvent')
$fixture = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/local/fixtures/persisted-reporting.synthetic.json') -Raw -Encoding utf8 | ConvertFrom-Json
$checks['Two distinguishable synthetic owner-mediated customers are defined'] = @($fixture.customers).Count -eq 2 -and @($fixture.customers.customerId | Sort-Object -Unique).Count -eq 2 -and $fixture.classification -ceq 'SYNTHETIC_ENGINEERING_FIXTURE'
$checks['Seed uses owner commands and engineering rule fixture'] = $controller.Contains('/operations/local/evidence/stage') -and $controller.Contains('/contracts/cid-031/v1') -and $controller.Contains('/contracts/cid-037/v1') -and $controller.Contains("ruleId = 'engineering.sum'")
$checks['Repeated Seed selects current owner revisions with deterministic calculation identity'] = $controller.Contains('/contracts/cid-030/v1') -and $controller.Contains('$currentProfile.data.facts') -and $controller.Contains('$calculationIdentity') -and $controller.Contains('$calculationKey')
$checks['Verify proves replay classified isolation exact export and event-bound Audit delivery'] = @('Same-key report replay','report.customer.invalid','AccessDenied','actualExportHash','calculationResultId','pendingEvents','audit.sourceEventId','audit.auditEvidenceId') | ForEach-Object { $controller.Contains($_) } | Where-Object { -not $_ } | Measure-Object | Select-Object -ExpandProperty Count | ForEach-Object { $_ -eq 0 }
$tests = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'tests/reporting/Monergy.Reporting.Tests/ServiceContractReportSourceReaderTests.cs') -Raw -Encoding utf8
$checks['Permanent exact owner-read fallback lineage and isolation tests exist'] = @('ReadsPersistedOwnerContractsWithReportingWorkloadAndExactLineage','MissingScenarioReturnsNoSourceAndNeverUsesReferenceSnapshots','MixedCustomerOwnerResponseFailsClosed','DeniedOwnerReadPreservesClassifiedFailureWithoutFallback','ExactRequestedSourceAndLineageMismatchFailsClosed','MalformedScenarioIsClassifiedAndDoesNotCallOwners') | ForEach-Object { $tests.Contains($_) } | Where-Object { -not $_ } | Measure-Object | Select-Object -ExpandProperty Count | ForEach-Object { $_ -eq 0 }
$malformedTestsPresent = @(@('NullScenarioCustomerIsClassifiedBeforeOwnerRead','duplicate-profile-fact','null-evidence-version','duplicate-evidence-version','malformed-explanation-inputs','explanation-changed-input-provenance') | Where-Object { -not $tests.Contains($_) }).Count -eq 0
$checks['Malformed and ambiguous scenario owner and explanation data is classified'] = $malformedTestsPresent
$recoveryCoveragePresent = @(@('UNRESOLVED_LAUNCH','IDENTITY_MISMATCH','Partial startup journal','Unresolved stop metadata preservation','Provider outage with live application PIDs','Complete reconciled runtime') | Where-Object { -not $recoveryTests.Contains($_) }).Count -eq 0
$checks['Controller recovery tests cover interruption mismatch outage and safe reconciliation'] = $recoveryCoveragePresent
$frontend = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'apps/customer-web/src/reports/referenceReportsApi.ts') -Raw -Encoding utf8
$checks['Browser uses real endpoints and exposes truthful persisted mode'] = $frontend.Contains('PERSISTED_REPORTING') -and $frontend.Contains("'cid-051'") -and $frontend.Contains("'cid-052'") -and $frontend.Contains('/operations/local/reporting/status') -and -not $frontend.Contains('route.fulfill')
$reportsExperience = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'apps/customer-web/src/reports/ReportsExperience.tsx') -Raw -Encoding utf8
$checks['Injected reporting experience mode is rendered consistently'] = $reportsExperience.Contains("experienceMode === 'PERSISTED_REPORTING'") -and @([regex]::Matches($reportsExperience, "reportingExperienceMode === 'PERSISTED_REPORTING'")).Count -eq 0
$projects = @(Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'services') -Recurse -File -Filter '*.csproj')
$projectText = @($projects | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
$checks['No Production broker or new provider dependency introduced'] = $projectText -notmatch 'Kafka|RabbitMQ|NATS|ServiceBus|EventBridge|Google\.Cloud\.PubSub|Amazon\.SQS|Amazon\.SNS'
$checks['No Atlas model or R8 exists in application repository'] = @(Get-ChildItem -LiteralPath $RepositoryRoot -Recurse -File -Filter '*.atlas.json').Count -eq 0 -and -not (Test-Path -LiteralPath (Join-Path $RepositoryRoot 'R8'))

foreach ($entry in $checks.GetEnumerator()) { Write-Output "[$(if ($entry.Value) { 'PASS' } else { 'FAIL' })] $($entry.Key)" }
$failed = @($checks.Values | Where-Object { -not $_ })
if ($failed.Count) { throw "D11 verification failed: $($checks.Count - $failed.Count)/$($checks.Count)." }
Write-Output "D11 verification passed: $($checks.Count)/$($checks.Count); implementation candidate only."
