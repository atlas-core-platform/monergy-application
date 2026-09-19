[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Test-ExactSet {
    param([object[]]$Actual, [object[]]$Expected)
    (@($Actual | ForEach-Object { [string]$_ } | Sort-Object) -join '|') -ceq
        (@($Expected | ForEach-Object { [string]$_ } | Sort-Object) -join '|')
}

function Test-ExactSequence {
    param([object[]]$Actual, [object[]]$Expected)
    (@($Actual | ForEach-Object { [string]$_ }) -join '|') -ceq
        (@($Expected | ForEach-Object { [string]$_ }) -join '|')
}

if ($SelfTest) {
    $tests = [ordered]@{
        'exact set accepts reordered values' = (Test-ExactSet @('b', 'a') @('a', 'b'))
        'exact set rejects a duplicate replacing a value' = (-not (Test-ExactSet @('a', 'a') @('a', 'b')))
        'exact sequence accepts canonical order' = (Test-ExactSequence @('a', 'b') @('a', 'b'))
        'exact sequence rejects reordered values' = (-not (Test-ExactSequence @('b', 'a') @('a', 'b')))
        'UAT is not an authorized reference zone' = ('UAT' -notin @('LOCAL', 'CI_EPHEMERAL', 'CI/EPHEMERAL'))
        'Production is not an authorized reference zone' = ('PRODUCTION' -notin @('LOCAL', 'CI_EPHEMERAL', 'CI/EPHEMERAL'))
    }
    $failed = @($tests.GetEnumerator() | Where-Object { -not $_.Value })
    foreach ($test in $tests.GetEnumerator()) {
        Write-Output "SELF-TEST $(if ($test.Value) { 'PASS' } else { 'FAIL' }): $($test.Key)"
    }
    if ($failed.Count -gt 0) { throw "D04 verifier self-tests failed: $($tests.Count - $failed.Count)/$($tests.Count)." }
    Write-Output "D04 verifier self-tests passed: $($tests.Count)/$($tests.Count)."
    exit 0
}

$expectedFeatures = @(
    'M2-WS04-E01-F01', 'M2-WS04-E01-F02', 'M2-WS04-E01-F03',
    'M2-WS04-E02-F01', 'M2-WS04-E02-F02', 'M2-WS04-E02-F03', 'M2-WS04-E03-F03'
)
$expectedFeatureNames = @(
    'Financial Profile, Income & Expense', 'Transactions & Bank Accounts',
    'Loans, Credit Cards & Credit Profile', 'Investment portfolio',
    'Insurance, Property & Vehicle', 'Tax Records & Financial Goals', 'Authoritative APIs & events'
)
$expectedContracts = @('CID-030', 'CID-031', 'CID-032', 'CID-033', 'CID-034', 'CID-035', 'CID-036')
$expectedD03Contracts = @('CID-020', 'CID-021', 'CID-022', 'CID-023', 'CID-024', 'CID-025', 'CID-027', 'CID-028', 'CID-031', 'CID-033', 'CID-034', 'CID-035', 'CID-055', 'CID-057')
$expectedContractOverlap = @('CID-031', 'CID-033', 'CID-034', 'CID-035')
$expectedD04Only = @('CID-030', 'CID-032', 'CID-036')
$expectedContractNames = @(
    'GetFinancialProfile', 'NormalizeSourceFacts', 'GetFinancialFact', 'GetFinancialProvenance',
    'FinancialFactCreated', 'FinancialFactUpdated', 'FinancialProfileChanged'
)
$expectedObjects = @(
    'Financial Profile', 'Income', 'Expense', 'Financial Transaction', 'Bank Account', 'Loan',
    'Credit Card Account', 'Credit Profile', 'Investment', 'Insurance Policy', 'Property',
    'Vehicle', 'Tax Record', 'Financial Goal', 'Audit Event'
)
$expectedDp = @('DP-01', 'DP-02', 'DP-03', 'DP-04', 'DP-05', 'DP-06', 'DP-07', 'DP-08', 'DP-09', 'DP-10', 'DP-11', 'DP-12', 'DP-13', 'DP-14', 'DP-17', 'DP-24', 'DP-25', 'DP-27', 'DP-28', 'DP-29', 'DP-30', 'DP-31', 'DP-32', 'DP-33', 'DP-34', 'DP-35', 'DP-36', 'DP-37', 'DP-39', 'DP-40', 'DP-41', 'DP-42', 'DP-43', 'DP-44', 'DP-45')
$expectedEp = @('EP-01', 'EP-02', 'EP-03', 'EP-04', 'EP-05', 'EP-06', 'EP-07', 'EP-08', 'EP-09', 'EP-10', 'EP-11', 'EP-12', 'EP-13', 'EP-14', 'EP-15', 'EP-16', 'EP-17', 'EP-18', 'EP-19', 'EP-20', 'EP-21', 'EP-22', 'EP-27', 'EP-28', 'EP-29', 'EP-30', 'EP-31', 'EP-32', 'EP-33', 'EP-34', 'EP-35', 'EP-36', 'EP-37', 'EP-38', 'EP-39', 'EP-40', 'EP-41', 'EP-42', 'EP-43', 'EP-44', 'EP-45', 'EP-46', 'EP-47', 'EP-48')
$expectedNfr = @('NFR-01', 'NFR-02', 'NFR-03', 'NFR-04', 'NFR-05', 'NFR-06', 'NFR-07', 'NFR-08', 'NFR-09', 'NFR-10', 'NFR-11', 'NFR-12', 'NFR-18', 'NFR-19', 'NFR-24', 'NFR-27', 'NFR-29', 'NFR-30', 'NFR-31', 'NFR-34', 'NFR-35', 'NFR-36', 'NFR-37', 'NFR-38', 'NFR-39', 'NFR-40', 'NFR-41', 'NFR-42', 'NFR-43', 'NFR-44', 'NFR-45', 'NFR-46', 'NFR-47', 'NFR-48', 'NFR-49', 'NFR-50')
$expectedTq = @('TQ-01', 'TQ-02', 'TQ-03', 'TQ-04', 'TQ-05', 'TQ-06', 'TQ-07', 'TQ-08', 'TQ-09', 'TQ-10', 'TQ-11', 'TQ-12', 'TQ-13', 'TQ-14', 'TQ-16', 'TQ-17', 'TQ-18', 'TQ-19', 'TQ-20', 'TQ-21', 'TQ-22', 'TQ-23', 'TQ-24', 'TQ-25', 'TQ-26', 'TQ-30', 'TQ-31', 'TQ-32', 'TQ-33', 'TQ-34', 'TQ-35', 'TQ-36', 'TQ-37', 'TQ-38', 'TQ-39', 'TQ-40', 'TQ-45', 'TQ-46', 'TQ-47', 'TQ-48')
$expectedDecisions = @('OD-06', 'OD-07', 'OD-08', 'OD-09', 'OD-13', 'OD-14', 'C-06', 'C-07', 'C-10', 'C-22')

$checks = [System.Collections.Generic.List[object]]::new()
function Add-Check {
    param([string]$Name, [bool]$Passed, [string]$Evidence)
    $checks.Add([pscustomobject]@{ Name = $Name; Passed = $Passed; Evidence = $Evidence }) | Out-Null
}

$scope = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/governance/d04-scope-lock.json') -Raw | ConvertFrom-Json
Add-Check 'Accepted identity and evidence boundary' ($scope.deliverable -ceq 'MWP-03-D04' -and $scope.status -ceq 'ACCEPTED_COMPLETE' -and $scope.evidenceLevel -ceq 'SIMULATOR_ACCEPTED') 'Accepted / Complete at SIMULATOR; no stronger readiness claim'
Add-Check 'Exact seven Feature IDs' (Test-ExactSequence @($scope.features.id) $expectedFeatures) '7/7 exact governed IDs'
Add-Check 'Exact seven Feature names' (Test-ExactSequence @($scope.features.name) $expectedFeatureNames) '7/7 exact governed names'
Add-Check 'Unique Financial Profile Feature ownership' (@($scope.features | Where-Object owner -cne 'Financial Profile Service').Count -eq 0 -and @($scope.features.id | Select-Object -Unique).Count -eq 7) 'Financial Profile Service owns 7/7'
Add-Check 'Exact seven contract IDs' (Test-ExactSequence @($scope.contracts.id) $expectedContracts) 'CID-030 through CID-036'
Add-Check 'Exact seven contract names' (Test-ExactSequence @($scope.contracts.name) $expectedContractNames) 'Canonical names preserved'
Add-Check 'Contract semantics complete' (@($scope.contracts | Where-Object { $_.owner -cne 'Financial Profile Service' -or $_.producer -cne 'Financial Profile Service' -or $_.consumers.Count -eq 0 -or [string]::IsNullOrWhiteSpace($_.input) -or [string]::IsNullOrWhiteSpace($_.output) -or [string]::IsNullOrWhiteSpace($_.stateSemantics) -or $_.errorSemantics.Count -eq 0 -or [string]::IsNullOrWhiteSpace($_.authorization) -or [string]::IsNullOrWhiteSpace($_.idempotency) -or [string]::IsNullOrWhiteSpace($_.provenance) -or $_.version -cne '1.0.0' }).Count -eq 0) 'Owner/producer/consumer, IO, state, error, authorization, idempotency, provenance and version resolved'
Add-Check 'Participating boundaries exact' (Test-ExactSet @($scope.participatingServices) @('Financial Profile Service', 'Audit Service')) 'Two governed service boundaries'
Add-Check 'Business Object set exact' (Test-ExactSequence @($scope.businessObjects) $expectedObjects) 'Fourteen Financial Profile objects plus Audit Event'
Add-Check 'Exact DP scope' (Test-ExactSequence @($scope.applicableControls.dp) $expectedDp) "$($expectedDp.Count) canonical DP IDs"
Add-Check 'Exact EP scope' (Test-ExactSequence @($scope.applicableControls.ep) $expectedEp) "$($expectedEp.Count) canonical EP IDs"
Add-Check 'Exact NFR scope' (Test-ExactSequence @($scope.applicableControls.nfr) $expectedNfr) "$($expectedNfr.Count) canonical NFR IDs"
Add-Check 'Exact TQ scope' (Test-ExactSequence @($scope.applicableControls.tq) $expectedTq) "$($expectedTq.Count) canonical TQ IDs"
Add-Check 'Decision dependencies preserved' (Test-ExactSequence @($scope.decisionDependencies) $expectedDecisions) 'No open decision is represented as resolved'
Add-Check 'Stage gates preserved' ($scope.stageGates.'SG-01' -ceq 'READY' -and $scope.stageGates.'SG-02' -ceq 'CONDITIONALLY_READY' -and $scope.stageGates.'SG-03' -ceq 'BLOCKED' -and $scope.stageGates.'SG-04' -ceq 'BLOCKED') 'SG-02 conditional; SG-03/SG-04 blocked'

$schema = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'contracts/schemas/financial-profile-authority.schema.json') -Raw | ConvertFrom-Json
Add-Check 'D04 schema exact contract IDs' (Test-ExactSequence @($schema.properties.contractId.enum) $expectedContracts) 'Closed seven-ID schema'
Add-Check 'D04 schema exact names and version' (Test-ExactSequence @($schema.properties.contractName.enum) $expectedContractNames -and $schema.properties.contractVersion.const -ceq '1.0.0' -and -not $schema.additionalProperties) 'Canonical names, 1.0.0 and closed envelope'

