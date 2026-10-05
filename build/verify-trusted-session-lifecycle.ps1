[CmdletBinding()]
param(
    [switch]$SelfTest,
    [string]$RepositoryRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Split-Path -Parent $PSScriptRoot
}

function Test-ExactSet([object[]]$Actual, [object[]]$Expected) {
    @($Actual).Count -eq @($Expected).Count -and
    @($Actual | Where-Object { $_ -notin $Expected }).Count -eq 0 -and
    @($Expected | Where-Object { $_ -notin $Actual }).Count -eq 0
}

function Test-ContainsAll([string]$Text, [string[]]$Values) {
    @($Values | Where-Object { -not $Text.Contains($_) }).Count -eq 0
}

function Test-Scope([object]$Value) {
    $Value.deliverable -ceq 'MWP-03-D14' -and
    $Value.status -ceq 'CANDIDATE_PENDING_CTO_REVIEW' -and
    $Value.evidenceLevel -ceq 'SIMULATOR_LOCAL_CI_EPHEMERAL' -and
    $Value.startingCommits.application -ceq '698f4b8f8ac180f2eeea1ead9dc0bb2a38b56346' -and
    $Value.startingCommits.architecture -ceq 'c9aaada1940008a8f7bf6841628cdb22c60c9bcc' -and
    $Value.startingCommits.systemExpert -ceq 'd5d58e9ea82badb6f8ef9335cf3ae2dd0caa2768' -and
    $Value.systemExpertVersion -ceq '0.3.2' -and
    $Value.systemExpertSourceContext.d13AcceptedStateCommit -ceq 'cb512b48d867b8d5cda703f623f9e7850563216c' -and
    $Value.systemExpertSourceContext.d14AuthoritativeMain -ceq 'd5d58e9ea82badb6f8ef9335cf3ae2dd0caa2768' -and
    $Value.systemExpertSourceContext.maintenanceCharacter -ceq 'V0_3_2_HYGIENE_ONLY_NO_ACCEPTED_MONERGY_SEMANTIC_CHANGE' -and
    @($Value.features).Count -eq 1 -and
    $Value.features[0].id -ceq 'M2-WS02-E01-F02' -and
    $Value.features[0].dependency -ceq 'M2-WS02-E01-F01' -and
    (Test-ExactSet @($Value.contractTreatment.preserved) @('CID-001')) -and
    (Test-ExactSet @($Value.contractTreatment.advanced) @('CID-002')) -and
    (Test-ExactSet @($Value.contractTreatment.newlyRealizedProducer) @('CID-005')) -and
    (Test-ExactSet @($Value.contractTreatment.notNewlyRealized) @('CID-006','CID-007')) -and
    (Test-ExactSet @($Value.changedDomainServiceBoundaries) @('Customer & Identity Service')) -and
    (Test-ExactSet @($Value.executionZones) @('LOCAL','CI_EPHEMERAL')) -and
    $Value.persistence -ceq 'OWNER_REFERENCE_IN_MEMORY_ONLY' -and
    $Value.referenceSessionLifetime -ceq 'INJECTED_SIMULATOR_POLICY_30_MINUTES_NOT_ARCHITECTURE_POLICY' -and
    $Value.revocationControl -ceq 'OWNER_INTERNAL_REFERENCE_CONTROL_NOT_PUBLIC_CONTRACT' -and
    $Value.eventTransport -ceq 'OWNER_LOCAL_REFERENCE_SINK_NO_DURABILITY_CLAIM' -and
    $Value.providerSelection -ceq 'NONE' -and
    $Value.frontendChange -ceq 'NONE' -and
    $Value.physicalMigration -ceq 'NONE' -and
    $Value.authorizationImplementation -ceq 'NONE' -and
    $Value.consentImplementation -ceq 'NONE' -and
    $Value.mfaImplementation -ceq 'NONE' -and
    $Value.d13.status -ceq 'ACCEPTED_COMPLETE' -and
    $Value.d13.behaviorPreserved -eq $true -and
    $Value.d13.sourceIdentityEvidenceChanged -eq $false -and
    $Value.hostedVerification.ordinaryBranchPushFullRun -eq $false -and
    $Value.stageGates.'SG-01' -ceq 'READY' -and
    $Value.stageGates.'SG-02' -ceq 'CONDITIONALLY_READY' -and
    $Value.stageGates.'SG-03' -ceq 'BLOCKED' -and
    $Value.stageGates.'SG-04' -ceq 'BLOCKED'
}

