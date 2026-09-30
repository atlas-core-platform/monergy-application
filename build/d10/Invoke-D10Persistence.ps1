[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [switch]$Reset
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($Reset) {
    $container = docker ps --all --quiet --filter 'name=^/monergy-d10-postgres-1$'
    if ($container) { docker rm --force monergy-d10-postgres-1 | Out-Null }
    if (docker volume ls --quiet --filter 'name=^monergy-d10_d10-postgres$') {
        docker volume rm --force monergy-d10_d10-postgres | Out-Null
    }
    $statePath = Join-Path $RepositoryRoot '.artifacts/d10/runtime-state.json'
    if (Test-Path -LiteralPath $statePath) { Remove-Item -LiteralPath $statePath -Force }
}

& (Join-Path $RepositoryRoot 'build/d10/Start-D10Infrastructure.ps1') -RepositoryRoot $RepositoryRoot
$state = Get-Content -LiteralPath (Join-Path $RepositoryRoot '.artifacts/d10/runtime-state.json') -Raw | ConvertFrom-Json
foreach ($name in @('job_management', 'reporting', 'audit')) {
    $servicePath = $name.Replace('_', '-')
    $entry = $state.services.$name
    $owner = "Host=127.0.0.1;Port=55433;Database=$($entry.database);Username=$($entry.owner);Password=$($entry.ownerPassword);SSL Mode=Disable"
    $runtime = "Host=127.0.0.1;Port=55433;Database=$($entry.database);Username=$($entry.runtime);Password=$($entry.runtimePassword);SSL Mode=Disable"
    Set-Item -Path "Env:D10_$($name.ToUpperInvariant())_OWNER_CONNECTION" -Value $owner
    Set-Item -Path "Env:D10_$($name.ToUpperInvariant())_RUNTIME_CONNECTION" -Value $runtime
    foreach ($pass in 1..2) {
        dotnet run --project (Join-Path $RepositoryRoot 'build/Monergy.DatabaseMigrator/Monergy.DatabaseMigrator.csproj') -- --service $servicePath --connection $owner --repository-root $RepositoryRoot
        if ($LASTEXITCODE -ne 0) { throw "D10 migration pass $pass failed for $servicePath." }
    }
}

$env:Monergy__ExecutionZone = 'CI_EPHEMERAL'
$env:Monergy__Persistence__Provider = 'POSTGRESQL_S3'
$env:Monergy__Persistence__JobManagement__RuntimeConnection = $env:D10_JOB_MANAGEMENT_RUNTIME_CONNECTION
$env:Monergy__Persistence__Reporting__RuntimeConnection = $env:D10_REPORTING_RUNTIME_CONNECTION
$env:Monergy__Persistence__Audit__RuntimeConnection = $env:D10_AUDIT_RUNTIME_CONNECTION
$tests = Join-Path $RepositoryRoot 'tests/job-management/Monergy.JobManagement.Tests/Monergy.JobManagement.Tests.csproj'
dotnet test $tests --filter 'Category=Physical' --logger 'console;verbosity=normal'
if ($LASTEXITCODE -ne 0) { throw 'D10 physical persistence and propagation tests failed.' }

$before = docker exec monergy-d10-postgres-1 psql -U $state.adminUser -d monergy_job_management -Atc 'SELECT count(*) FROM job_management.jobs;'
docker restart monergy-d10-postgres-1 | Out-Null
$ready = $false
foreach ($attempt in 1..60) {
    if ((docker inspect --format '{{.State.Health.Status}}' monergy-d10-postgres-1) -eq 'healthy') { $ready = $true; break }
    Start-Sleep -Seconds 1
}
if (-not $ready) { throw 'D10 PostgreSQL did not recover after retained-volume restart.' }
dotnet test $tests --filter 'FullyQualifiedName~PersistedJobSurvivesRepositoryReconstructionAndDatabaseRestart'
if ($LASTEXITCODE -ne 0) { throw 'D10 retained-volume job restart verification failed.' }
$after = docker exec monergy-d10-postgres-1 psql -U $state.adminUser -d monergy_job_management -Atc 'SELECT count(*) FROM job_management.jobs;'
if ([int]$after -lt [int]$before) { throw 'Durable job state was lost across PostgreSQL restart.' }
Write-Output "D10 physical verification passed; jobs before=$before after=$after."
