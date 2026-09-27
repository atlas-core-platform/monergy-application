[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Split-Path -Parent $PSScriptRoot
}

function Test-Scope {
    param($Scope)
    $featureIds = 'M2-WS06-E01-F01|M2-WS06-E01-F02|M2-WS06-E01-F03|M2-WS06-E02-F01|M2-WS06-E02-F02|M2-WS06-E02-F03'
    return (($Scope.features.id -join '|') -ceq $featureIds -and
        @($Scope.features).Count -eq 6 -and
        @($Scope.features | Where-Object { $_.owner -cne 'Integration Gateway Service' -or $_.readiness -cne 'READY' }).Count -eq 0 -and
        ($Scope.consumedContracts -join '|') -ceq 'CID-007|CID-011' -and
        ($Scope.newlyRealizedContracts -join '|') -ceq 'CID-015|CID-016|CID-017|CID-018' -and
        ($Scope.services -join '|') -ceq 'Integration Gateway Service|Consent Service' -and
        ($Scope.businessObjects -join '|') -ceq 'Data Source|Consent' -and
        $Scope.status -ceq 'CANDIDATE_PENDING_CTO_REVIEW' -and
        $Scope.evidenceLevel -ceq 'SIMULATOR' -and
        ($Scope.executionZones -join '|') -ceq 'LOCAL|CI_EPHEMERAL' -and
        $Scope.providerSelection -ceq 'UNRESOLVED' -and
        $Scope.physicalPersistenceOrBroker -ceq 'NOT_SELECTED' -and
        $Scope.frontendBusinessChange -ceq 'NONE' -and
        $Scope.stageGates.'SG-02' -ceq 'CONDITIONALLY_READY' -and
        $Scope.outstandingDecisions.'OD-04' -ceq 'UNRESOLVED' -and
        $Scope.outstandingDecisions.'OD-05' -ceq 'UNRESOLVED' -and
        $Scope.outstandingDecisions.'OD-06' -ceq 'UNRESOLVED' -and
        $Scope.architectureIntegrity.evidenceSource -ceq 'CTO_USER_EXTERNAL_VERIFICATION' -and
        $Scope.architectureIntegrity.head -ceq 'c7c2e32c3f924b409c9f0b84cc061b03e725b722' -and
        $Scope.architectureIntegrity.worktree -ceq 'CLEAN' -and
        $Scope.architectureIntegrity.'R1-R7' -ceq 'FROZEN_UNCHANGED' -and
        $Scope.architectureIntegrity.R8 -ceq 'ABSENT')
}

function Test-ForbiddenImplementation {
    param([string]$Text)
    $Text -notmatch '(?i)M2-WS06-E03|Plaid|Yodlee|Finicity|Salt\s*Edge|Tink\b|EntityFrameworkCore|SqlConnection|Npgsql|MongoClient|Kafka|RabbitMQ|ServiceBusClient'
}

function Test-ChangedPaths {
    param([string[]]$Paths)
    @($Paths | Where-Object { $_ -match '^(apps/customer-web/|apps\\customer-web\\)' }).Count -eq 0
}

$scopePath = Join-Path $RepositoryRoot 'build/governance/d06-scope-lock.json'
$scope = Get-Content -LiteralPath $scopePath -Raw -Encoding utf8 | ConvertFrom-Json

if ($SelfTest) {
    $checks = [ordered]@{ 'valid candidate' = Test-Scope $scope }

    $copy = $scope | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $copy.features += [pscustomobject]@{id='M2-WS06-E02-F04';name='Unauthorized';owner='Integration Gateway Service';readiness='READY'}
    $checks['unauthorized seventh Feature rejected'] = -not (Test-Scope $copy)

    $copy = $scope | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $copy.features = @($copy.features | Select-Object -Skip 1)
    $checks['missing authorized Feature rejected'] = -not (Test-Scope $copy)

    $copy = $scope | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $copy.features[0].id = 'M2-WS06-E03-F01'
    $checks['WS06-E03 provider-specific Feature rejected'] = -not (Test-Scope $copy)

    $checks['provider SDK rejected'] = -not (Test-ForbiddenImplementation '<PackageReference Include="Plaid.Net" />')
    $checks['provider selection rejected'] = -not (Test-ForbiddenImplementation 'Yodlee connector implementation')
    $checks['physical database rejected'] = -not (Test-ForbiddenImplementation 'SqlConnection')
    $checks['physical broker rejected'] = -not (Test-ForbiddenImplementation 'Kafka producer')
    $checks['frontend business implementation rejected'] = -not (Test-ChangedPaths @('apps/customer-web/src/features/provider-accounts.tsx'))

    foreach ($evidence in @('SANDBOX', 'UAT', 'PRODUCTION')) {
        $copy = $scope | ConvertTo-Json -Depth 20 | ConvertFrom-Json
        $copy.evidenceLevel = $evidence
        $checks["$evidence evidence claim rejected"] = -not (Test-Scope $copy)
    }

    $copy = $scope | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $copy.stageGates.'SG-02' = 'READY'
    $checks['SG-02 READY rejected'] = -not (Test-Scope $copy)

    foreach ($decision in @('OD-04', 'OD-05', 'OD-06')) {
        $copy = $scope | ConvertTo-Json -Depth 20 | ConvertFrom-Json
        $copy.outstandingDecisions.$decision = 'RESOLVED'
        $checks["$decision resolved rejected"] = -not (Test-Scope $copy)
    }

    $copy = $scope | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $copy.architectureIntegrity.'R1-R7' = 'MUTATED'
    $checks['R1-R7 changed or mutated claim rejected'] = -not (Test-Scope $copy)

    $copy = $scope | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $copy.architectureIntegrity.R8 = 'INTRODUCED'
    $checks['R8 introduced claim rejected'] = -not (Test-Scope $copy)

    foreach ($entry in $checks.GetEnumerator()) {
        Write-Output "SELF-TEST $(if ($entry.Value) { 'PASS' } else { 'FAIL' }): $($entry.Key)"
    }
    if (@($checks.Values | Where-Object { -not $_ }).Count) { throw 'D06 negative self-tests failed.' }
    Write-Output "D06 self-tests passed: $($checks.Count)/$($checks.Count)."
    exit 0
}

