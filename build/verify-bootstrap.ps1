[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$SelfTest
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
$actualRoot = @(Get-ChildItem -LiteralPath $RepositoryRoot -Directory | Where-Object { $_.Name -notmatch '^\.' } | Select-Object -ExpandProperty Name)
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
Add-Check 'Reserved service status' (@($serviceCatalog.services | Where-Object status -cne 'RESERVED').Count -eq 0) 'No service claims Feature implementation'

$serviceFoldersValid = $true
$migrationFoldersValid = $true
foreach ($serviceId in $expectedServiceIds) {
    $serviceFoldersValid = $serviceFoldersValid -and (Test-Path -LiteralPath (Join-Path $RepositoryRoot "services/$serviceId"))
    $migrationFoldersValid = $migrationFoldersValid -and (Test-Path -LiteralPath (Join-Path $RepositoryRoot "services/$serviceId/migrations"))
}
Add-Check 'Service source folders' $serviceFoldersValid 'All 12 governed folders exist'
Add-Check 'Service-owned migration scaffolds' $migrationFoldersValid 'All 12 services own a migration history location'

$sqlFiles = @(Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'services') -Recurse -File -Filter '*.sql')
Add-Check 'No speculative migrations' ($sqlFiles.Count -eq 0) "$($sqlFiles.Count) SQL migration files"

$appCatalog = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'apps/catalog.json') -Raw | ConvertFrom-Json
Add-Check 'D05 application boundaries' (Test-ExactSet @($appCatalog.applications.folder) @('customer-web', 'administration-web')) 'Two reserved applications'

$contractFolders = @(Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'contracts') -Directory | Select-Object -ExpandProperty Name)
Add-Check 'D03 contract taxonomy homes' (Test-ExactSet $contractFolders @('queries', 'commands', 'events', 'integration', 'security-context')) ($contractFolders -join ', ')

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
$workflowValid = $workflowText -match 'Invoke-Bootstrap\.ps1 -Task All' -and
    $workflowText -match 'actions/checkout@[0-9a-f]{40}' -and
    $workflowText -match '(?m)^\s*contents:\s*read\s*$'
Add-Check 'Hosted bootstrap workflow' $workflowValid 'Commit-pinned checkout, read-only permissions, and complete bootstrap command'

$codeOwnersPath = Join-Path $RepositoryRoot '.github/CODEOWNERS'
$codeOwners = if (Test-Path -LiteralPath $codeOwnersPath) { (Get-Content -LiteralPath $codeOwnersPath -Raw).Trim() } else { '' }
Add-Check 'CODEOWNERS governance' ($codeOwners -ceq '* @Magkhan060') 'Repository-wide owner is explicit'

$ownershipPolicy = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/governance/ownership-policy.json') -Raw | ConvertFrom-Json
$protectionStateValid = $ownershipPolicy.protectedMain -ceq 'BLOCKED_CURRENT_GITHUB_PLAN' -and
    @($ownershipPolicy.codeOwners).Count -eq 1 -and $ownershipPolicy.codeOwners[0] -ceq '@Magkhan060' -and
    $ownershipPolicy.bootstrapException.commit -ceq '852bebbe62eccd54be214ba2168d19dfdda467d6'
Add-Check 'Branch-protection evidence' $protectionStateValid 'CODEOWNERS and bootstrap exception are governed; current-plan blocker remains explicit'

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
    $repositoryManifest.productTechnologyDecision -ceq 'UNRESOLVED' -and
    $repositoryManifest.status -ceq 'CANDIDATE_CLOSURE_BLOCKED' -and
    $repositoryManifest.hostedRepositoryDecision -ceq 'PARTIALLY_RESOLVED' -and
    $repositoryManifest.remote -ceq 'https://github.com/atlas-core-platform/monergy-application.git' -and
    $repositoryManifest.hostedCi -ceq 'GITHUB_ACTIONS' -and
    $repositoryManifest.branchProtection -ceq 'BLOCKED_CURRENT_GITHUB_PLAN' -and
    $repositoryManifest.businessFeatureImplementation -ceq 'NONE' -and
    $repositoryManifest.deploymentState -ceq 'NOT_DEPLOYED'
Add-Check 'Truthful repository state' $manifestValid 'Hosted CI resolved; branch protection, stack, Feature, and deployment are accurately bounded'

$observability = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'shared/platform/observability.contract.json') -Raw | ConvertFrom-Json
Add-Check 'Observability bootstrap' (@($observability.requiredEvidence).Count -eq 8 -and @($observability.prohibitedTelemetry).Count -eq 6) 'D05 telemetry and exclusion categories represented'

$releaseTemplate = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/release/product-release-manifest.template.json') -Raw | ConvertFrom-Json
Add-Check 'Release manifest bootstrap' ($releaseTemplate.status -ceq 'TEMPLATE_NOT_A_RELEASE' -and @($releaseTemplate.componentArtifacts).Count -eq 0) 'Template cannot be mistaken for a release'

$dependencyNames = @('package.json', 'package-lock.json', 'pnpm-lock.yaml', 'yarn.lock', 'requirements.txt', 'poetry.lock', 'pom.xml', 'build.gradle', 'Cargo.toml', 'go.mod')
$dependencyManifests = @(Get-ChildItem -LiteralPath $RepositoryRoot -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/]\.git[\\/]' -and $_.Name -in $dependencyNames })
Add-Check 'No unapproved product technology' ($dependencyManifests.Count -eq 0) "$($dependencyManifests.Count) product dependency manifests"

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
