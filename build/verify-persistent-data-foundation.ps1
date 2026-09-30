[CmdletBinding()]
param([string]$RepositoryRoot, [switch]$SelfTest, [switch]$D10ForwardRegression)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = Split-Path -Parent $PSScriptRoot }

function Test-ExactSet([object[]]$Actual, [object[]]$Expected) {
    $Actual.Count -eq $Expected.Count -and (@($Actual | Sort-Object) -join '|') -ceq (@($Expected | Sort-Object) -join '|')
}
function Test-Scope($Scope) {
    $Scope.deliverable -ceq 'MWP-03-D09' -and
    $Scope.status -ceq 'CANDIDATE_PENDING_CTO_REVIEW' -and
    $Scope.businessFeatureCompletion -ceq 'NONE' -and
    (Test-ExactSet @($Scope.executionZones) @('LOCAL','CI_EPHEMERAL')) -and
    $Scope.database.technology -ceq 'PostgreSQL' -and $Scope.database.version -ceq '18.6' -and
    $Scope.database.productionHosting -ceq 'NOT_SELECTED' -and
    $Scope.packages.Npgsql -ceq '10.0.3' -and $Scope.packages.Dapper -ceq '2.1.89' -and
    $Scope.packages.'dbup-postgresql' -ceq '7.0.1' -and $Scope.packages.'AWSSDK.S3' -ceq '4.0.103.4' -and
    $Scope.evidenceContent.technology -ceq 'S3_COMPATIBLE' -and
    $Scope.evidenceContent.localCiFixture -ceq 'SeaweedFS 4.47' -and
    $Scope.evidenceContent.productionHosting -ceq 'NOT_SELECTED' -and
    $Scope.evidenceContent.integrity -ceq 'MONERGY_SHA256_NOT_ETAG' -and
    (Test-ExactSet @($Scope.persistedServices) @('evidence','financial-profile','financial-rules','reporting','audit')) -and
    (Test-ExactSet @($Scope.transactionalOutbox) @('evidence','financial-profile','financial-rules','reporting')) -and
    @($Scope.providers.PSObject.Properties | Where-Object Value -cne 'NOT_SELECTED').Count -eq 0 -and
    $Scope.stageGates.'SG-01' -ceq 'READY' -and $Scope.stageGates.'SG-02' -ceq 'CONDITIONALLY_READY' -and
    $Scope.stageGates.'SG-03' -ceq 'BLOCKED' -and $Scope.stageGates.'SG-04' -ceq 'BLOCKED' -and
    $Scope.architectureIntegrity.'R1-R7' -ceq 'FROZEN_UNCHANGED' -and $Scope.architectureIntegrity.R8 -ceq 'ABSENT'
}

