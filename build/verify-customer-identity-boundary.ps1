[CmdletBinding()]
param([string]$RepositoryRoot, [switch]$SelfTest)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = Split-Path -Parent $PSScriptRoot }

function Get-OrdinalSorted([object[]]$Values) {
    [string[]]$strings = @($Values | ForEach-Object { [string]$_ })
    [Array]::Sort($strings, [StringComparer]::Ordinal)
    return $strings
}

function Test-ExactSet([object[]]$Actual, [object[]]$Expected) {
    $actualSorted = @(Get-OrdinalSorted $Actual)
    $expectedSorted = @(Get-OrdinalSorted $Expected)
    $actualSorted.Count -eq $expectedSorted.Count -and
        ($actualSorted -join '|') -ceq ($expectedSorted -join '|')
}

function Test-Scope($Value) {
    $Value.deliverable -ceq 'MWP-03-D13' -and
    $Value.status -ceq 'CANDIDATE_PENDING_CTO_REVIEW' -and
    $Value.evidenceLevel -ceq 'SIMULATOR_LOCAL_CI_EPHEMERAL' -and
    $Value.startingCommits.application -ceq '930eb47eb172b6551b0b3d70d49b33a07337fb98' -and
    $Value.startingCommits.architecture -ceq 'b1edc68f29987b9aa9dc3125c3e77140de975f8f' -and
    @($Value.features).Count -eq 1 -and
    $Value.features[0].id -ceq 'M2-WS02-E01-F01' -and
    $Value.features[0].owner -ceq 'Customer & Identity Service' -and
    $Value.features[0].readiness -ceq 'READY' -and
    (Test-ExactSet @($Value.newlyRealizedContracts) @('CID-001','CID-002','CID-003','CID-004')) -and
    (Test-ExactSet @($Value.excludedContracts) @('CID-005','CID-006','CID-007')) -and
    (Test-ExactSet @($Value.changedDomainServiceBoundaries) @('Customer & Identity Service')) -and
    (Test-ExactSet @($Value.executionZones) @('LOCAL','CI_EPHEMERAL')) -and
    $Value.persistence -ceq 'OWNER_REFERENCE_IN_MEMORY_ONLY' -and
    $Value.providerSelection -ceq 'NONE' -and
    $Value.frontendChange -ceq 'NONE' -and
    $Value.physicalMigration -ceq 'NONE' -and
    $Value.eventRealization -ceq 'NONE' -and
    $Value.d11.status -ceq 'ACCEPTED_COMPLETE' -and $Value.d11.sourceChange -ceq 'NONE' -and
    $Value.d12.status -ceq 'ACCEPTED_COMPLETE' -and $Value.d12.sourceChange -ceq 'NONE' -and
    $Value.hostedVerification.ordinaryBranchPushFullRun -eq $false -and
    $Value.hostedVerification.preMergeAuthority -ceq 'PULL_REQUEST' -and
    $Value.hostedVerification.postMergeAuthority -ceq 'MAIN' -and
    $Value.stageGates.'SG-01' -ceq 'READY' -and
    $Value.stageGates.'SG-02' -ceq 'CONDITIONALLY_READY' -and
    $Value.stageGates.'SG-03' -ceq 'BLOCKED' -and
    $Value.stageGates.'SG-04' -ceq 'BLOCKED'
}

function Test-ForbiddenImplementation([string]$Text) {
    $Text -notmatch '(?i)\b(Auth0|Cognito|Entra|Okta)\b|EntityFrameworkCore|SqlConnection|Npgsql|MongoClient|Kafka|RabbitMQ|ServiceBus|EventBridge'
}

function Test-ContainsAll([string]$Text, [string[]]$Values) {
    @($Values | Where-Object { -not $Text.Contains($_) }).Count -eq 0
}

