[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$SelfTest,
    [switch]$D09ForwardRegression
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Test-ExactSet {
    param([object[]]$Actual, [object[]]$Expected)
    $actualText = (@($Actual | ForEach-Object { [string]$_ } | Sort-Object) -join '|')
    $expectedText = (@($Expected | ForEach-Object { [string]$_ } | Sort-Object) -join '|')
    return $actualText -ceq $expectedText
}

function Test-SecretText {
    param([string]$Content)
    $assignment = '(?im)(password|secret|token|api[_-]?key|private[_-]?key)\s*[:=]\s*["''][^"''$\s][^"'']+["'']'
    return ($Content -match $assignment) -or
        ($Content -match '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----') -or
        ($Content -match '\bAKIA[0-9A-Z]{16}\b')
}

if ($SelfTest) {
    $unsafeCredentialFixture = 'pass' + 'word = "' + 'unsafe-value' + '"'
    $tests = [ordered]@{
        'exact set accepted' = (Test-ExactSet @('a', 'b') @('b', 'a'))
        'duplicate rejected' = (-not (Test-ExactSet @('a', 'a') @('a', 'b')))
        'missing item rejected' = (-not (Test-ExactSet @('a') @('a', 'b')))
        'literal credential rejected' = (Test-SecretText $unsafeCredentialFixture)
        'empty secret references accepted' = (-not (Test-SecretText '"secretReferences": []'))
        'private key rejected' = (Test-SecretText ('-----BEGIN PRIVATE ' + 'KEY-----'))
    }
    $failures = 0
    foreach ($test in $tests.GetEnumerator()) {
        if ($test.Value) { Write-Output "SELF-TEST PASS: $($test.Key)" }
        else { Write-Output "SELF-TEST FAIL: $($test.Key)"; $failures++ }
    }
    if ($failures -gt 0) { throw "Bootstrap verifier self-tests failed: $failures/$($tests.Count)." }
    Write-Output "Bootstrap verifier self-tests passed: $($tests.Count)/$($tests.Count)."
    exit 0
}

$checks = New-Object System.Collections.Generic.List[object]
function Add-Check {
    param([string]$Name, [bool]$Passed, [string]$Evidence)
    $checks.Add([pscustomobject]@{ Name = $Name; Passed = $Passed; Evidence = $Evidence }) | Out-Null
}

$expectedRoot = @('apps', 'services', 'contracts', 'shared', 'tests', 'build')
$ignoredGeneratedRoots = @('node_modules', 'playwright-report', 'test-results')
$actualRoot = @(Get-ChildItem -LiteralPath $RepositoryRoot -Directory | Where-Object { $_.Name -notmatch '^\.' -and $_.Name -notin $ignoredGeneratedRoots } | Select-Object -ExpandProperty Name)
Add-Check 'D05 root responsibilities' (Test-ExactSet $actualRoot $expectedRoot) ($actualRoot -join ', ')

$expectedServiceIds = @(
    'customer-identity', 'consent', 'integration-gateway', 'evidence',
    'document-intelligence', 'financial-profile', 'financial-rules',
    'search-retrieval', 'ai-intelligence', 'reporting', 'job-management', 'audit'
)
$serviceCatalog = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/catalog.json') -Raw | ConvertFrom-Json
$serviceIds = @($serviceCatalog.services | ForEach-Object { $_.id })
Add-Check 'Twelve R3 service identities' (Test-ExactSet $serviceIds $expectedServiceIds) "$($serviceIds.Count) service entries"
Add-Check 'Unique service artifact identities' (@($serviceCatalog.services.artifact | Select-Object -Unique).Count -eq 12) '12 independently named artifacts'
$expectedVs02Features = [ordered]@{
    'evidence' = @('M2-WS03-E01-F02')
    'document-intelligence' = @('M2-WS03-E02-F01', 'M2-WS03-E02-F02')
    'financial-profile' = @('M2-WS03-E03-F01', 'M2-WS03-E03-F02', 'M2-WS03-E03-F03', 'M2-WS04-E03-F01', 'M2-WS04-E03-F02')
    'job-management' = @()
    'audit' = @()
}
$vs02StateValid = $true
foreach ($serviceId in $expectedVs02Features.Keys) {
    $service = @($serviceCatalog.services | Where-Object id -CEQ $serviceId)
    $vs02StateValid = $vs02StateValid -and $service.Count -eq 1
    if ($service.Count -eq 1) {
        $expectedImplementation = if ($serviceId -in @('job-management', 'audit')) { 'SUPPORTING_BOUNDARY' } else { 'IMPLEMENTATION_CANDIDATE' }
        $vs02StateValid = $vs02StateValid -and
            $service[0].status -ceq 'VS02_IMPLEMENTATION_CANDIDATE' -and
            $service[0].featureImplementation -ceq $expectedImplementation -and
            (Test-ExactSet @($service[0].featureIds) $expectedVs02Features[$serviceId])
    }
}

$expectedD05Features = @(
    'M2-WS05-E01-F01', 'M2-WS05-E01-F02', 'M2-WS05-E01-F03',
    'M2-WS05-E02-F01', 'M2-WS05-E02-F02', 'M2-WS05-E02-F03'
)
$financialRules = @($serviceCatalog.services | Where-Object id -CEQ 'financial-rules')
$financialRulesStateValid = $financialRules.Count -eq 1 -and
    $financialRules[0].status -ceq 'D05_IMPLEMENTATION_ACCEPTED_SIMULATOR' -and
    $financialRules[0].featureImplementation -ceq 'IMPLEMENTATION_ACCEPTED_SIMULATOR' -and
    @($financialRules[0].featureIds).Count -eq 0 -and
    $financialRules[0].d05Status -ceq 'IMPLEMENTATION_ACCEPTED_SIMULATOR' -and
    (Test-ExactSet @($financialRules[0].d05FeatureIds) $expectedD05Features)

$expectedD06Features = @(
    'M2-WS06-E01-F01', 'M2-WS06-E01-F02', 'M2-WS06-E01-F03',
    'M2-WS06-E02-F01', 'M2-WS06-E02-F02', 'M2-WS06-E02-F03'
)
$integrationGateway = @($serviceCatalog.services | Where-Object id -CEQ 'integration-gateway')
$integrationGatewayStateValid = $integrationGateway.Count -eq 1 -and
    $integrationGateway[0].status -ceq 'D06_IMPLEMENTATION_CANDIDATE_SIMULATOR' -and
    $integrationGateway[0].featureImplementation -ceq 'IMPLEMENTATION_CANDIDATE_SIMULATOR' -and
    (Test-ExactSet @($integrationGateway[0].featureIds) $expectedD06Features)

$expectedD07Features = @('M2-WS07-E01-F01', 'M2-WS07-E01-F02', 'M2-WS07-E01-F03', 'M2-WS07-E02-F01', 'M2-WS07-E02-F03')
$searchRetrieval = @($serviceCatalog.services | Where-Object id -CEQ 'search-retrieval')
$searchRetrievalStateValid = $searchRetrieval.Count -eq 1 -and
    $searchRetrieval[0].status -ceq 'D07_IMPLEMENTATION_CANDIDATE_SIMULATOR' -and
    $searchRetrieval[0].featureImplementation -ceq 'IMPLEMENTATION_CANDIDATE_SIMULATOR' -and
    (Test-ExactSet @($searchRetrieval[0].featureIds) $expectedD07Features)

$expectedD08Features = @('M2-WS08-E01-F02', 'M2-WS08-E02-F01', 'M2-WS08-E02-F03')
$reporting = @($serviceCatalog.services | Where-Object id -CEQ 'reporting')
$reportingStateValid = $reporting.Count -eq 1 -and
    $reporting[0].status -ceq 'D08_IMPLEMENTATION_CANDIDATE_SIMULATOR' -and
    $reporting[0].featureImplementation -ceq 'IMPLEMENTATION_CANDIDATE_SIMULATOR' -and
    (Test-ExactSet @($reporting[0].featureIds) $expectedD08Features)

$expectedScaffoldIds = @('customer-identity', 'consent', 'ai-intelligence')
$scaffoldStateValid = $true
foreach ($serviceId in $expectedScaffoldIds) {
    $service = @($serviceCatalog.services | Where-Object id -CEQ $serviceId)
    $scaffoldStateValid = $scaffoldStateValid -and $service.Count -eq 1
    if ($service.Count -eq 1) {
        $scaffoldStateValid = $scaffoldStateValid -and
            $service[0].status -ceq 'TOOLCHAIN_SCAFFOLD' -and
            $service[0].featureImplementation -ceq 'NONE' -and
            @($service[0].featureIds).Count -eq 0
    }
}

$serviceStateManifest = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'repository.manifest.json') -Raw | ConvertFrom-Json
$d06Scope = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/governance/d06-scope-lock.json') -Raw | ConvertFrom-Json
$d06GovernanceValid = $serviceStateManifest.d06Status -ceq 'CANDIDATE_PENDING_CTO_REVIEW' -and
    $serviceStateManifest.d06FeatureState -ceq 'IMPLEMENTATION_CANDIDATE_SIMULATOR_6_OF_6' -and
    $serviceStateManifest.d06EvidenceLevel -ceq 'SIMULATOR_CANDIDATE' -and
    $serviceStateManifest.integrationEvidenceLevel -ceq 'SIMULATOR_REFERENCE_ADAPTER' -and
    (Test-ExactSet @($serviceStateManifest.d06OutstandingDecisions) @('OD-04', 'OD-05', 'OD-06')) -and
    $serviceStateManifest.stageGates.'SG-01' -ceq 'READY' -and
    $serviceStateManifest.stageGates.'SG-02' -ceq 'CONDITIONALLY_READY' -and
    $serviceStateManifest.stageGates.'SG-03' -ceq 'BLOCKED' -and
    $serviceStateManifest.stageGates.'SG-04' -ceq 'BLOCKED' -and
    $d06Scope.status -ceq 'CANDIDATE_PENDING_CTO_REVIEW' -and
    $d06Scope.evidenceLevel -ceq 'SIMULATOR' -and
    (Test-ExactSet @($d06Scope.features.id) $expectedD06Features) -and
    $d06Scope.providerSelection -ceq 'UNRESOLVED' -and
    $d06Scope.physicalPersistenceOrBroker -ceq 'NOT_SELECTED' -and
    $d06Scope.outstandingDecisions.'OD-04' -ceq 'UNRESOLVED' -and
    $d06Scope.outstandingDecisions.'OD-05' -ceq 'UNRESOLVED' -and
    $d06Scope.outstandingDecisions.'OD-06' -ceq 'UNRESOLVED' -and
    $d06Scope.stageGates.'SG-01' -ceq 'READY' -and
    $d06Scope.stageGates.'SG-02' -ceq 'CONDITIONALLY_READY' -and
    $d06Scope.stageGates.'SG-03' -ceq 'BLOCKED' -and
    $d06Scope.stageGates.'SG-04' -ceq 'BLOCKED' -and
    $d06Scope.architectureIntegrity.'R1-R7' -ceq 'FROZEN_UNCHANGED' -and
    $d06Scope.architectureIntegrity.R8 -ceq 'ABSENT'