$checks = [ordered]@{}
$checks['Exact six-Feature candidate scope'] = Test-Scope $scope
$checks['Starting application baseline pinned'] = $scope.startingCommits.application -ceq '7716e8f6f214ec81274508faa3d6041545141f2b'
$checks['Starting architecture baseline pinned'] = $scope.startingCommits.architecture -ceq 'c7c2e32c3f924b409c9f0b84cc061b03e725b722'
$checks['External architecture integrity evidence is recorded without repository inspection'] =
    $scope.architectureIntegrity.evidenceSource -ceq 'CTO_USER_EXTERNAL_VERIFICATION' -and
    $scope.architectureIntegrity.head -ceq $scope.startingCommits.architecture -and
    $scope.architectureIntegrity.worktree -ceq 'CLEAN' -and
    $scope.architectureIntegrity.'R1-R7' -ceq 'FROZEN_UNCHANGED' -and
    $scope.architectureIntegrity.R8 -ceq 'ABSENT'

$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'services/integration-gateway') -Recurse -File |
    Where-Object { $_.Extension -in '.cs', '.csproj', '.md' -and $_.FullName -notmatch '[\\/](bin|obj)[\\/]' })
$sourceText = @($sourceFiles | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
$checks['No provider SDK/name or physical DB/broker selected'] = Test-ForbiddenImplementation $sourceText
$checks['Provider-neutral connector port exists'] = $sourceText.Contains('interface IProviderConnector') -and $sourceText.Contains('ReferenceProviderConnector')
$checks['Authorization and consent remain distinct'] = $sourceText.Contains('IGatewayAuthorizationPolicy') -and $sourceText.Contains('IConsentDecisionPort')
$checks['Current access checked before replay and each retry'] = $sourceText.Contains('EvaluateAccessAsync') -and
    $sourceText.Contains('authorizationPolicy.AuthorizeAsync') -and $sourceText.Contains('consentDecisions.EvaluateAsync')
$checks['Idempotency conflict and concurrent replay are explicit'] = $sourceText.Contains('integration.idempotency.conflict') -and $sourceText.Contains('ConcurrentReplay')
$checks['Rate limit, unknown outcome and circuit states are explicit'] = $sourceText.Contains('RateLimited') -and $sourceText.Contains('UnknownOutcome') -and $sourceText.Contains('integration.circuit.open')
$checks['Connector health is derived'] = $sourceText.Contains('ConnectorHealth') -and $sourceText.Contains('ConsecutiveFailures')
$checks['Telemetry surface excludes request fields and security context'] = $sourceText.Contains('GatewayTelemetrySignal') -and -not $sourceText.Contains('GatewayTelemetrySignal(`n    TrustedSecurityContext')
$catalog = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'contracts/Monergy.Contracts/D06ContractCatalog.cs') -Raw
$checks['D06 contract catalog has exact realized set'] = ([regex]::Matches($catalog, 'CID-01[5-8]')).Count -eq 4
$schema = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'contracts/schemas/integration-gateway-core.schema.json') -Raw | ConvertFrom-Json
$checks['Closed D06 wire schema exists'] = $schema.oneOf.Count -eq 4 -and $schema.unevaluatedProperties -eq $false
$checks['Isolated D06 tests exist'] = Test-Path -LiteralPath (Join-Path $RepositoryRoot 'tests/integration-gateway/Monergy.IntegrationGateway.Tests/Monergy.IntegrationGateway.Tests.csproj')
$changedPaths = @(& git -C $RepositoryRoot diff --name-only $scope.startingCommits.application)
$checks['No frontend business implementation'] = Test-ChangedPaths $changedPaths

foreach ($entry in $checks.GetEnumerator()) { Write-Output "[$(if($entry.Value){'PASS'}else{'FAIL'})] $($entry.Key)" }
$failed = @($checks.Values | Where-Object { -not $_ })
if ($failed.Count) { throw "D06 verification failed: $($checks.Count-$failed.Count)/$($checks.Count)." }
Write-Output "D06 verification passed: $($checks.Count)/$($checks.Count); candidate only."