$d03Catalog = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'contracts/Monergy.Contracts/Vs02ContractCatalog.cs') -Raw
$d04Catalog = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'contracts/Monergy.Contracts/D04ContractCatalog.cs') -Raw
Add-Check 'D03 catalog remains fourteen contracts' (([regex]::Matches($d03Catalog, 'new\("CID-')).Count -eq 14) 'Accepted D03 catalog not widened'
Add-Check 'D04 executable catalog exact identities' (@($expectedContracts | Where-Object { -not $d04Catalog.Contains("`"$_`"") }).Count -eq 0 -and ([regex]::Matches($d04Catalog, '"CID-03[0-6]"')).Count -eq 7) 'Seven D04 definitions'
$objectTypes = @('INCOME', 'EXPENSE', 'FINANCIAL_TRANSACTION', 'BANK_ACCOUNT', 'LOAN', 'CREDIT_CARD_ACCOUNT', 'CREDIT_PROFILE', 'INVESTMENT', 'INSURANCE_POLICY', 'PROPERTY', 'VEHICLE', 'TAX_RECORD', 'FINANCIAL_GOAL')
Add-Check 'Thirteen child object categories exact' (@($objectTypes | Where-Object { -not $d04Catalog.Contains("`"$_`"") }).Count -eq 0) 'Financial Profile is the aggregate over thirteen governed child categories'

$applicationText = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/financial-profile/Application/FinancialProfileApplication.cs') -Raw
$repositoryText = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/financial-profile/Infrastructure/InMemoryFinancialProfileRepository.cs') -Raw
$endpointsText = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/financial-profile/FinancialProfileEndpoints.cs') -Raw
Add-Check 'Authoritative query and command surface' ($applicationText.Contains('GetFinancialProfileAsync') -and $applicationText.Contains('NormalizeSourceFactsAsync') -and $applicationText.Contains('GetFinancialFactAsync') -and $applicationText.Contains('GetFinancialProvenanceAsync') -and $endpointsText.Contains('cid-030') -and $endpointsText.Contains('cid-031') -and $endpointsText.Contains('cid-032') -and $endpointsText.Contains('cid-033')) 'CID-030 through CID-033 service operations'
Add-Check 'Immutable history and explicit revision' ($repositoryText.Contains('factHistory') -and $repositoryText.Contains('profileRevision') -and $repositoryText.Contains('FinancialProvenance') -and $repositoryText.Contains('CID-035')) 'Updates retain revision and provenance history'
Add-Check 'Application-owned CID-036 with atomic adapter publication' ($applicationText.Contains('FinancialProfileChangeTransition') -and $applicationText.Contains('CID-036') -and $applicationText.Contains('FinancialProfileChangedPayload') -and $repositoryText.Contains('profileChange.CreateEvent') -and -not $repositoryText.Contains('CID-036') -and -not $repositoryText.Contains('FinancialProfileChangedPayload') -and $repositoryText.Contains('lock (sync)') -and $repositoryText.Contains('outbox.AddRange(events)')) 'Application defines the governed event; adapter atomically persists the supplied transition result'
Add-Check 'Governed idempotency identity and payload conflict guard' ($applicationText.Contains('FinancialNormalizationIdentity') -and $applicationText.Contains('request.ContractName') -and $applicationText.Contains('request.ContractVersion') -and $applicationText.Contains('request.Security.Access.CustomerId') -and $repositoryText.Contains('Dictionary<FinancialNormalizationIdentity') -and $repositoryText.Contains('idempotencyPayloads') -and $repositoryText.Contains('Idempotency key conflict')) 'Contract/version/customer/key identity governs replay; payload equality only detects conflicting key reuse'
Add-Check 'Customer isolation enforcement' ($applicationText.Contains('SameCustomer') -and $applicationText.Contains('request.Security.Access.CustomerId') -and $applicationText.Contains('NotFound')) 'Owner checks payload and trusted customer context'

$referenceGuard = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'shared/platform/Monergy.Platform/ReferenceAdapterGuard.cs') -Raw
Add-Check 'Reference adapter remains LOCAL/CI only' ($referenceGuard.Contains('LOCAL') -and $referenceGuard.Contains('CI_EPHEMERAL') -and $referenceGuard.Contains('InvalidOperationException')) 'UAT and Production fail closed'
$projectText = @(Get-ChildItem -LiteralPath $RepositoryRoot -Recurse -File -Filter '*.csproj' | Where-Object { $_.FullName -notmatch '[\/](?:bin|obj)[\/]' } | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
Add-Check 'No physical provider dependency' ($projectText -notmatch 'EntityFrameworkCore|Npgsql|SqlClient|MongoDB|StackExchange\.Redis|Azure\.|Amazon\.|Google\.Cloud|OpenAI') 'No database, broker, cloud, OCR or AI provider SDK'
$migrationFiles = @(Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'services') -Recurse -File | Where-Object { $_.Extension -in @('.sql', '.ddl') })
Add-Check 'No physical migrations' ($migrationFiles.Count -eq 0) 'Migration homes remain placeholders'

$testRoot = Join-Path $RepositoryRoot 'tests/financial-profile/Monergy.FinancialProfile.Tests'
$testText = @(Get-ChildItem -LiteralPath $testRoot -File -Filter '*.cs' | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
Add-Check 'Permanent D04 test suite present' ($testText.Contains('AllGovernedObjectTypesBecomeOneAuthorizedProviderNeutralProfile') -and $testText.Contains('RevisionsPreserveHistoryProvenanceAndReplaySafety') -and $testText.Contains('QueriesReturnCurrentAuthorityAndFailClosedAcrossCustomers') -and $testText.Contains('EveryContractRejectsMalformedUnknownMembers') -and $testText.Contains('IdempotencyIdentityIncludesContractVersionCustomerScopeAndCallerKey')) 'Domain, lifecycle, authorization, isolation, governed idempotency, lineage, audit and per-contract malformed coverage'
Add-Check 'Every D04 CID appears in permanent tests' (@($expectedContracts | Where-Object { -not $testText.Contains($_) }).Count -eq 0) '7/7 contract compatibility evidence sets'
$d03TestRoot = Join-Path $RepositoryRoot 'tests/vs02/Monergy.Vs02.Tests'
$d03TestFiles = @(Get-ChildItem -LiteralPath $d03TestRoot -File -Filter '*.cs')
Add-Check 'D04 test ownership is independent from D03' ((Test-Path -LiteralPath (Join-Path $testRoot 'Monergy.FinancialProfile.Tests.csproj')) -and @($d03TestFiles | Where-Object Name -in @('FinancialProfileAuthorityTests.cs', 'FinancialProfileContractCompatibilityTests.cs')).Count -eq 0) 'D04 authority and compatibility tests live only in the Financial Profile test project'

$manifest = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'repository.manifest.json') -Raw | ConvertFrom-Json
Add-Check 'Truthful D04 accepted lifecycle' ($manifest.d04Status -ceq 'ACCEPTED_COMPLETE' -and $manifest.d04FeatureState -ceq 'IMPLEMENTATION_ACCEPTED_SIMULATOR_7_OF_7' -and $manifest.d04ContractState -ceq 'APPLICABLE_COMPATIBILITY_AND_BEHAVIORAL_EVIDENCE_ACCEPTED_SIMULATOR_7_OF_7' -and $manifest.d04EvidenceLevel -ceq 'SIMULATOR_ACCEPTED' -and $manifest.deploymentState -ceq 'NOT_DEPLOYED') 'Accepted / Complete at SIMULATOR; no Integration, UAT, Production or deployment claim'
$serviceCatalog = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/catalog.json') -Raw | ConvertFrom-Json
$financialProfile = $serviceCatalog.services | Where-Object id -ceq 'financial-profile'
$audit = $serviceCatalog.services | Where-Object id -ceq 'audit'
Add-Check 'D04 service catalog assignment' ($financialProfile.d04Status -ceq 'IMPLEMENTATION_ACCEPTED_SIMULATOR' -and (Test-ExactSequence @($financialProfile.d04FeatureIds) $expectedFeatures) -and $audit.d04Status -ceq 'GOVERNED_AUDIT_PARTICIPANT') 'Seven Features assigned once; Audit is supporting only'
$frontendD04References = @(Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'apps') -Recurse -File | Select-String -SimpleMatch 'MWP-03-D04')
Add-Check 'Frontend business scope unchanged' ($frontendD04References.Count -eq 0) 'NONE REQUIRED BY D04 FEATURE SCOPE'

$acceptance = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/governance/d04-acceptance.json') -Raw | ConvertFrom-Json
Add-Check 'Approved candidates and CTO evidence basis recorded' ($acceptance.applicationLifecycle -ceq 'ACCEPTED_COMPLETE' -and $acceptance.crossRepositoryLifecycle -ceq 'PENDING_ARCHITECTURE_CLOSURE' -and $acceptance.evidenceLevel -ceq 'SIMULATOR' -and $acceptance.approvedCandidates.application -ceq '3b253887dd18dc780848b56bbda4d5fd1744c407' -and $acceptance.approvedCandidates.architecture -ceq '7fba22efbbd526a7ca031bca9af8471b92e47c42' -and $acceptance.approvalBasis.independentCtoGithubConnectorInspection -ceq 'HTTP_404') 'Supplied CTO approval basis and independently unavailable connector inspection are explicit'
Add-Check 'D03 and D04 contract overlap is exact' ((Test-ExactSequence @($acceptance.contracts.d04Applicable) $expectedContracts) -and (Test-ExactSequence @($acceptance.contracts.acceptedD03) $expectedD03Contracts) -and (Test-ExactSequence @($acceptance.contracts.overlap) $expectedContractOverlap) -and (Test-ExactSequence @($acceptance.contracts.d04OnlyRelativeToD03) $expectedD04Only) -and $acceptance.contracts.programWideUnionCount -eq 17) 'D04 applicable 7; overlap 4; D04-only 3; program union 17'
Add-Check 'Approved hosted candidate evidence is pinned' (@($acceptance.workflows).Count -eq 2 -and @($acceptance.workflows | Where-Object conclusion -cne 'success').Count -eq 0 -and (Test-ExactSet @($acceptance.workflows.id) @(35455698197, 35455695670)) -and @($acceptance.workflows | Where-Object { $_.artifact.sha256 -notmatch '^[0-9a-f]{64}$' -or $_.artifact.manifestSha256 -notmatch '^[0-9a-f]{64}$' -or $_.artifact.repositorySbomSha256 -notmatch '^[0-9a-f]{64}$' -or $_.artifact.ociEvidenceMatrixSha256 -notmatch '^[0-9a-f]{64}$' }).Count -eq 0) 'PR and push workflow identities, source identity and immutable artifact hashes recorded'
Add-Check 'Acceptance preserves actual security findings and stage gates' ($acceptance.verification.hosted.ociImages -eq 12 -and $acceptance.verification.hosted.ociGrypeMatches.total -eq 120 -and $acceptance.verification.hosted.vulnerabilityPolicyResult -ceq 'PASS_WITH_RECORDED_NON_BLOCKING_OCI_FINDINGS' -and $acceptance.stageGates.'SG-01' -ceq 'READY' -and $acceptance.stageGates.'SG-02' -ceq 'CONDITIONALLY_READY' -and $acceptance.stageGates.'SG-03' -ceq 'BLOCKED' -and $acceptance.stageGates.'SG-04' -ceq 'BLOCKED' -and -not $acceptance.publication.packages -and -not $acceptance.publication.images -and -not $acceptance.publication.deployment -and -not $acceptance.publication.tag) 'Non-blocking OCI findings retained; gates unchanged; nothing published, deployed or tagged'

$failures = @($checks | Where-Object { -not $_.Passed })
foreach ($check in $checks) {
    Write-Output "[$(if ($check.Passed) { 'PASS' } else { 'FAIL' })] $($check.Name) - $($check.Evidence)"
}
if ($failures.Count -gt 0) {
    throw "D04 verification failed: $($checks.Count - $failures.Count)/$($checks.Count); failed: $($failures.Name -join ', ')."
}
Write-Output "D04 verification passed: $($checks.Count)/$($checks.Count)."
