[CmdletBinding()]
param(
    [switch]$SelfTest,
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Test-ExactSet([object[]]$Actual, [object[]]$Expected) {
    [string[]]$actualValues = @($Actual | ForEach-Object { [string]$_ })
    [string[]]$expectedValues = @($Expected | ForEach-Object { [string]$_ })
    [Array]::Sort($actualValues, [StringComparer]::Ordinal)
    [Array]::Sort($expectedValues, [StringComparer]::Ordinal)
    $actualValues.Count -eq $expectedValues.Count -and
        ($actualValues -join '|') -ceq ($expectedValues -join '|')
}

function Test-Scope([object]$Value) {
    $Value.deliverable -ceq 'MWP-03-D15' -and
    $Value.status -ceq 'CANDIDATE_PENDING_CTO_REVIEW' -and
    $Value.feature.id -ceq 'M2-WS01-E02-F01' -and
    $Value.feature.readiness -ceq 'READY' -and
    $Value.startingCommits.application -ceq '18936b7a3c271cfc3dc4fe9d18d5bb15ad54e9e7' -and
    $Value.startingCommits.architecture -ceq 'a9ce12ecd76a1dfd4bdca6aaea0fa46a064546b7' -and
    $Value.startingCommits.systemExpert -ceq '8a2f01ec68f9bcc2b35d45c59e9d6f8a478b8eed' -and
    $Value.contractTrace -ceq 'INTERNAL_ONLY' -and
    @($Value.changedDomainServices).Count -eq 0 -and
    @($Value.newOrChangedContracts).Count -eq 0 -and
    $Value.businessObjectChanges -ceq 'NONE' -and
    $Value.databaseSchemaChanges -ceq 'NONE' -and
    $Value.frontendProductBehaviorChanges -ceq 'NONE' -and
    $Value.productionProviderSelection -ceq 'NONE' -and
    $Value.runtimeHostingSelection -ceq 'NONE' -and
    $Value.dependencyChange -ceq 'NONE' -and
    $Value.containerBaseImageChange -ceq 'NONE' -and
    $Value.fullRegressionTrigger -ceq 'GOVERNED_MANUAL_WORKFLOW_DISPATCH' -and
    $Value.recurringSchedule -ceq 'NONE' -and
    $Value.historicalSemantics -ceq 'D01_THROUGH_D14_PRESERVED' -and
    $Value.stageGates.'SG-01' -ceq 'READY' -and
    $Value.stageGates.'SG-02' -ceq 'CONDITIONALLY_READY' -and
    $Value.stageGates.'SG-03' -ceq 'BLOCKED' -and
    $Value.stageGates.'SG-04' -ceq 'BLOCKED'
}

$scopePath = Join-Path $RepositoryRoot 'build/governance/d15-scope-lock.json'
$mapPath = Join-Path $RepositoryRoot 'build/governance/ci-impact-map.json'
$classifierPath = Join-Path $RepositoryRoot 'build/ci/Get-CiImpact.ps1'
$workflowPath = Join-Path $RepositoryRoot '.github/workflows/bootstrap.yml'
$scope = Get-Content -LiteralPath $scopePath -Raw -Encoding utf8 | ConvertFrom-Json
$map = Get-Content -LiteralPath $mapPath -Raw -Encoding utf8 | ConvertFrom-Json
$workflow = Get-Content -LiteralPath $workflowPath -Raw -Encoding utf8

function Get-Impact([string[]]$Paths, [string]$Mode = 'PullRequest') {
    $root = Join-Path $RepositoryRoot '.artifacts/d15/self-test'
    if (-not (Test-Path -LiteralPath $root)) { New-Item -ItemType Directory -Path $root -Force | Out-Null }
    $output = Join-Path $root "$([Guid]::NewGuid().ToString('N')).json"
    try {
        $null = & $classifierPath -ChangedPath $Paths -Mode $Mode -OutputPath $output -RepositoryRoot $RepositoryRoot
        return Get-Content -LiteralPath $output -Raw -Encoding utf8 | ConvertFrom-Json
    }
    finally {
        if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Force }
    }
}

function Test-NoBusinessVerifier([object]$Impact) {
    @($Impact.verifiers | Where-Object { $_ -match '^D(?:0[3-9]|1[0-4])$' }).Count -eq 0
}

function Test-NoCandidateInventoryCoupling([string]$Content) {
    $gitDiffInvocation = 'git -C $RepositoryRoot ' + 'diff'
    $gitUntrackedInvocation = 'git -C $RepositoryRoot ' + 'ls-files'
    $candidateInventoryAssertion = 'Candidate inventory remains ' + 'delivery and toolchain only'
    -not $Content.Contains($gitDiffInvocation) -and
        -not $Content.Contains($gitUntrackedInvocation) -and
        -not $Content.Contains($candidateInventoryAssertion)
}