function Test-RemediationSource(
    [string]$Application,
    [string]$Ports,
    [string]$Repository,
    [string]$Tests) {
    $Ports.Contains('CustomerIdMaximumLength = 128') -and
    $Ports.Contains('RequestIdMaximumLength = 256') -and
    $Ports.Contains('CorrelationIdMaximumLength = 256') -and
    $Ports.Contains('!value.Any(char.IsControl)') -and
    $Ports.Contains('IsSafeTelemetryEnvelope') -and
    $Ports.Contains('AcceptedTelemetryOperations.Contains(signal.Operation)') -and
    $Ports.Contains('AcceptedTelemetryOutcomes.Contains(signal.Outcome)') -and
    $Ports.Contains('Enum.GetNames<ContractOutcome>()') -and
    (Test-ContainsAll $Ports @(
        'D13ContractNames.GetCustomer',
        'D13ContractNames.GetTrustedActorContext',
        'D13ContractNames.RegisterOrUpdateCustomer',
        'D13ContractNames.RecordKycResult')) -and
    $Ports.Contains('IsValidCanonicalResolution') -and
    (Test-ContainsAll $Ports @('"CUSTOMER"','"ADVISOR"','"ADMINISTRATOR"','"PROFESSIONAL"')) -and
    $Application.Contains('contract.telemetry-context.invalid') -and
    $Application.Contains('identity.authentication.malformed') -and
    $Application.Contains('CustomerIdentityBoundaryValidation.IsValidCanonicalResolution(resolved)') -and
    $Application.Contains('Complete(request, customerId, result)') -and
    $Application.Contains('CustomerIdentityBoundaryValidation.IsSafeTelemetrySignal(signal)') -and
    $Repository.Contains('The reference authentication record is malformed.') -and
    $Repository.Contains('CustomerIdentityBoundaryValidation.IsSafeTelemetrySignal(signal)') -and
    $Tests.Contains('ApplicationRejectsMalformedCanonicalProviderResolutionsWithoutNativeLeakage') -and
    $Tests.Contains('UnsafeTelemetryIdentifiersAreRejectedWithoutTelemetryContamination') -and
    $Tests.Contains('TelemetrySinkDefensivelyDropsUnsafeIdentifiers') -and
    $Tests.Contains('TelemetrySinkDropsControlCharacterAndUnknownOperations') -and
    $Tests.Contains('TelemetrySinkDropsInvalidOutcomes') -and
    $Tests.Contains('UnsafeOrUnknownRequestContractNameIsRejectedWithoutTelemetryContamination')
}

$scopePath = Join-Path $RepositoryRoot 'build/governance/d13-scope-lock.json'
$scope = Get-Content -LiteralPath $scopePath -Raw -Encoding utf8 | ConvertFrom-Json