$scopePath = Join-Path $RepositoryRoot 'build/governance/d09-scope-lock.json'
$scope = Get-Content -LiteralPath $scopePath -Raw | ConvertFrom-Json
$d10ScopePath = Join-Path $RepositoryRoot 'build/governance/d10-scope-lock.json'
if ($D10ForwardRegression -and -not (Test-Path -LiteralPath $d10ScopePath)) {
    throw 'D10 forward regression requires the governed D10 scope lock.'
}
if ($SelfTest) {
    $checks = [ordered]@{ 'valid D09 scope' = Test-Scope $scope }
    foreach ($mutation in @(
        @{name='PostgreSQL 19 rejected'; path='database.version'; value='19.0-beta'},
        @{name='Production hosting rejected'; path='database.productionHosting'; value='Amazon RDS'},
        @{name='Feature claim rejected'; path='businessFeatureCompletion'; value='M2-NEW'},
        @{name='QA execution rejected'; path='executionZones'; value=@('LOCAL','QA')},
        @{name='EF Core substitution rejected'; path='packages.Npgsql'; value='EntityFrameworkCore'},
        @{name='Floating S3 fixture rejected'; path='evidenceContent.localCiFixture'; value='SeaweedFS latest'},
        @{name='ETag integrity rejected'; path='evidenceContent.integrity'; value='ETAG'},
        @{name='Extra persisted service rejected'; path='persistedServices'; value=@('evidence','financial-profile','financial-rules','reporting','audit','job-management')},
        @{name='Missing outbox rejected'; path='transactionalOutbox'; value=@('evidence','financial-profile','financial-rules')},
        @{name='Broker selection rejected'; path='providers.messageBroker'; value='Kafka'},
        @{name='Search provider rejected'; path='providers.searchVector'; value='OpenSearch'},
        @{name='AI provider rejected'; path='providers.aiModel'; value='OpenAI'},
        @{name='Cloud selection rejected'; path='providers.cloudOrchestrator'; value='Kubernetes'},
        @{name='SG-02 advancement rejected'; path='stageGates.SG-02'; value='READY'},
        @{name='R8 rejected'; path='architectureIntegrity.R8'; value='PRESENT'},
        @{name='AWS production claim rejected'; path='evidenceContent.productionHosting'; value='Amazon S3'},
        @{name='Wrong package pin rejected'; path='packages.Dapper'; value='2.1.90'}
    )) {
        $copy = $scope | ConvertTo-Json -Depth 20 | ConvertFrom-Json
        $parts = $mutation.path.Split('.')
        if ($parts.Count -eq 1) {
            $copy.($parts[0]) = $mutation.value
        } else {
            $target = $copy
            foreach ($part in $parts[0..($parts.Count-2)]) { $target = $target.$part }
            $target.($parts[-1]) = $mutation.value
        }
        $checks[$mutation.name] = -not (Test-Scope $copy)
    }
    foreach ($entry in $checks.GetEnumerator()) { Write-Output "SELF-TEST $(if($entry.Value){'PASS'}else{'FAIL'}): $($entry.Key)" }
    if (@($checks.Values | Where-Object { -not $_ }).Count) { throw 'D09 negative self-tests failed.' }
    Write-Output "D09 self-tests passed: $($checks.Count)/$($checks.Count)."; exit 0
}

$checks = [ordered]@{}
$checks['D09 scope and unchanged gates'] = Test-Scope $scope
$packages = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'Directory.Packages.props') -Raw
foreach ($pin in @('Npgsql" Version="10.0.3','Dapper" Version="2.1.89','dbup-postgresql" Version="7.0.1','AWSSDK.S3" Version="4.0.103.4')) {
    $checks["Exact package pin $pin"] = $packages.Contains($pin)
}
$compose = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/d09/compose.yml') -Raw
$checks['Pinned PostgreSQL 18.6 digest'] = $compose.Contains('postgres:18.6@sha256:5a5a84b19854a9ffaa54082c166ff4ec27473a361e496e5ea167f298f2da9722') -and -not $compose.Contains('postgres:latest')
$checks['Pinned SeaweedFS 4.47 LOCAL CI fixture digest'] = $compose.Contains('seaweedfs:4.47@sha256:ce9e796f1fe6f06968f4c04bdaf8f678dad9c8acdfef3d244133d71bfa6bf882') -and -not $compose.Contains(':latest')
$cohort = @('evidence','financial-profile','financial-rules','reporting','audit')
$currentCohort = if ($D10ForwardRegression) { @($cohort + 'job-management') } else { $cohort }
foreach ($service in $currentCohort) {
    $migrationRoot = Join-Path $RepositoryRoot "services/$service/migrations"
    $checks["$service owns migrations"] = (Test-Path -LiteralPath $migrationRoot) -and @(Get-ChildItem -LiteralPath $migrationRoot -File -Filter '*.sql').Count -ge 1
}
$allSql = @(Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'services') -Recurse -File -Filter '*.sql')
$checks['No shared or out-of-cohort business migrations'] = @($allSql | Where-Object {
    $relative = $_.FullName.Substring($RepositoryRoot.Length).Replace('\','/')
    -not ($currentCohort | Where-Object { $relative.StartsWith("/services/$_/migrations/", [StringComparison]::Ordinal) })
}).Count -eq 0
$allServiceSource = @(Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'services') -Recurse -File -Filter '*.cs' |
    Where-Object FullName -NotMatch '[\\/](bin|obj)[\\/]')