function Get-OciPlan([string[]]$ServiceIds) {
    $arguments = @{
        RepositoryRoot = $RepositoryRoot
        PlanOnly = $true
    }
    if (@($ServiceIds).Count -gt 0) { $arguments.ServiceId = $ServiceIds }
    $output = @(& (Join-Path $RepositoryRoot 'build/hosted-oci-evidence.ps1') @arguments)
    return ([string]$output[-1]) | ConvertFrom-Json
}

if ($SelfTest) {
    $customer = Get-Impact @('services/customer-identity/Application/CustomerIdentityApplication.cs')
    $reporting = Get-Impact @('services/reporting/Application/ReportingApplication.cs')
    $financialRules = Get-Impact @('services/financial-rules/Domain/EngineeringRules.cs')
    $customerContracts = Get-Impact @('contracts/Monergy.Contracts/D14TrustedSessionContracts.cs')
    $financialProfileContracts = Get-Impact @('contracts/Monergy.Contracts/D04ContractCatalog.cs')
    $financialRulesContracts = Get-Impact @('contracts/Monergy.Contracts/FinancialRulesContracts.cs')
    $integrationContracts = Get-Impact @('contracts/Monergy.Contracts/D06ContractCatalog.cs')
    $searchContracts = Get-Impact @('contracts/Monergy.Contracts/D07ContractCatalog.cs')
    $reportingContracts = Get-Impact @('contracts/Monergy.Contracts/D08ContractCatalog.cs')
    $jobAuditContracts = Get-Impact @('contracts/Monergy.Contracts/D10ContractCatalog.cs')
    $documentReprocessingContracts = Get-Impact @('contracts/Monergy.Contracts/D12DocumentReprocessingContracts.cs')
    $documentContractCatalog = Get-Impact @('contracts/Monergy.Contracts/D12ContractCatalog.cs')
    $sharedContractPrimitives = Get-Impact @('contracts/Monergy.Contracts/ContractPrimitives.cs')
    $frontend = Get-Impact @('apps/customer-web/src/App.tsx')
    $migration = Get-Impact @('services/evidence/migrations/0003_candidate.sql')
    $docker = Get-Impact @('services/reporting/Dockerfile')
    $runtime = Get-Impact @('global.json')
    $impactedOciPlan = Get-OciPlan @('reporting')
    $fullOciPlan = Get-OciPlan @()
    $package = Get-Impact @('pnpm-lock.yaml')
    $workflowOnly = Get-Impact @('.github/workflows/bootstrap.yml')
    $documentation = Get-Impact @('README.md', 'docs/quality.md')
    $unmapped = Get-Impact @('runtime/new-host/Program.cs')
    $full = Get-Impact @('.github/workflows/bootstrap.yml') 'FullRegression'
    $merge = Get-Impact @('services/customer-identity/Application/CustomerIdentityApplication.cs') 'MergeIntegrity'
    $d13Verifier = Get-Impact @('build/verify-customer-identity-boundary.ps1')
    $d14Verifier = Get-Impact @('build/verify-trusted-session-lifecycle.ps1')
    $d16Release = Get-Impact @('build/release/New-ImmutableArtifact.ps1')
    $d05Release = Get-Impact @('build/release/New-CandidateManifest.ps1')
    $d13Text = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/verify-customer-identity-boundary.ps1') -Raw -Encoding utf8
    $d14Text = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/verify-trusted-session-lifecycle.ps1') -Raw -Encoding utf8
    $verifierText = Get-Content -LiteralPath $PSCommandPath -Raw -Encoding utf8

    $mutatedScope = $scope | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $mutatedScope.productionProviderSelection = 'SELECTED'
    $advancedScope = $scope | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $advancedScope.stageGates.'SG-02' = 'READY'

    $checks = [ordered]@{
        '1 Customer Identity source routes focused verification' =
            $customer.services -contains 'customer-identity' -and $customer.verifiers -contains 'D13' -and $customer.verifiers -contains 'D14'
        '2 Reporting source routes Reporting and D11 verification' =
            $reporting.services -contains 'reporting' -and $reporting.verifiers -contains 'D08' -and $reporting.gates.d11
        '3 Financial Rules source routes Financial Rules verification' =
            $financialRules.services -contains 'financial-rules' -and $financialRules.verifiers -contains 'D05'
        '4 Customer Identity contracts fan out to the exact owner and consumer set' =
            (Test-ExactSet @($customerContracts.services) @('customer-identity','consent','reporting','financial-profile','integration-gateway','audit'))
        '5 Financial Profile contracts fan out to the exact owner and consumer set' =
            (Test-ExactSet @($financialProfileContracts.services) @('financial-profile','document-intelligence','integration-gateway','financial-rules','search-retrieval','reporting','audit'))
        '6 Financial Rules contracts fan out to the exact owner and consumer set' =
            (Test-ExactSet @($financialRulesContracts.services) @('financial-rules','reporting','ai-intelligence','search-retrieval','audit'))
        '7 Integration Gateway contracts fan out to the exact owner and consumer set' =
            (Test-ExactSet @($integrationContracts.services) @('integration-gateway','customer-identity','evidence','financial-profile','document-intelligence','job-management','audit'))
        '8 Search contracts fan out to the exact owner and consumer set' =
            (Test-ExactSet @($searchContracts.services) @('search-retrieval','ai-intelligence','reporting','job-management'))
        '9 Reporting contracts fan out to the exact owner and consumer set' =
            (Test-ExactSet @($reportingContracts.services) @('reporting','job-management','audit'))
        '10 D10 catalog fans out to all governed services' =
            (Test-ExactSet @($jobAuditContracts.services) @($map.services.id))
        '11 Focused D12 payload contracts retain the exact four-service route' =
            (Test-ExactSet @($documentReprocessingContracts.services) @('document-intelligence','evidence','job-management','audit'))
        '12 Broad D12 catalog fans out to all governed services' =
            (Test-ExactSet @($documentContractCatalog.services) @($map.services.id))
        '13 Shared contract primitives fan out to all governed services' =
            (Test-ExactSet @($sharedContractPrimitives.services) @($map.services.id))
        '14 Frontend change enables component and browser verification' =
            $frontend.gates.frontendTests -and $frontend.gates.browser
        '15 Migration change enables physical persistence verification' =
            $migration.gates.persistence -and $migration.verifiers -contains 'D09'
        '16 Container and runtime pins select appropriate OCI security scope' =
            (Test-ExactSet @($docker.ociServices) @('reporting')) -and @($runtime.ociServices).Count -eq 12 -and $runtime.gates.dependencySecurity -and
            $impactedOciPlan.scope -ceq 'IMPACTED' -and (Test-ExactSet @($impactedOciPlan.selectedServices) @('reporting')) -and
            $fullOciPlan.scope -ceq 'FULL' -and @($fullOciPlan.selectedServices).Count -eq 12
        '17 Package lock enables dependency security verification' = $package.gates.dependencySecurity
        '18 Workflow-only change excludes unrelated product suites' =
            @($workflowOnly.services).Count -eq 0 -and (Test-NoBusinessVerifier $workflowOnly)
        '19 Documentation-only change excludes heavyweight gates' =
            $documentation.documentationOnly -and -not $documentation.gates.browser -and -not $documentation.gates.persistence -and -not $documentation.gates.oci
        '20 Unmapped runtime path fails safe to full regression' =
            $unmapped.fullRegression -and $unmapped.unmappedPaths -contains 'runtime/new-host/Program.cs'
        '21 FULL_REGRESSION selects all historical and systemic gates' =
            $full.fullRegression -and @($full.services).Count -eq 12 -and @($full.verifiers).Count -eq 16 -and @($full.ociServices).Count -eq 12 -and
            $full.gates.browser -and $full.gates.persistence -and $full.gates.jobAudit -and $full.gates.d11
        '22 Ordinary CI-only PR is not selected by deliverable number' =
            $workflowOnly.effectiveMode -ceq 'PullRequest' -and -not $workflowOnly.fullRegression -and (Test-NoBusinessVerifier $workflowOnly)
        '23 Main merge-integrity remains narrower than FULL_REGRESSION' =
            $merge.effectiveMode -ceq 'MergeIntegrity' -and -not $merge.fullRegression -and @($merge.verifiers).Count -lt @($full.verifiers).Count
        '24 Direct-push detection remains represented' =
            $workflow.Contains('Direct push to main detected') -and $workflow.Contains('/commits/$env:MONERGY_COMMIT_SHA/pulls')
        '25 D13 and D14 verifier changes route only their corresponding verifier' =
            (Test-ExactSet @($d13Verifier.verifiers) @('D13')) -and
            (Test-ExactSet @($d14Verifier.verifiers) @('D14'))
        '26 D13 and D14 no longer depend on current workflow topology' =
            -not $d13Text.Contains('Test-HostedWorkflow') -and -not $d13Text.Contains('Verify D01-D13 controlled implementation') -and
            -not $d14Text.Contains("'.github/workflows'") -and -not $d14Text.Contains('$workflowChanges')
        '27 D15 policy verification is independent of candidate inventory' =
            (Test-NoCandidateInventoryCoupling $verifierText)
        '28 Provider and stage-gate changes are rejected' =
            (Test-Scope $scope) -and -not (Test-Scope $mutatedScope) -and -not (Test-Scope $advancedScope)
        '29 D16 release paths select D01 D02 D15 and D16 without heavy product lanes' =
            (Test-ExactSet @($d16Release.verifiers) @('D01','D02','D15','D16')) -and
            @($d16Release.services).Count -eq 0 -and -not $d16Release.gates.browser -and -not $d16Release.gates.persistence -and -not $d16Release.gates.oci
        '30 D05 historical manifest changes still select D05 verification' =
            (Test-ExactSet @($d05Release.verifiers) @('D01','D02','D05','D15'))
    }
    foreach ($entry in $checks.GetEnumerator()) {
        Write-Output "SELF-TEST $(if ($entry.Value) { 'PASS' } else { 'FAIL' }): $($entry.Key)"
    }
    $failed = @($checks.GetEnumerator() | Where-Object { -not $_.Value })
    if ($failed.Count) { throw "D15 negative/self-tests failed: $($checks.Count - $failed.Count)/$($checks.Count)." }
    Write-Output "D15 negative/self-tests passed: $($checks.Count)/$($checks.Count)."
    exit 0
}