if ($SelfTest) {
    $applicationSource = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/customer-identity/Application/CustomerIdentityApplication.cs') -Raw -Encoding utf8
    $portsSource = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/customer-identity/Application/CustomerIdentityPorts.cs') -Raw -Encoding utf8
    $repositorySource = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/customer-identity/Infrastructure/ReferenceCustomerIdentityAdapters.cs') -Raw -Encoding utf8
    $focusedTests = @(Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'tests/customer-identity/Monergy.CustomerIdentity.Tests') -Recurse -File -Filter '*.cs' |
        ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw -Encoding utf8 }) -join "`n"
    $checks = [ordered]@{
        'valid D13 scope' = Test-Scope $scope
        'valid R1 and R2 remediation source' = Test-RemediationSource $applicationSource $portsSource $repositorySource $focusedTests
    }
    foreach ($mutation in @(
        @{ name='second Feature rejected'; path='features'; value=@($scope.features + [pscustomobject]@{id='M2-WS02-E01-F02';owner='Customer & Identity Service';readiness='READY'}) },
        @{ name='contract expansion rejected'; path='newlyRealizedContracts'; value=@($scope.newlyRealizedContracts + 'CID-005') },
        @{ name='CID-007 realization rejected'; path='excludedContracts'; value=@('CID-005','CID-006') },
        @{ name='acceptance claim rejected'; path='status'; value='ACCEPTED_COMPLETE' },
        @{ name='Production evidence rejected'; path='evidenceLevel'; value='PRODUCTION' },
        @{ name='persistent adapter rejected'; path='persistence'; value='POSTGRESQL' },
        @{ name='provider selection rejected'; path='providerSelection'; value='PRODUCTION_PROVIDER' },
        @{ name='frontend scope rejected'; path='frontendChange'; value='LOGIN_UI' },
        @{ name='event realization rejected'; path='eventRealization'; value='CID-005' },
        @{ name='D11 source change rejected'; path='d11.sourceChange'; value='MODIFIED' },
        @{ name='D12 source change rejected'; path='d12.sourceChange'; value='MODIFIED' },
        @{ name='ordinary push full run rejected'; path='hostedVerification.ordinaryBranchPushFullRun'; value=$true },
        @{ name='stage-gate advancement rejected'; path='stageGates.SG-02'; value='READY' }
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
    $checks['unsafe telemetry mutation rejected'] = -not (Test-RemediationSource $applicationSource (
        $portsSource.Replace('!value.Any(char.IsControl)', 'true')) $repositorySource $focusedTests)
    $checks['missing application canonical-resolution guard rejected'] = -not (Test-RemediationSource (
        $applicationSource.Replace('CustomerIdentityBoundaryValidation.IsValidCanonicalResolution(resolved)',
            'true')) $portsSource $repositorySource $focusedTests)
    $checks['missing telemetry operation allowlist rejected'] = -not (Test-RemediationSource $applicationSource (
        $portsSource.Replace('AcceptedTelemetryOperations.Contains(signal.Operation)', 'true')) $repositorySource $focusedTests)
    $checks['missing telemetry outcome vocabulary rejected'] = -not (Test-RemediationSource $applicationSource (
        $portsSource.Replace('AcceptedTelemetryOutcomes.Contains(signal.Outcome)', 'true')) $repositorySource $focusedTests)
    foreach ($entry in $checks.GetEnumerator()) {
        Write-Output "SELF-TEST $(if ($entry.Value) { 'PASS' } else { 'FAIL' }): $($entry.Key)"
    }
    if (@($checks.Values | Where-Object { -not $_ }).Count) { throw 'D13 negative self-tests failed.' }
    Write-Output "D13 self-tests passed: $($checks.Count)/$($checks.Count) (positive controls: 2; negative mutations: 17)."
    exit 0
}

$checks = [ordered]@{}
$checks['Exact one-Feature D13 scope and unchanged governance'] = Test-Scope $scope

$catalogText = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'contracts/Monergy.Contracts/D13ContractCatalog.cs') -Raw -Encoding utf8
$catalogIds = @([regex]::Matches($catalogText, 'new\("(CID-\d{3})"') | ForEach-Object { $_.Groups[1].Value })
$checks['Exactly CID-001 through CID-004 are cataloged by D13'] =
    Test-ExactSet $catalogIds @('CID-001','CID-002','CID-003','CID-004')
$legacyCatalog = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'contracts/Monergy.Contracts/Vs02ContractCatalog.cs') -Raw -Encoding utf8
$checks['Historical D03 fourteen-contract catalog remains isolated'] =
    ([regex]::Matches($legacyCatalog, 'new\("CID-\d{3}"')).Count -eq 14 -and
    $legacyCatalog -notmatch 'CID-00[1-4]'

$contracts = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'contracts/Monergy.Contracts/D13CustomerIdentityContracts.cs') -Raw -Encoding utf8
$checks['Four deterministic canonical contract names and DTO families exist'] =
    @('GetCustomer','GetTrustedActorContext','Register/UpdateCustomer','RecordKycResult','CustomerProjection','KycVerification') |
        ForEach-Object { $contracts.Contains($_) } | Where-Object { -not $_ } |
        Measure-Object | Select-Object -ExpandProperty Count | ForEach-Object { $_ -eq 0 }
$checks['D13 wire DTOs contain no provider-native or excluded-event surface'] =
    $contracts -notmatch '(?i)Claim|Bearer|Password|Credential|Mfa|ProviderPayload|CID-005|CID-006|CID-007'

