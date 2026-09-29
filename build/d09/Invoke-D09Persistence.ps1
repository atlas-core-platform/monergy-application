[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [switch]$Reset
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Net.Http

function Test-HttpEndpoint([string]$Uri) {
    $client = [System.Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromSeconds(2)
    try {
        $response = $client.GetAsync($Uri).GetAwaiter().GetResult()
        [void]$response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
        $response.Dispose()
        return $true
    }
    catch { return $false }
    finally { $client.Dispose() }
}

$compose = Join-Path $RepositoryRoot 'build/d09/compose.yml'
if ($Reset) {
    foreach ($container in @('monergy-d09-postgres-1', 'monergy-d09-seaweedfs-1')) {
        $id = docker ps --all --quiet --filter "name=^/$container$"
        if ($id) { docker rm --force $container | Out-Null }
    }
    foreach ($volume in @('monergy-d09_d09-postgres', 'monergy-d09_d09-seaweed')) {
        if (docker volume ls --quiet --filter "name=^$volume$") { docker volume rm --force $volume | Out-Null }
    }
    $retainedReference = Join-Path $RepositoryRoot '.artifacts/d09/retained-s3-reference.json'
    if (Test-Path -LiteralPath $retainedReference) { Remove-Item -LiteralPath $retainedReference -Force }
    foreach ($generatedState in @('runtime-state.json', 'seaweed-s3.json')) {
        $generatedPath = Join-Path $RepositoryRoot ".artifacts/d09/$generatedState"
        if (Test-Path -LiteralPath $generatedPath) { Remove-Item -LiteralPath $generatedPath -Force }
    }
}
& (Join-Path $RepositoryRoot 'build/d09/Start-D09Infrastructure.ps1') -RepositoryRoot $RepositoryRoot
$statePath = Join-Path $RepositoryRoot '.artifacts/d09/runtime-state.json'
$state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json

foreach ($name in @('evidence', 'financial_profile', 'financial_rules', 'reporting', 'audit')) {
    $servicePath = $name.Replace('_', '-')
    $entry = $state.services.$name
    $owner = "Host=127.0.0.1;Port=55432;Database=$($entry.database);Username=$($entry.owner);Password=$($entry.ownerPassword);SSL Mode=Disable"
    $runtime = "Host=127.0.0.1;Port=55432;Database=$($entry.database);Username=$($entry.runtime);Password=$($entry.runtimePassword);SSL Mode=Disable"
    Set-Item -Path "Env:D09_$($name.ToUpperInvariant())_OWNER_CONNECTION" -Value $owner
    Set-Item -Path "Env:D09_$($name.ToUpperInvariant())_RUNTIME_CONNECTION" -Value $runtime
    foreach ($pass in 1..2) {
        dotnet run --project (Join-Path $RepositoryRoot 'build/Monergy.DatabaseMigrator/Monergy.DatabaseMigrator.csproj') -- --service $servicePath --connection $owner --repository-root $RepositoryRoot
        if ($LASTEXITCODE -ne 0) { throw "Migration pass $pass failed for $servicePath." }
    }
}

$env:D09_S3_ENDPOINT = 'http://127.0.0.1:58333'
$env:D09_S3_ACCESS_KEY = $state.s3AccessKey
$env:D09_S3_SECRET_KEY = $state.s3SecretKey
$env:D09_EVIDENCE_REFERENCE_FILE = Join-Path $RepositoryRoot '.artifacts/d09/retained-s3-reference.json'
$tests = Join-Path $RepositoryRoot 'tests/persistence/Monergy.Persistence.Tests/Monergy.Persistence.Tests.csproj'
dotnet test $tests --logger 'console;verbosity=normal'
if ($LASTEXITCODE -ne 0) { throw 'D09 physical persistence integration tests failed.' }

$before = docker exec monergy-d09-postgres-1 psql -U $state.adminUser -d monergy_evidence -Atc 'SELECT count(*) FROM evidence.document_versions;'
docker restart monergy-d09-postgres-1 monergy-d09-seaweedfs-1 | Out-Null
$ready = $false
foreach ($attempt in 1..60) {
    $health = docker inspect --format '{{.State.Health.Status}}' monergy-d09-postgres-1
    $seaweedRunning = docker inspect --format '{{.State.Running}}' monergy-d09-seaweedfs-1
    $s3Ready = Test-HttpEndpoint -Uri 'http://127.0.0.1:58333/'
    if ($health -eq 'healthy' -and $seaweedRunning -eq 'true' -and $s3Ready) { $ready = $true; break }
    Start-Sleep -Seconds 1
}
if (-not $ready) { throw 'D09 infrastructure did not recover after retained-volume restart.' }
Start-Sleep -Seconds 5
dotnet test $tests --filter 'FullyQualifiedName~EvidenceMetadataOutboxAndS3BytesSurviveAdapterReconstruction'
if ($LASTEXITCODE -ne 0) { throw 'D09 retained-volume restart verification failed.' }
$after = docker exec monergy-d09-postgres-1 psql -U $state.adminUser -d monergy_evidence -Atc 'SELECT count(*) FROM evidence.document_versions;'
if ([int]$after -lt [int]$before) { throw 'Evidence metadata was lost across PostgreSQL restart.' }
Write-Output "D09 persistence verification passed; Evidence rows before=$before after=$after."