$checks = [ordered]@{}
$checks['Bounded D15 scope preserves governance and non-claims'] = Test-Scope $scope
$checks['Impact map has twelve exact service boundaries and fail-safe policy'] =
    $map.unmappedProductionPolicy -ceq 'FULL_REGRESSION' -and
    (Test-ExactSet @($map.services.id) @('customer-identity','consent','integration-gateway','evidence','document-intelligence','financial-profile','financial-rules','search-retrieval','ai-intelligence','reporting','job-management','audit'))
$checks['Full regression preserves D01 through D16 and all twelve OCI services'] =
    (Test-ExactSet @($map.fullRegression.verifiers) @(1..16 | ForEach-Object { 'D{0:D2}' -f $_ })) -and
    @($map.fullRegression.testProjects).Count -ge 10
$checks['Workflow exposes only impacted or governed manual full-regression modes'] =
    $workflow.Contains('verification_mode:') -and $workflow.Contains('full_regression') -and
    $workflow.Contains('Get-CiImpact.ps1') -and -not ($workflow -match '(?m)^\s*schedule:\s*$')
$checks['Universal gate retains restore format lint build architecture secret and D15 controls'] =
    @('Task Restore','Task FormatCheck','Task Lint','Task Build','Task ArchitectureTest','Task SecretScan','Task D15Verification') |
        Where-Object { $workflow.Contains($_) } |
        Measure-Object | Select-Object -ExpandProperty Count | ForEach-Object { $_ -eq 7 }