$applicationPath = Join-Path $RepositoryRoot 'services/customer-identity/Application/CustomerIdentityApplication.cs'
$portsPath = Join-Path $RepositoryRoot 'services/customer-identity/Application/CustomerIdentityPorts.cs'
$repositoryPath = Join-Path $RepositoryRoot 'services/customer-identity/Infrastructure/ReferenceCustomerIdentityAdapters.cs'
$application = Get-Content -LiteralPath $applicationPath -Raw -Encoding utf8
$ports = Get-Content -LiteralPath $portsPath -Raw -Encoding utf8
$repository = Get-Content -LiteralPath $repositoryPath -Raw -Encoding utf8
$checks['Provider abstraction is replaceable guarded and fail closed'] =
    $ports.Contains('ICustomerAuthenticationProvider') -and
    $repository.Contains('ReferenceAdapterGuard.EnsureAllowed(configuration)') -and
    $application.Contains('identity.authentication.unverifiable') -and
    $application.Contains('ContractErrorCategory.AuthenticationRequired')
$checks['Canonical actor resolution is validated before CID-002 success'] =
    $ports.Contains('IsValidCanonicalResolution') -and
    $ports.Contains('actor.AuthenticatedAt != default') -and
    (Test-ContainsAll $ports @('"CUSTOMER"','"ADVISOR"','"ADMINISTRATOR"','"PROFESSIONAL"')) -and
    $application.Contains('identity.authentication.malformed') -and
    $repository.Contains('The reference authentication record is malformed.')
$checks['Customer and KYC authority use typed scoped idempotency and immutable snapshots'] =
    $ports.Contains('CustomerMutationIdentity') -and $ports.Contains('KycMutationIdentity') -and
    $repository.Contains('Dictionary<CustomerMutationIdentity') -and
    $repository.Contains('Dictionary<KycMutationIdentity') -and
    $repository.Contains('MutationDisposition.Replayed') -and
    $repository.Contains('ToImmutableArray') -and $repository.Contains('with { }')
$checks['Historical D13 sources introduce no provider database broker or event; only the governed AM-05 migration is additive'] =
    (Test-ForbiddenImplementation ($application + "`n" + $ports + "`n" + $repository)) -and
    -not $application.Contains('DomainEvent<') -and
    @(Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'services/customer-identity') -Recurse -File -Filter '*.sql' | Where-Object { $_.Name -cne '0001_tenant_access_sessions.sql' }).Count -eq 0
$telemetryShape = [regex]::Match($ports,
    'public sealed record CustomerIdentityTelemetrySignal\((?<shape>[\s\S]*?)\);').Groups['shape'].Value
$checks['Telemetry signals use exact operation outcome and safe identifier vocabularies'] =
    $telemetryShape.Contains('string Operation') -and $telemetryShape.Contains('string Outcome') -and
    $telemetryShape.Contains('string CustomerId') -and $telemetryShape.Contains('string RequestId') -and
    $telemetryShape.Contains('string CorrelationId') -and
    $telemetryShape -notmatch 'DisplayName|StatusCode|VerificationId|AuthenticationReference' -and
    $ports.Contains('!value.Any(char.IsControl)') -and
    $ports.Contains('AcceptedTelemetryOperations.Contains(signal.Operation)') -and
    $ports.Contains('AcceptedTelemetryOutcomes.Contains(signal.Outcome)') -and
    $ports.Contains('Enum.GetNames<ContractOutcome>()') -and
    (Test-ContainsAll $ports @(
        'D13ContractNames.GetCustomer',
        'D13ContractNames.GetTrustedActorContext',
        'D13ContractNames.RegisterOrUpdateCustomer',
        'D13ContractNames.RecordKycResult')) -and
    $application.Contains('CustomerIdentityBoundaryValidation.IsSafeTelemetrySignal(signal)') -and
    $repository.Contains('CustomerIdentityBoundaryValidation.IsSafeTelemetrySignal(signal)')