$serviceSourceText = @($allServiceSource | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
$checks['Applications use runtime credentials never migration-owner credentials'] =
    ([regex]::Matches($serviceSourceText, 'RuntimeConnection')).Count -eq $(if ($D10ForwardRegression) { 6 } else { 5 }) -and
    -not $serviceSourceText.Contains('OwnerConnection')
foreach ($service in $currentCohort) {
    $schema = $service.Replace('-', '_')
    $otherSchemas = @($currentCohort | Where-Object { $_ -cne $service } | ForEach-Object { $_.Replace('-', '_') })
    $ownedFiles = @(
        Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot "services/$service") -Recurse -File |
            Where-Object { $_.Extension -in @('.cs', '.sql') -and $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
    )
    $ownedText = @($ownedFiles | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
    $checks["$service SQL remains inside $schema authority"] =
        @($otherSchemas | Where-Object { $ownedText -match "(?<![A-Za-z0-9_])$([regex]::Escape($_))\." }).Count -eq 0
}
$serviceProjects = @(Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'services') -Recurse -File -Filter '*.csproj')
$projectText = @($serviceProjects | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
$checks['EF Core remains absent'] = $projectText -notmatch 'EntityFrameworkCore'
$awsReferences = @(
    Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'services') -Recurse -File |
        Where-Object { $_.Extension -in @('.cs', '.csproj') } |
        Select-String -Pattern 'AWSSDK\.S3|AmazonS3Client' |
        Select-Object -ExpandProperty Path -Unique
)
$checks['AWS SDK isolated to Evidence infrastructure/project'] = @($awsReferences | Where-Object { $_ -notmatch 'services[\\/]evidence[\\/](Infrastructure[\\/]|Monergy.Services.Evidence.csproj)' }).Count -eq 0
$checks['S3 bytes excluded from PostgreSQL'] = @($allSql | Select-String -Pattern 'bytea|large object|content_bytes').Count -eq 0
$evidenceS3 = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/evidence/Infrastructure/PostgresEvidenceAdapters.cs') -Raw
$checks['Opaque keys and Monergy SHA-256 independent of ETag'] = $evidenceS3.Contains('SHA256.HashData') -and $evidenceS3.Contains('content-') -and -not $evidenceS3.Contains('ETag') -and -not $evidenceS3.Contains('DeleteObject')
$expectedOutboxProducers = if ($D10ForwardRegression) { @('evidence','financial-profile','financial-rules','reporting','job-management') } else { @('evidence','financial-profile','financial-rules','reporting') }
$checks['Transactional outboxes exist for exact producers'] = Test-ExactSet @($allSql | Where-Object { $_.Name -ceq '0002_outbox.sql' -or ($D10ForwardRegression -and $_.Name -ceq '0001_durable_job_authority.sql') } | Where-Object { (Get-Content -LiteralPath $_.FullName -Raw).Contains('CREATE TABLE') -and (Get-Content -LiteralPath $_.FullName -Raw).Contains('outbox') } | ForEach-Object { Split-Path -Leaf (Split-Path -Parent (Split-Path -Parent $_.FullName)) }) $expectedOutboxProducers
$bootstrap = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/d09/bootstrap-postgres.sh') -Raw
foreach ($service in @('evidence','financial_profile','financial_rules','reporting','audit')) {
    $checks["Distinct $service owner/runtime roles"] = $bootstrap.Contains("monergy_`${service}_owner") -and $bootstrap.Contains("monergy_`${service}_runtime")
}
$checks['No broker cloud search or AI provider selected'] = $projectText -notmatch 'Kafka|RabbitMQ|ServiceBus|OpenAI|Pinecone|OpenSearch|Kubernetes'
$checks['Physical adapters fail closed outside LOCAL CI'] = (Get-Content -LiteralPath (Join-Path $RepositoryRoot 'shared/platform/Monergy.Platform/PhysicalPersistenceGuard.cs') -Raw).Contains('D09 physical persistence may run only in LOCAL or CI_EPHEMERAL')
$checks['Permanent physical integration tests exist'] = Test-Path -LiteralPath (Join-Path $RepositoryRoot 'tests/persistence/Monergy.Persistence.Tests/PersistentFoundationTests.cs')
foreach ($entry in $checks.GetEnumerator()) { Write-Output "[$(if($entry.Value){'PASS'}else{'FAIL'})] $($entry.Key)" }
$failed = @($checks.Values | Where-Object { -not $_ })
if ($failed.Count) { throw "D09 verification failed: $($checks.Count-$failed.Count)/$($checks.Count)." }
Write-Output "D09 verification passed: $($checks.Count)/$($checks.Count); candidate only."