$checks['Conditional heavy gates are driven by classifier outputs'] =
    @('run_browser','run_persistence','run_job_audit','run_d11','run_oci','run_dependency_security') |
        Where-Object { $workflow.Contains($_) } |
    Measure-Object | Select-Object -ExpandProperty Count | ForEach-Object { $_ -eq 6 }
$ociSource = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/hosted-oci-evidence.ps1') -Raw -Encoding utf8
$checks['OCI evidence supports impacted subsets and complete default capability'] =
    $ociSource.Contains('[string[]]$ServiceId') -and
    $ociSource.Contains('[switch]$PlanOnly') -and
    $ociSource.Contains('if ($requested.Count -eq 0)') -and
    $ociSource.Contains('selectedServices = @($services.id)') -and
    $ociSource.Contains('$matrix.buildPass -ne $services.Count')
$checks['Direct-push detection remains a warning compensating control'] =
    $workflow.Contains('Direct push to main detected') -and $workflow.Contains('/commits/$env:MONERGY_COMMIT_SHA/pulls')

foreach ($entry in $checks.GetEnumerator()) {
    Write-Output "[$(if ($entry.Value) { 'PASS' } else { 'FAIL' })] $($entry.Key)"
}
$failed = @($checks.GetEnumerator() | Where-Object { -not $_.Value })
if ($failed.Count) { throw "D15 verification failed: $($checks.Count - $failed.Count)/$($checks.Count)." }
Write-Output "D15 verification passed: $($checks.Count)/$($checks.Count)."