$serviceStateValid = $vs02StateValid -and $financialRulesStateValid -and
    $integrationGatewayStateValid -and $searchRetrievalStateValid -and $reportingStateValid -and $scaffoldStateValid -and $d06GovernanceValid
Add-Check 'Controlled service realization state' $serviceStateValid 'Five VS-02 boundaries; D05 Rules accepted; D06 Gateway, D07 Search and D08 Reporting candidates; three scaffolds; SIMULATOR and governance limits preserved'

$serviceFoldersValid = $true
$migrationFoldersValid = $true
foreach ($serviceId in $expectedServiceIds) {
    $serviceFoldersValid = $serviceFoldersValid -and (Test-Path -LiteralPath (Join-Path $RepositoryRoot "services/$serviceId"))
    $migrationFoldersValid = $migrationFoldersValid -and (Test-Path -LiteralPath (Join-Path $RepositoryRoot "services/$serviceId/migrations"))
}
Add-Check 'Service source folders' $serviceFoldersValid 'All 12 governed folders exist'
Add-Check 'Service-owned migration scaffolds' $migrationFoldersValid 'All 12 services own a migration history location'

$sqlFiles = @(Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'services') -Recurse -File -Filter '*.sql')
if ($D09ForwardRegression) {
    $authorized = @($sqlFiles | Where-Object { $_.FullName -match '[\\/](evidence|financial-profile|financial-rules|reporting|audit)[\\/]migrations[\\/]' })
    Add-Check 'D09-authorized service migrations only' ($sqlFiles.Count -eq 9 -and $authorized.Count -eq 9) 'Exact five-service D09 cohort; historical default remains zero migrations'
} else {
    Add-Check 'No speculative migrations' ($sqlFiles.Count -eq 0) "$($sqlFiles.Count) SQL migration files"
}

