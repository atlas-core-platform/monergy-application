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
    $expectedFeatures = @('M2-WS09-E01-F01','M2-WS09-E01-F02','M2-WS09-E01-F03','M2-WS09-E02-F01')
    $expectedContracts = @('CID-005','CID-007','CID-011','CID-012','CID-013','CID-017','CID-023','CID-034','CID-040','CID-049','CID-053','CID-055','CID-056','CID-057','CID-058','CID-059','CID-060','CID-061')
    $Value.deliverable -ceq 'MWP-03-D10' -and
    $Value.status -ceq 'CANDIDATE_PENDING_CTO_REVIEW' -and
    $Value.evidenceLevel -ceq 'LOCAL_CI_EPHEMERAL_PHYSICAL' -and
    (Test-ExactSet @($Value.features) $expectedFeatures) -and
    (Test-ExactSet @($Value.contractFootprint) $expectedContracts) -and
    (Test-ExactSet @($Value.advancedContracts) @('CID-055','CID-057')) -and
    (Test-ExactSet @($Value.newlyRealizedContracts) @('CID-056','CID-058','CID-059','CID-060','CID-061')) -and
    $Value.database.name -ceq 'monergy_job_management' -and
    $Value.database.schema -ceq 'job_management' -and
    $Value.database.technology -ceq 'PostgreSQL' -and
    $Value.database.version -ceq '18.6' -and
    $Value.database.productionHosting -ceq 'NOT_SELECTED' -and
    $Value.eventTransport.implementation -ceq 'PROVIDER_NEUTRAL_REFERENCE' -and
    (Test-ExactSet @($Value.eventTransport.executionZones) @('LOCAL','CI_EPHEMERAL')) -and
    $Value.eventTransport.productionProvider -ceq 'UNRESOLVED_OD_15' -and
    $Value.conditionalPlatformFeatures.'M2-WS01-E03-F01' -ceq 'NOT_ACCEPTED_BY_D10' -and
    $Value.conditionalPlatformFeatures.'M2-WS01-E03-F02' -ceq 'NOT_ACCEPTED_BY_D10' -and
    @($Value.outstandingDecisions.PSObject.Properties | Where-Object Value -cne 'UNRESOLVED').Count -eq 0 -and
    $Value.stageGates.'SG-01' -ceq 'READY' -and
    $Value.stageGates.'SG-02' -ceq 'CONDITIONALLY_READY' -and
    $Value.stageGates.'SG-03' -ceq 'BLOCKED' -and
    $Value.stageGates.'SG-04' -ceq 'BLOCKED' -and
    $Value.architectureIntegrity.'R1-R7' -ceq 'FROZEN_UNCHANGED' -and
    $Value.architectureIntegrity.R8 -ceq 'ABSENT'
}

$scopePath = Join-Path $RepositoryRoot 'build/governance/d10-scope-lock.json'
$scope = Get-Content -LiteralPath $scopePath -Raw -Encoding utf8 | ConvertFrom-Json

