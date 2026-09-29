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

$statePath = Join-Path $RepositoryRoot '.artifacts/d09/runtime-state.json'
$retainedVolume = docker volume ls --quiet --filter 'name=^monergy-d09_d09-postgres$'
if ((Test-Path -LiteralPath $statePath) -and $retainedVolume) {
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    $env:D09_POSTGRES_ADMIN_USER = $state.adminUser
    $env:D09_POSTGRES_ADMIN_PASSWORD = $state.adminPassword
    $s3AccessKey = $state.s3AccessKey
    $s3SecretKey = $state.s3SecretKey
    foreach ($service in @('evidence', 'financial_profile', 'financial_rules', 'reporting', 'audit')) {
        $upper = $service.ToUpperInvariant()
        Set-Item -Path "Env:MONERGY_${upper}_OWNER_PASSWORD" -Value $state.services.$service.ownerPassword
        Set-Item -Path "Env:MONERGY_${upper}_RUNTIME_PASSWORD" -Value $state.services.$service.runtimePassword
    }
}
else {
    $env:D09_POSTGRES_ADMIN_USER = 'd09_admin'
    $env:D09_POSTGRES_ADMIN_PASSWORD = New-EphemeralSecret
    foreach ($service in @('EVIDENCE', 'FINANCIAL_PROFILE', 'FINANCIAL_RULES', 'REPORTING', 'AUDIT')) {
        Set-Item -Path "Env:MONERGY_${service}_OWNER_PASSWORD" -Value (New-EphemeralSecret)
        Set-Item -Path "Env:MONERGY_${service}_RUNTIME_PASSWORD" -Value (New-EphemeralSecret)
    }
    $s3AccessKey = "d09$((New-EphemeralSecret).Substring(0, 20))"
    $s3SecretKey = New-EphemeralSecret
}
$s3ConfigPath = Join-Path $RepositoryRoot '.artifacts/d09/seaweed-s3.json'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $s3ConfigPath) | Out-Null
$s3Config = @{
    identities = @(@{
        name = 'd09-fixture'
        credentials = @(@{ accessKey = $s3AccessKey; secretKey = $s3SecretKey })
        actions = @('Admin', 'Read', 'Write')
    })
} | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText($s3ConfigPath, $s3Config, [Text.UTF8Encoding]::new($false))
$env:D09_S3_CONFIG_PATH = $s3ConfigPath.Replace('\', '/')

$compose = Join-Path $RepositoryRoot 'build/d09/compose.yml'
docker compose -f $compose up -d --wait
if ($LASTEXITCODE -ne 0) { throw 'D09 infrastructure failed to become ready.' }

$state = [ordered]@{ endpoint = '127.0.0.1'; postgresPort = 55432; s3Port = 58333 }
$state.adminUser = $env:D09_POSTGRES_ADMIN_USER
$state.adminPassword = $env:D09_POSTGRES_ADMIN_PASSWORD
$state.s3AccessKey = $s3AccessKey
$state.s3SecretKey = $s3SecretKey
$state.services = [ordered]@{}
foreach ($service in @('evidence', 'financial_profile', 'financial_rules', 'reporting', 'audit')) {
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