$appCatalog = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'apps/catalog.json') -Raw | ConvertFrom-Json
Add-Check 'D05 application boundaries' (Test-ExactSet @($appCatalog.applications.folder) @('customer-web', 'administration-web')) 'Two reserved applications'

$contractFolders = @(Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'contracts') -Directory | Select-Object -ExpandProperty Name)
Add-Check 'D03 contract taxonomy and implementation homes' (Test-ExactSet $contractFolders @('queries', 'commands', 'events', 'integration', 'security-context', 'Monergy.Contracts', 'schemas')) ($contractFolders -join ', ')

$gateCatalog = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/ci/gates.json') -Raw | ConvertFrom-Json
$expectedGateIds = 1..11 | ForEach-Object { 'CG-{0:D2}' -f $_ }
$expectedGateCategories = @(
    'Source/static validation', 'Compile/build', 'Unit verification', 'Secret scanning',
    'Source security analysis', 'Dependency/composition/license analysis',
    'Contract compatibility', 'Migration validation', 'Artifact creation',
    'Artifact vulnerability validation', 'Integration verification'
)
Add-Check 'Eleven CI gate identities' (Test-ExactSet @($gateCatalog.gates.id) $expectedGateIds) 'CG-01 through CG-11'
Add-Check 'Eleven D05 gate categories' (Test-ExactSet @($gateCatalog.gates.category) $expectedGateCategories) 'All categories preserved'
$allowedRealization = @('IMPLEMENTED_EXECUTABLE', 'CONFIGURED_REQUIRES_EXTERNAL_INFRASTRUCTURE', 'DEFERRED_OPEN_DECISION')
Add-Check 'CI realization vocabulary' (@($gateCatalog.gates | Where-Object { $_.realization -notin $allowedRealization }).Count -eq 0) 'Three controlled realization states'
$allowedResults = @('PASS', 'FAIL', 'BLOCKED', 'NOT_RUN', 'NOT_APPLICABLE')
Add-Check 'D07 result vocabulary' (@($gateCatalog.gates | Where-Object { $_.result -notin $allowedResults }).Count -eq 0) 'Five accepted evidence states'