if ($SelfTest) {
    $checks = [ordered]@{ 'valid D10 scope' = Test-Scope $scope }
    foreach ($mutation in @(
        @{ name='feature removal rejected'; path='features'; value=@('M2-WS09-E01-F01') },
        @{ name='contract expansion rejected'; path='contractFootprint'; value=@($scope.contractFootprint + 'CID-062') },
        @{ name='acceptance rejected'; path='status'; value='ACCEPTED_COMPLETE' },
        @{ name='Production evidence rejected'; path='evidenceLevel'; value='PRODUCTION' },
        @{ name='database hosting rejected'; path='database.productionHosting'; value='Managed PostgreSQL' },
        @{ name='broker selection rejected'; path='eventTransport.productionProvider'; value='Kafka' },
        @{ name='platform Feature acceptance rejected'; path='conditionalPlatformFeatures.M2-WS01-E03-F01'; value='ACCEPTED' },
        @{ name='OD-15 resolution rejected'; path='outstandingDecisions.OD-15'; value='RESOLVED' },
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
    if (@($checks.Values | Where-Object { -not $_ }).Count) { throw 'D10 negative self-tests failed.' }
    Write-Output "D10 self-tests passed: $($checks.Count)/$($checks.Count)."
    exit 0
}

$checks = [ordered]@{}
$checks['D10 scope and unchanged governance'] = Test-Scope $scope
$catalog = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'contracts/Monergy.Contracts/D10ContractCatalog.cs') -Raw -Encoding utf8
$catalogIds = @([regex]::Matches($catalog, 'new\("(CID-\d{3})"') | ForEach-Object { $_.Groups[1].Value })
$checks['Exact eighteen-contract implementation catalog'] = Test-ExactSet $catalogIds @($scope.contractFootprint)
$contractSource = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'contracts/Monergy.Contracts/Vs02Contracts.cs') -Raw -Encoding utf8
$checks['Cancel and lifecycle contracts are explicit'] = @('CancelJob','JobStarted','JobCompleted','JobFailed','CancellationRequested') | ForEach-Object { $contractSource.Contains($_) } | Where-Object { -not $_ } | Measure-Object | Select-Object -ExpandProperty Count | ForEach-Object { $_ -eq 0 }
$migration = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/job-management/migrations/0001_durable_job_authority.sql') -Raw -Encoding utf8
$checks['Service-owned durable Job schema exists'] = @('job_management.jobs','job_management.idempotency_operations','job_management.execution_attempts','job_management.outbox') | ForEach-Object { $migration.Contains($_) } | Where-Object { -not $_ } | Measure-Object | Select-Object -ExpandProperty Count | ForEach-Object { $_ -eq 0 }
$checks['Job outbox least privilege is column-scoped'] = $migration.Contains('GRANT SELECT, INSERT, UPDATE (dispatched_at) ON job_management.outbox') -and -not $migration.Contains('GRANT SELECT, INSERT, UPDATE ON job_management.outbox')
$repository = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/job-management/Infrastructure/PostgresJobRepository.cs') -Raw -Encoding utf8
$checks['Durable lifecycle includes idempotency attempts recovery and outbox'] = @('pg_advisory_xact_lock','idempotency_operations','execution_attempts','RecoverInterruptedAsync','PendingEventsAsync','MarkDispatchedAsync') | ForEach-Object { $repository.Contains($_) } | Where-Object { -not $_ } | Measure-Object | Select-Object -ExpandProperty Count | ForEach-Object { $_ -eq 0 }
$application = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/job-management/Application/JobManagementApplication.cs') -Raw -Encoding utf8
$authorizeIndex = $application.IndexOf('AuthorizeAsync', [StringComparison]::Ordinal)
$consentIndex = $application.IndexOf('EvaluateAsync', [StringComparison]::Ordinal)
$executeIndex = $application.IndexOf('target.ExecuteAsync', [StringComparison]::Ordinal)
$checks['Authorization and consent precede delegated execution'] = $authorizeIndex -ge 0 -and $consentIndex -gt $authorizeIndex -and $executeIndex -gt $consentIndex
$transport = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'shared/platform/Monergy.Platform/ReferenceEventTransport.cs') -Raw -Encoding utf8
$checks['Reference transport fails closed and exposes delivery telemetry'] = $transport.Contains('D10 reference event transport may run only in LOCAL or CI_EPHEMERAL') -and $transport.Contains('EventDeliveryTelemetrySnapshot') -and $transport.Contains('RecordDuplicateDeliveries') -and $transport.Contains('RecordAuditIngestionFailure')
$auditMigration = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/audit/migrations/0002_event_inbox.sql') -Raw -Encoding utf8
$auditRepository = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/audit/Infrastructure/PostgresAuditEvidenceRepository.cs') -Raw -Encoding utf8
$checks['Audit inbox is separate from append-only evidence'] = $auditMigration.Contains('CREATE TABLE audit.inbox') -and $auditRepository.Contains('INSERT INTO audit.inbox') -and $auditRepository.Contains('INSERT INTO audit.evidence')
$jobDispatcher = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/job-management/Infrastructure/JobOutboxDispatcher.cs') -Raw -Encoding utf8
$reportDispatcher = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/reporting/Infrastructure/ReportingAuditOutboxDispatcher.cs') -Raw -Encoding utf8
$checks['Job and Reporting outboxes propagate with crash-window injection'] = $jobDispatcher.Contains('FailAfterPublishBeforeMarkerOnce') -and $reportDispatcher.Contains('FailAfterPublishBeforeMarkerOnce')
$physicalTests = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'tests/job-management/Monergy.JobManagement.Tests/PhysicalJobAndAuditTests.cs') -Raw -Encoding utf8
$behaviorTests = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'tests/job-management/Monergy.JobManagement.Tests/DurableJobBehaviorTests.cs') -Raw -Encoding utf8
$checks['Deterministic lifecycle and physical failure evidence exists'] = @('DuplicateSubmission','ExecutionReevaluatesAuthorizationAndConsent','CancellationBeforeAndDuringExecution','InterruptedRunningJob','PersistedJobSurvives','JobOutboxSurvives','ReportingOutboxPropagates','RuntimeRolesEnforce','OperationalMetricsExpose') | ForEach-Object { ($behaviorTests + $physicalTests).Contains($_) } | Where-Object { -not $_ } | Measure-Object | Select-Object -ExpandProperty Count | ForEach-Object { $_ -eq 0 }
$compose = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/d10/compose.yml') -Raw -Encoding utf8
$checks['D10 physical fixture is pinned PostgreSQL only'] = $compose.Contains('postgres:18.6@sha256:5a5a84b19854a9ffaa54082c166ff4ec27473a361e496e5ea167f298f2da9722') -and -not $compose.Contains(':latest')
$projects = @(Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'services') -Recurse -File -Filter '*.csproj')
$projectText = @($projects | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
$checks['No Production broker or provider dependency introduced'] = $projectText -notmatch 'Kafka|RabbitMQ|NATS|ServiceBus|EventBridge|Google\.Cloud\.PubSub|Amazon\.SQS|Amazon\.SNS'
$atlasArtifacts = @(Get-ChildItem -LiteralPath $RepositoryRoot -Recurse -File -Filter '*.atlas.json')
$checks['No Atlas artifact or R8 introduced in application'] = $atlasArtifacts.Count -eq 0 -and -not (Test-Path -LiteralPath (Join-Path $RepositoryRoot 'R8'))

foreach ($entry in $checks.GetEnumerator()) { Write-Output "[$(if ($entry.Value) { 'PASS' } else { 'FAIL' })] $($entry.Key)" }
$failed = @($checks.Values | Where-Object { -not $_ })
if ($failed.Count) { throw "D10 verification failed: $($checks.Count - $failed.Count)/$($checks.Count)." }
Write-Output "D10 verification passed: $($checks.Count)/$($checks.Count); candidate only."