$endpoints = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/customer-identity/CustomerIdentityEndpoints.cs') -Raw -Encoding utf8
$program = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/customer-identity/Program.cs') -Raw -Encoding utf8
$routeIds = @([regex]::Matches($endpoints, '/contracts/(cid-\d{3})/v1') | ForEach-Object { $_.Groups[1].Value.ToUpperInvariant() })
$checks['Exactly four guarded HTTP routes use strict contract JSON'] =
    (Test-ExactSet $routeIds @('CID-001','CID-002','CID-003','CID-004')) -and
    $program.Contains('ContractJson.Options.UnmappedMemberHandling') -and
    $program.Contains('ReferenceAdapterGuard.IsSelected')

$matrix = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/governance/d13-scenario-matrix.json') -Raw -Encoding utf8 | ConvertFrom-Json
$scenarioIds = 1..10 | ForEach-Object { 'D13-T{0:D2}' -f $_ }
$checks['All D13-T01 through D13-T10 scenario groups name executable evidence'] =
    $matrix.status -ceq 'CANDIDATE_EVIDENCE_IMPLEMENTED' -and
    (Test-ExactSet @($matrix.scenarios.id) $scenarioIds) -and
    @($matrix.scenarios | Where-Object {
        $testsProperty = $_.PSObject.Properties['tests']
        $commandsProperty = $_.PSObject.Properties['commands']
        $testCount = if ($null -eq $testsProperty) { 0 } else { @($testsProperty.Value).Count }
        $commandCount = if ($null -eq $commandsProperty) { 0 } else { @($commandsProperty.Value).Count }
        @($_.evidenceTypes).Count -eq 0 -or
            ($testCount -eq 0 -and $commandCount -eq 0)
    }).Count -eq 0
$testRoot = Join-Path $RepositoryRoot 'tests/customer-identity/Monergy.CustomerIdentity.Tests'
$testText = @(Get-ChildItem -LiteralPath $testRoot -Recurse -File -Filter '*.cs' |
    ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw -Encoding utf8 }) -join "`n"
$namedTests = @($matrix.scenarios | ForEach-Object {
    $property = $_.PSObject.Properties['tests']
    if ($null -ne $property) { $property.Value }
} | Where-Object { $_ } | Select-Object -Unique)
$checks['Every scenario-matrix test is present in focused source'] =
    @($namedTests | Where-Object { -not $testText.Contains([string]$_) }).Count -eq 0
$checks['R1 and R2 executable negative evidence is governed'] =
    Test-RemediationSource $application $ports $repository $testText

$manifest = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'repository.manifest.json') -Raw -Encoding utf8 | ConvertFrom-Json
$checks['Repository manifest records bounded D13 candidate'] =
    $manifest.d13Status -ceq 'CANDIDATE_PENDING_CTO_REVIEW' -and
    $manifest.d13FeatureState -ceq 'IMPLEMENTATION_CANDIDATE_SIMULATOR_1_OF_1' -and
    $manifest.d13ContractState -ceq 'REALIZED_4_CANDIDATE' -and
    $manifest.d13ProviderSelection -ceq 'NONE' -and
    $manifest.deploymentState -ceq 'NOT_DEPLOYED'

$solution = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'Monergy.Application.slnx') -Raw -Encoding utf8
$checks['Focused D13 test project is included in the solution'] =
    $solution.Contains('tests/customer-identity/Monergy.CustomerIdentity.Tests/Monergy.CustomerIdentity.Tests.csproj')

$dockerfile = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/customer-identity/Dockerfile') -Raw -Encoding utf8
$checks['Customer Identity OCI digest pins are unchanged'] =
    $dockerfile.Contains($scope.customerIdentityOciBaseImages.sdk) -and
    $dockerfile.Contains($scope.customerIdentityOciBaseImages.runtime) -and
    @([regex]::Matches($dockerfile, '(?m)^FROM ')).Count -eq 2

foreach ($entry in $checks.GetEnumerator()) {
    Write-Output "[$(if ($entry.Value) { 'PASS' } else { 'FAIL' })] $($entry.Key)"
}
$failed = @($checks.Values | Where-Object { -not $_ })
if ($failed.Count) { throw "D13 verification failed: $($checks.Count - $failed.Count)/$($checks.Count)." }
Write-Output "D13 verification passed: $($checks.Count)/$($checks.Count); candidate only."