$pipeline = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/ci/pipeline.json') -Raw | ConvertFrom-Json
Add-Check 'Five D05 pipeline stages' (Test-ExactSet @($pipeline.stages.name) @('Change Validation', 'Security Validation', 'Contract & Persistence Validation', 'Artifact Creation', 'Integration Verification')) 'Five logical stages'
$hostedPipelineValid = $pipeline.hostedExecution -ceq 'GITHUB_ACTIONS'
Add-Check 'Hosted CI realization' $hostedPipelineValid 'GitHub Actions executes portable D05 gate semantics'
$pipelineGateIds = @($pipeline.stages | ForEach-Object { $_.gateIds })
Add-Check 'CI gate stage coverage' (Test-ExactSet $pipelineGateIds $expectedGateIds) 'Each gate appears exactly once'

$workflowPath = Join-Path $RepositoryRoot '.github/workflows/bootstrap.yml'
$workflowText = if (Test-Path -LiteralPath $workflowPath) { Get-Content -LiteralPath $workflowPath -Raw } else { '' }
$workflowValid = $workflowText -match 'Invoke-Toolchain\.ps1 -Task Restore' -and
    $workflowText -match 'Invoke-Toolchain\.ps1 -Task D02Verification' -and
    $workflowText -match 'actions/checkout@[0-9a-f]{40}' -and
    $workflowText -match '(?m)^\s*contents:\s*read\s*$' -and
    $workflowText -match '(?m)^\s*pull-requests:\s*read\s*$' -and
    $workflowText.Contains('Direct push to main detected') -and
    $workflowText.Contains('/commits/$env:MONERGY_COMMIT_SHA/pulls')