function Test-ImplementationSource(
    [string]$Application,
    [string]$Ports,
    [string]$Lifecycle,
    [string]$Adapters,
    [string]$Contracts) {
    (Test-ContainsAll $Application @(
        'trustedSessions.EstablishOrEvaluate',
        'TrustedSessionEvaluationStatus.Current',
        'TrustedSessionEvaluationStatus.DependencyFailure',
        'identity.session.not-current',
        'identity.session.unavailable')) -and
    (Test-ContainsAll $Ports @(
        'TrustedSessionPolicy',
        'TimeSpan.FromMinutes(30)',
        'ITrustedSessionRepository',
        'ICustomerIdentityEventSink',
        'TrustedSessionSnapshot')) -and
    (Test-ContainsAll $Lifecycle @(
        'owner-local reference control',
        'REFERENCE_SESSION_ESTABLISHED',
        'REFERENCE_SESSION_REVOKED',
        '"CID-005"',
        'D14ContractNames.CustomerIdentityChanged',
        'Customer & Identity Service',
        '"Customer"')) -and
    (Test-ContainsAll $Adapters @(
        'now >= current.ExpiresAt',
        'current.Revoked',
        'TrustedSessionRevocationStatus.AlreadyRevoked',
        'CreationEffectCount',
        'RevocationEffectCount',
        'session with { Actor = session.Actor with { } }',
        'ReferenceAdapterGuard.EnsureAllowed(configuration)')) -and
    $Contracts.Contains('CustomerIdentityChangedPayload') -and
    $Contracts.Contains('string ChangeKind') -and
    $Contracts.Contains('int Revision') -and
    $Contracts -notmatch '(?i)AuthenticationReference|AuthenticationContextId|Provider|Credential|Token|CustomerProjection|DisplayName|Kyc'
}

$scopePath = Join-Path $RepositoryRoot 'build/governance/d14-scope-lock.json'
$scope = Get-Content -LiteralPath $scopePath -Raw -Encoding utf8 | ConvertFrom-Json
$application = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/customer-identity/Application/CustomerIdentityApplication.cs') -Raw -Encoding utf8
$ports = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/customer-identity/Application/CustomerIdentityPorts.cs') -Raw -Encoding utf8
$lifecycle = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/customer-identity/Application/TrustedSessionLifecycle.cs') -Raw -Encoding utf8
$adapters = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/customer-identity/Infrastructure/ReferenceCustomerIdentityAdapters.cs') -Raw -Encoding utf8
$contracts = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'contracts/Monergy.Contracts/D14TrustedSessionContracts.cs') -Raw -Encoding utf8

