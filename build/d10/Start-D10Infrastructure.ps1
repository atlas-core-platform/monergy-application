[CmdletBinding()]
param([string]$RepositoryRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function New-EphemeralSecret {
    $bytes = [byte[]]::new(32)
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($bytes) }
    finally { $generator.Dispose() }
    return ([BitConverter]::ToString($bytes)).Replace('-', '')
}

$statePath = Join-Path $RepositoryRoot '.artifacts/d10/runtime-state.json'
$retainedVolume = docker volume ls --quiet --filter 'name=^monergy-d10_d10-postgres$'
if ((Test-Path -LiteralPath $statePath) -and $retainedVolume) {
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    $env:D10_POSTGRES_ADMIN_USER = $state.adminUser
    $env:D10_POSTGRES_ADMIN_PASSWORD = $state.adminPassword
    foreach ($service in @('job_management', 'reporting', 'audit')) {
        $upper = $service.ToUpperInvariant()
        Set-Item -Path "Env:MONERGY_${upper}_OWNER_PASSWORD" -Value $state.services.$service.ownerPassword
        Set-Item -Path "Env:MONERGY_${upper}_RUNTIME_PASSWORD" -Value $state.services.$service.runtimePassword
    }
}
else {
    $env:D10_POSTGRES_ADMIN_USER = 'd10_admin'
    $env:D10_POSTGRES_ADMIN_PASSWORD = New-EphemeralSecret
    foreach ($service in @('JOB_MANAGEMENT', 'REPORTING', 'AUDIT')) {
        Set-Item -Path "Env:MONERGY_${service}_OWNER_PASSWORD" -Value (New-EphemeralSecret)
        Set-Item -Path "Env:MONERGY_${service}_RUNTIME_PASSWORD" -Value (New-EphemeralSecret)
    }
}

$compose = Join-Path $RepositoryRoot 'build/d10/compose.yml'
docker compose -f $compose up -d --wait
if ($LASTEXITCODE -ne 0) { throw 'D10 PostgreSQL infrastructure failed to become ready.' }

$state = [ordered]@{ endpoint = '127.0.0.1'; postgresPort = 55433 }
$state.adminUser = $env:D10_POSTGRES_ADMIN_USER
$state.adminPassword = $env:D10_POSTGRES_ADMIN_PASSWORD
$state.services = [ordered]@{}
foreach ($service in @('job_management', 'reporting', 'audit')) {
    $upper = $service.ToUpperInvariant()
    $state.services[$service] = [ordered]@{
        database = "monergy_$service"
        owner = "monergy_${service}_owner"
        ownerPassword = (Get-Item "Env:MONERGY_${upper}_OWNER_PASSWORD").Value
        runtime = "monergy_${service}_runtime"
        runtimePassword = (Get-Item "Env:MONERGY_${upper}_RUNTIME_PASSWORD").Value
    }
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $statePath) | Out-Null
$state | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $statePath -Encoding UTF8
Write-Output $statePath