Add-Check 'Hosted bootstrap workflow' $workflowValid 'Commit-pinned checkout, least privilege, complete bootstrap, and direct-push detection'

$codeOwnersPath = Join-Path $RepositoryRoot '.github/CODEOWNERS'
$codeOwners = if (Test-Path -LiteralPath $codeOwnersPath) { (Get-Content -LiteralPath $codeOwnersPath -Raw).Trim() } else { '' }
Add-Check 'CODEOWNERS governance' ($codeOwners -ceq '* @Magkhan060') 'Repository-wide owner is explicit'

$ownershipPolicy = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/governance/ownership-policy.json') -Raw | ConvertFrom-Json
$expectedExpiryTriggers = @(
    'SECOND_WRITE_CAPABLE_CONTRIBUTOR', 'EXTERNAL_WRITE_ACCESS',
    'SG_02_INTEGRATION_READY', 'UAT_OR_PRODUCTION_PREPARATION',
    'PRODUCTION_ARTIFACT_OR_DEPLOYMENT_CREDENTIAL', 'ENFORCED_REVIEW_REQUIREMENT'
)
$protectionStateValid = $ownershipPolicy.protectedMain -ceq 'NOT_IMPLEMENTED_GITHUB_FREE_PLAN_LIMITATION' -and
    @($ownershipPolicy.codeOwners).Count -eq 1 -and $ownershipPolicy.codeOwners[0] -ceq '@Magkhan060' -and
    $ownershipPolicy.codeOwnersMode -ceq 'ADVISORY_GITHUB_FREE' -and
    $ownershipPolicy.pullRequestPolicy -ceq 'REQUIRED_NOT_ENFORCED' -and
    $ownershipPolicy.directPushDetection -ceq 'GITHUB_ACTIONS_WARNING' -and
    $ownershipPolicy.forcePushPolicy -ceq 'PROHIBITED_NOT_ENFORCED' -and
    $ownershipPolicy.branchDeletionPolicy -ceq 'PROHIBITED_NOT_ENFORCED' -and
    $ownershipPolicy.repositoryBasePermission -ceq 'READ' -and
    $ownershipPolicy.directRepositoryCollaborators -eq 0 -and
    $ownershipPolicy.directRepositoryTeams -eq 0 -and
    (Test-ExactSet @($ownershipPolicy.exceptionExpiryTriggers) $expectedExpiryTriggers) -and
    $ownershipPolicy.bootstrapException.commit -ceq '852bebbe62eccd54be214ba2168d19dfdda467d6'
Add-Check 'GitHub Free control model' $protectionStateValid 'Advisory ownership, minimum access, policy controls, detection, and six expiry triggers'

$exceptionPath = Join-Path $RepositoryRoot 'build/governance/github-free-governance-exception.md'
$exceptionText = if (Test-Path -LiteralPath $exceptionPath) { Get-Content -LiteralPath $exceptionPath -Raw } else { '' }
$exceptionValid = $exceptionText.Contains('NOT_IMPLEMENTED — GITHUB FREE PLAN LIMITATION') -and
    $exceptionText.Contains('cannot technically prevent') -and
    $exceptionText.Contains('expires immediately') -and
    $exceptionText.Contains('GitHub Team')
Add-Check 'GitHub Free exception record' $exceptionValid 'Residual risk, non-enforcement, compensating controls, and expiry are explicit'

$localConfig = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/config/local.example.json') -Raw | ConvertFrom-Json
$ciConfig = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/config/ci.example.json') -Raw | ConvertFrom-Json
$configValid = $localConfig.executionZone -ceq 'LOCAL' -and $ciConfig.executionZone -ceq 'CI_EPHEMERAL' -and
    (-not $localConfig.persistentEnvironment) -and (-not $ciConfig.persistentEnvironment) -and
    @($localConfig.secretReferences).Count -eq 0 -and @($ciConfig.secretReferences).Count -eq 0
Add-Check 'Local and CI configuration isolation' $configValid 'No persistent environment or secret value configured'