if ($SelfTest) {
    $checks = [ordered]@{
        'valid D14 scope' = Test-Scope $scope
        'valid trusted-session implementation source' = Test-ImplementationSource $application $ports $lifecycle $adapters $contracts
    }
    foreach ($mutation in @(
        @{ name='second Feature rejected'; path='features'; value=@($scope.features + [pscustomobject]@{id='M2-WS02-E02-F01';dependency='M2-WS02-E01-F02'}) },
        @{ name='stale pre-maintenance System Expert lock rejected'; path='startingCommits.systemExpert'; value='cb512b48d867b8d5cda703f623f9e7850563216c' },
        @{ name='new contract ID rejected'; path='contractTreatment.newlyRealizedProducer'; value=@('CID-005','CID-099') },
        @{ name='CID-007 realization rejected'; path='contractTreatment.notNewlyRealized'; value=@('CID-006') },
        @{ name='Production evidence rejected'; path='evidenceLevel'; value='PRODUCTION' },
        @{ name='Production persistence rejected'; path='persistence'; value='POSTGRESQL' },
        @{ name='normative lifetime rejected'; path='referenceSessionLifetime'; value='PRODUCTION_POLICY_30_MINUTES' },
        @{ name='public revocation contract rejected'; path='revocationControl'; value='PUBLIC_SESSION_API' },
        @{ name='acceptance claim rejected'; path='status'; value='ACCEPTED_COMPLETE' },
        @{ name='stage gate advancement rejected'; path='stageGates.SG-02'; value='READY' }
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
    $checks['missing exact expiry rejection rejected'] = -not (Test-ImplementationSource $application $ports $lifecycle (
        $adapters.Replace('now >= current.ExpiresAt', 'now > current.ExpiresAt')) $contracts)
    $checks['missing revoked-state check rejected'] = -not (Test-ImplementationSource $application $ports $lifecycle (
        $adapters.Replace('current.Revoked', 'false')) $contracts)
    $checks['missing application session gate rejected'] = -not (Test-ImplementationSource (
        $application.Replace('trustedSessions.EstablishOrEvaluate', 'Bypass')) $ports $lifecycle $adapters $contracts)
    $checks['protected event payload rejected'] = -not (Test-ImplementationSource $application $ports $lifecycle $adapters (
        $contracts.Replace('int Revision', "int Revision,`n    string AuthenticationReference")))
    foreach ($entry in $checks.GetEnumerator()) {
        Write-Output "SELF-TEST $(if ($entry.Value) { 'PASS' } else { 'FAIL' }): $($entry.Key)"
    }
    if (@($checks.Values | Where-Object { -not $_ }).Count) { throw 'D14 negative self-tests failed.' }
    Write-Output "D14 self-tests passed: $($checks.Count)/$($checks.Count) (positive controls: 2; negative mutations: 14)."
    exit 0
}

$checks = [ordered]@{}
$checks['Exact one-Feature D14 scope and frozen stage gates'] = Test-Scope $scope

$catalog = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'contracts/Monergy.Contracts/D14ContractCatalog.cs') -Raw -Encoding utf8
$catalogIds = @([regex]::Matches($catalog, 'new\("(CID-\d{3})"') | ForEach-Object { $_.Groups[1].Value })
$checks['D14 catalog preserves CID-001 advances CID-002 and realizes only CID-005'] =
    (Test-ExactSet $catalogIds @('CID-001','CID-002','CID-005')) -and
    (Test-ContainsAll $catalog @('PRESERVED_D13','ADVANCED_CURRENT_SESSION_TRUST','NEWLY_REALIZED_PRODUCER')) -and
    $catalog -notmatch 'CID-006|CID-007'

$checks['Current-session lifecycle is clock driven fail closed and irreversible'] =
    Test-ImplementationSource $application $ports $lifecycle $adapters $contracts

$endpoints = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/customer-identity/CustomerIdentityEndpoints.cs') -Raw -Encoding utf8
$routeIds = @([regex]::Matches($endpoints, '/contracts/(cid-\d{3})/v1') | ForEach-Object { $_.Groups[1].Value.ToUpperInvariant() })
$checks['No session API or new HTTP contract route is introduced'] =
    (Test-ExactSet $routeIds @('CID-001','CID-002','CID-003','CID-004')) -and
    $endpoints -notmatch '(?i)/session|cid-005|revoke'

$checks['CID-005 payload is minimal provider-neutral and Audit-envelope compatible'] =
    $contracts -match 'CustomerIdentityChangedPayload\([\s\S]*string ChangeKind,[\s\S]*int Revision\)' -and
    $contracts -notmatch '(?i)AuthenticationReference|AuthenticationContextId|Provider|Credential|Token|DisplayName|Kyc' -and
    $lifecycle.Contains('DomainEvent<CustomerIdentityChangedPayload>') -and
    $lifecycle.Contains('"Customer"') -and
    $lifecycle.Contains('session.CustomerId')

$checks['Reference adapters remain LOCAL or CI_EPHEMERAL and introduce no physical store'] =
    @([regex]::Matches($adapters, 'ReferenceAdapterGuard.EnsureAllowed\(configuration\)')).Count -ge 5 -and
    @((Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'services/customer-identity') -Recurse -File -Filter '*.sql')).Count -eq 0 -and
    ($application + $ports + $lifecycle + $adapters) -notmatch '(?i)EntityFrameworkCore|SqlConnection|Npgsql|MongoClient|Redis|Kafka|RabbitMQ|ServiceBus|EventBridge'

$matrix = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/governance/d14-scenario-matrix.json') -Raw -Encoding utf8 | ConvertFrom-Json
$scenarioIds = 1..11 | ForEach-Object { 'D14-T{0:D2}' -f $_ }
$checks['All D14-T01 through D14-T11 scenario groups name executable evidence'] =
    $matrix.status -ceq 'CANDIDATE_EVIDENCE_IMPLEMENTED' -and
    (Test-ExactSet @($matrix.scenarios.id) $scenarioIds) -and
    @($matrix.scenarios | Where-Object {
        $testCount = if ($null -eq $_.PSObject.Properties['tests']) { 0 } else { @($_.tests).Count }
        $commandCount = if ($null -eq $_.PSObject.Properties['commands']) { 0 } else { @($_.commands).Count }
        @($_.evidenceTypes).Count -eq 0 -or ($testCount -eq 0 -and $commandCount -eq 0)
    }).Count -eq 0