$baselineLock = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/governance/architecture-baseline-lock.json') -Raw | ConvertFrom-Json
$lockValid = $baselineLock.requiredPublicationCommit -ceq '193667fc7ad4d7f919f213f9a96260afa0f09fb9' -and
    $baselineLock.mwp02Status -ceq 'CLOSED' -and $baselineLock.sg01 -ceq 'READY' -and
    $baselineLock.sg02 -ceq 'CONDITIONALLY_READY' -and $baselineLock.sg03 -ceq 'BLOCKED' -and
    $baselineLock.sg04 -ceq 'BLOCKED' -and @($baselineLock.frozenModels).Count -eq 7 -and
    $baselineLock.r8ArtifactCount -eq 0
Add-Check 'Governed baseline lock' $lockValid 'MWP-02 closed; stage gates and R1-R7 frozen state preserved'

$repositoryManifest = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'repository.manifest.json') -Raw | ConvertFrom-Json
$manifestValid = $repositoryManifest.repository -ceq 'monergy-application' -and
    $repositoryManifest.productTechnologyDecision -in @('UNRESOLVED', 'RESOLVED_BY_D02_PT_01_THROUGH_PT_11') -and
    $repositoryManifest.status -in @('ACCEPTED_COMPLETE', 'CANDIDATE_PENDING_CTO_REVIEW', 'CTO_APPROVED_CANDIDATE_PENDING_CLOSURE') -and
    $repositoryManifest.hostedRepositoryDecision -ceq 'GITHUB_FREE_EXCEPTION_ACCEPTED' -and
    $repositoryManifest.remote -ceq 'https://github.com/atlas-core-platform/monergy-application.git' -and
    $repositoryManifest.hostedCi -ceq 'GITHUB_ACTIONS' -and
    $repositoryManifest.branchProtection -ceq 'NOT_IMPLEMENTED_GITHUB_FREE_PLAN_LIMITATION' -and
    $repositoryManifest.businessFeatureImplementation -in @('NONE', 'VS02_IMPLEMENTATION_CANDIDATE', 'VS02_IMPLEMENTATION_ACCEPTED_SIMULATOR') -and
    $repositoryManifest.deploymentState -ceq 'NOT_DEPLOYED'
Add-Check 'Truthful repository state' $manifestValid 'D01 controls remain accepted while D03 may truthfully record bounded SIMULATOR acceptance; deployment remains absent'

$observability = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'shared/platform/observability.contract.json') -Raw | ConvertFrom-Json
Add-Check 'Observability bootstrap' (@($observability.requiredEvidence).Count -eq 8 -and @($observability.prohibitedTelemetry).Count -eq 6) 'D05 telemetry and exclusion categories represented'

$releaseTemplate = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/release/product-release-manifest.template.json') -Raw | ConvertFrom-Json
Add-Check 'Release manifest bootstrap' ($releaseTemplate.status -ceq 'TEMPLATE_NOT_A_RELEASE' -and @($releaseTemplate.componentArtifacts).Count -eq 0) 'Template cannot be mistaken for a release'

$approvedDependencyFiles = @('package.json', 'pnpm-lock.yaml', 'Directory.Packages.props', 'global.json')
$approvedTechnologyState = @($approvedDependencyFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $RepositoryRoot $_)) }).Count -eq 0
Add-Check 'Governed product technology state' $approvedTechnologyState 'D02-approved package managers, runtime pins, and dependency locks are present'

$branchValid = $true
if (Test-Path -LiteralPath (Join-Path $RepositoryRoot '.git')) {
    $branch = (& git -C $RepositoryRoot branch --show-current 2>$null)
    $branchValid = $branch -notin @('dev', 'qa', 'uat', 'production')
}
Add-Check 'No environment branch' $branchValid 'Current branch is not a persistent environment name'

$failures = @($checks | Where-Object { -not $_.Passed })
foreach ($check in $checks) {
    $state = if ($check.Passed) { 'PASS' } else { 'FAIL' }
    Write-Output "[$state] $($check.Name) - $($check.Evidence)"
}
if ($failures.Count -gt 0) {
    throw "Bootstrap verification failed: $($checks.Count - $failures.Count)/$($checks.Count); failed: $($failures.Name -join ', ')."
}
Write-Output "Bootstrap verification passed: $($checks.Count)/$($checks.Count)."