$testRoot = Join-Path $RepositoryRoot 'tests/customer-identity/Monergy.CustomerIdentity.Tests'
$testText = @(Get-ChildItem -LiteralPath $testRoot -Recurse -File -Filter '*.cs' |
    ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw -Encoding utf8 }) -join "`n"
$namedTests = @($matrix.scenarios | ForEach-Object {
    if ($null -ne $_.PSObject.Properties['tests']) { $_.tests }
} | Where-Object { $_ } | Select-Object -Unique)
$checks['Every D14 scenario-matrix test is present in focused source'] =
    @($namedTests | Where-Object { -not $testText.Contains([string]$_) }).Count -eq 0

$manifest = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'repository.manifest.json') -Raw -Encoding utf8 | ConvertFrom-Json
$checks['Repository manifest records bounded D14 candidate without acceptance claim'] =
    $manifest.d14Status -ceq 'CANDIDATE_PENDING_CTO_REVIEW' -and
    $manifest.d14FeatureState -ceq 'IMPLEMENTATION_CANDIDATE_SIMULATOR_1_OF_1' -and
    $manifest.d14ContractState -ceq 'CID_001_PRESERVED_CID_002_ADVANCED_CID_005_PRODUCER_REALIZED_CANDIDATE' -and
    $manifest.d14SessionPersistence -ceq 'OWNER_REFERENCE_IN_MEMORY_ONLY' -and
    $manifest.d14EventTransport -ceq 'OWNER_LOCAL_REFERENCE_SINK_NO_DURABILITY_CLAIM' -and
    $manifest.deploymentState -ceq 'NOT_DEPLOYED'

$sourceIdentity = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/d14/Get-D14SourceIdentity.ps1') -Raw -Encoding utf8
$checks['Canonical D14 source identity uses exact baseline ordinal order and unambiguous framing'] =
    $sourceIdentity.Contains('698f4b8f8ac180f2eeea1ead9dc0bb2a38b56346') -and
    $sourceIdentity.Contains('[Array]::Sort($paths, [StringComparer]::Ordinal)') -and
    $sourceIdentity.Contains('big-endian Int32 path-byte-length + file-byte-length') -and
    $sourceIdentity.Contains('Select-Object -Unique')

$d13EvidenceChanges = @(git -C $RepositoryRoot diff --name-only '698f4b8f8ac180f2eeea1ead9dc0bb2a38b56346' -- 'build/d13')
$workflowChanges = @(git -C $RepositoryRoot diff --name-only '698f4b8f8ac180f2eeea1ead9dc0bb2a38b56346' -- '.github/workflows')
$checks['D13 source identity and hosted workflow policy are unchanged'] =
    $d13EvidenceChanges.Count -eq 0 -and $workflowChanges.Count -eq 0

$changed = @(git -C $RepositoryRoot diff --name-only '698f4b8f8ac180f2eeea1ead9dc0bb2a38b56346' --)
$untracked = @(git -C $RepositoryRoot ls-files --others --exclude-standard)
$inventory = @($changed + $untracked | Select-Object -Unique)
$allowed = '^(contracts/Monergy\.Contracts/D14|services/customer-identity/|tests/customer-identity/|build/d14/|build/governance/d14-|build/verify-trusted-session-lifecycle\.ps1$|repository\.manifest\.json$)'
$checks['Changed and untracked inventory is bounded to D14 implementation areas'] =
    @($inventory | Where-Object { ([string]$_).Replace('\','/') -notmatch $allowed }).Count -eq 0

$dockerfile = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/customer-identity/Dockerfile') -Raw -Encoding utf8
$checks['Customer Identity OCI digest pins and dependency versions are unchanged'] =
    $dockerfile.Contains($scope.customerIdentityOciBaseImages.sdk) -and
    $dockerfile.Contains($scope.customerIdentityOciBaseImages.runtime) -and
    @($inventory | Where-Object { $_ -match '(^|/)(Directory\.Packages\.props|global\.json|package\.json|pnpm-lock\.yaml|packages\.lock\.json)$' }).Count -eq 0

foreach ($entry in $checks.GetEnumerator()) {
    Write-Output "[$(if ($entry.Value) { 'PASS' } else { 'FAIL' })] $($entry.Key)"
}
$failed = @($checks.Values | Where-Object { -not $_ })
if ($failed.Count) { throw "D14 verification failed: $($checks.Count - $failed.Count)/$($checks.Count)." }
Write-Output "D14 verification passed: $($checks.Count)/$($checks.Count); candidate only."
