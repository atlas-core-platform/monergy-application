[CmdletBinding()]
param([string]$RepositoryRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot) }
Import-Module (Join-Path $RepositoryRoot 'build/local/Monergy.LocalProcess.psm1') -Force

$scratch = Join-Path ([IO.Path]::GetTempPath()) "monergy d11 process $([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $scratch | Out-Null
$probe = Join-Path $scratch 'probe.ps1'
$output = Join-Path $scratch 'result.json'
$sentinel = 'D11_PARENT_SENTINEL_SECRET'
Set-Item -Path "Env:$sentinel" -Value 'must-not-leak'
try {
    [IO.File]::WriteAllText($probe, @'
param([string]$Output)
$names = @([Environment]::GetEnvironmentVariables().Keys | ForEach-Object { [string]$_ } | Sort-Object)
$result = [ordered]@{
  names = $names
  ownRuntime = [bool]$env:Monergy__Persistence__Reporting__RuntimeConnection
  foreignRuntime = [bool]$env:Monergy__Persistence__Audit__RuntimeConnection
  migration = [bool]$env:MONERGY_MIGRATION_CONNECTION
  parentSentinel = [bool]$env:D11_PARENT_SENTINEL_SECRET
  privilegedOrSetup = @($names | Where-Object { $_ -match 'ADMIN|OWNER|MIGRAT|LocalSetup|S3__|LocalTransport' }).Count
  databaseSecrets = @($names | Where-Object { $_ -match 'Persistence__.+__RuntimeConnection' }).Count
}
[IO.File]::WriteAllText($Output, ($result | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
'@, [Text.UTF8Encoding]::new($false))
    $shell = (Get-Process -Id $PID).Path
    $runtime = @{ 'Monergy__Persistence__Reporting__RuntimeConnection' = 'synthetic-runtime-value' }
    $arguments = @('-NoProfile','-NonInteractive','-File',$probe,'-Output',$output)
    $result = Invoke-ControlledProcess $shell $arguments $RepositoryRoot $runtime
    if ($result.ExitCode -ne 0) { throw 'The controlled environment probe failed.' }
    $observed = Get-Content -LiteralPath $output -Raw | ConvertFrom-Json
    if (-not $observed.ownRuntime -or $observed.foreignRuntime -or $observed.migration -or $observed.parentSentinel) {
        throw 'Runtime child environment isolation failed.'
    }
    if ($observed.databaseSecrets -ne 1 -or $observed.privilegedOrSetup -ne 0) { throw 'Runtime child received a privileged or foreign secret.' }
    $renderedArguments = (New-ControlledProcessStartInfo $shell $arguments $RepositoryRoot $runtime).Arguments
    if ($renderedArguments.Contains('synthetic-runtime-value')) { throw 'A runtime credential appeared in child arguments.' }

    $bootstrap = Join-Path $RepositoryRoot 'build/local/bootstrap-persisted-reporting.sh'
    $bootstrapProbe = Join-Path $scratch 'bootstrap-probe.sh'
    $bootstrapProbeOutput = Join-Path $scratch 'bootstrap-probe-output'
    New-Item -ItemType Directory -Path $bootstrapProbeOutput | Out-Null
    [IO.File]::WriteAllText($bootstrapProbe, @'
#!/usr/bin/env bash
set -euo pipefail
probe_dir="${D11_PSQL_PROBE_DIR:?}"
count=0
psql() {
  count=$((count + 1))
  printf '%s\0' "$@" > "$probe_dir/argv-$count.bin"
  cat > "$probe_dir/stdin-$count.sql"
  printf 'owner=%s\nruntime=%s\n' \
    "${D11_BOOTSTRAP_OWNER_PASSWORD-}" \
    "${D11_BOOTSTRAP_RUNTIME_PASSWORD-}" > "$probe_dir/environment-$count.txt"
}
source "${D11_BOOTSTRAP_SCRIPT:?}"
'@, [Text.UTF8Encoding]::new($false))
    $bootstrapSecrets = @{
        'POSTGRES_USER' = 'd11_admin'
        'D11_PSQL_PROBE_DIR' = $bootstrapProbeOutput.Replace('\','/')
        'D11_BOOTSTRAP_SCRIPT' = $bootstrap.Replace('\','/')
    }
    $serviceNames = @('EVIDENCE','FINANCIAL_PROFILE','FINANCIAL_RULES','REPORTING','AUDIT')
    $passwordSentinels = @()
    $ownerPasswordSentinels = @()
    $runtimePasswordSentinels = @()
    foreach ($serviceName in $serviceNames) {
        $ownerSentinel = "owner-secret-$($serviceName.ToLowerInvariant())"
        $runtimeSentinel = "runtime-secret-$($serviceName.ToLowerInvariant())"
        $bootstrapSecrets["MONERGY_${serviceName}_OWNER_PASSWORD"] = $ownerSentinel
        $bootstrapSecrets["MONERGY_${serviceName}_RUNTIME_PASSWORD"] = $runtimeSentinel
        $passwordSentinels += $ownerSentinel, $runtimeSentinel
        $ownerPasswordSentinels += $ownerSentinel
        $runtimePasswordSentinels += $runtimeSentinel
    }
    $bash = if ($env:OS -eq 'Windows_NT') {
        $gitBash = Join-Path $env:ProgramFiles 'Git/bin/bash.exe'
        if (-not (Test-Path -LiteralPath $gitBash -PathType Leaf)) { throw 'Git Bash is required for the D11 bootstrap argv test.' }
        $gitBash
    }
    else { (Get-Command bash -ErrorAction Stop).Source }
    $bootstrapResult = Invoke-ControlledProcess $bash @($bootstrapProbe.Replace('\','/')) `
        $RepositoryRoot $bootstrapSecrets
    if ($bootstrapResult.ExitCode -ne 0) { throw 'The D11 bootstrap argv probe failed.' }
    $argvFiles = @(Get-ChildItem -LiteralPath $bootstrapProbeOutput -Filter 'argv-*.bin')
    $stdinFiles = @(Get-ChildItem -LiteralPath $bootstrapProbeOutput -Filter 'stdin-*.sql')
    if ($argvFiles.Count -ne 15 -or $stdinFiles.Count -ne 15) { throw 'The D11 bootstrap argv probe did not observe all expected psql calls.' }
    $capturedArguments = ($argvFiles | ForEach-Object { [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($_.FullName)) }) -join "`n"
    foreach ($password in $passwordSentinels) {
        if ($capturedArguments.Contains($password)) { throw 'A D11 database password appeared in psql arguments.' }
    }
    if ($capturedArguments -match '--set=(owner|runtime)_password') {
        throw 'A D11 database password variable remains on the psql command line.'
    }
    $roleInputs = @($stdinFiles | Where-Object { (Get-Content -Raw -LiteralPath $_.FullName).Contains('CREATE ROLE') })
    if ($roleInputs.Count -ne 5) { throw 'The D11 bootstrap argv probe did not observe five role-bootstrap inputs.' }
    foreach ($input in $roleInputs) {
        $sql = Get-Content -Raw -LiteralPath $input.FullName
        if (-not $sql.Contains('\getenv owner_password D11_BOOTSTRAP_OWNER_PASSWORD') -or
            -not $sql.Contains('\getenv runtime_password D11_BOOTSTRAP_RUNTIME_PASSWORD')) {
            throw 'The D11 bootstrap does not import role passwords from its controlled environment.'
        }
        $callNumber = [IO.Path]::GetFileNameWithoutExtension($input.Name).Split('-')[-1]
        $capturedEnvironment = Get-Content -LiteralPath (Join-Path $bootstrapProbeOutput "environment-$callNumber.txt")
        $ownerValue = ($capturedEnvironment | Where-Object { $_.StartsWith('owner=', [StringComparison]::Ordinal) }).Substring(6)
        $runtimeValue = ($capturedEnvironment | Where-Object { $_.StartsWith('runtime=', [StringComparison]::Ordinal) }).Substring(8)
        if ($ownerValue -cnotin $ownerPasswordSentinels -or $runtimeValue -cnotin $runtimePasswordSentinels) {
            throw 'The D11 bootstrap did not supply role passwords through the controlled psql environment.'
        }
    }

    $web = Invoke-ControlledProcess $shell $arguments $RepositoryRoot @{ 'VITE_MONERGY_REPORTING_MODE'='PERSISTED_REPORTING' }
    $webObserved = Get-Content -LiteralPath $output -Raw | ConvertFrom-Json
    if ($web.ExitCode -ne 0 -or $webObserved.databaseSecrets -ne 0 -or $webObserved.privilegedOrSetup -ne 0) {
        throw 'Customer Web received a database, storage, Audit transport, setup or migration secret.'
    }
    $migration = Invoke-ControlledProcess $shell $arguments $RepositoryRoot @{ 'MONERGY_MIGRATION_CONNECTION'='synthetic-owner-value' }
    $migrationObserved = Get-Content -LiteralPath $output -Raw | ConvertFrom-Json
    if ($migration.ExitCode -ne 0 -or -not $migrationObserved.migration -or $migrationObserved.databaseSecrets -ne 0 -or $migrationObserved.parentSentinel) {
        throw 'Migration child did not receive exactly its controlled migration secret boundary.'
    }

    $sleeper = Start-ControlledProcess $shell @('-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 30') $scratch @{}
    try {
        $entry = [pscustomobject]@{pid=$sleeper.Id;startTimeTicks=$sleeper.StartTime.ToUniversalTime().Ticks;executable=$shell}
        if ((Get-ControlledProcessOwnership $entry) -ne 'VERIFIED_RUNNING') { throw 'Exact process ownership was not recognized.' }
        $stale = [pscustomobject]@{pid=$entry.pid;startTimeTicks=$entry.startTimeTicks+1;executable=$entry.executable}
        if ((Get-ControlledProcessOwnership $stale) -ne 'IDENTITY_MISMATCH') { throw 'Stale start identity was not rejected.' }
        $unrelated = [pscustomobject]@{pid=$entry.pid;startTimeTicks=$entry.startTimeTicks;executable=(Join-Path $scratch 'unrelated.exe')}
        if ((Get-ControlledProcessOwnership $unrelated) -ne 'IDENTITY_MISMATCH') { throw 'Unrelated executable identity was not rejected.' }
    }
    finally {
        if (-not $sleeper.HasExited) { $sleeper.Kill(); $sleeper.WaitForExit() }
        $sleeper.Dispose()
    }

    $failure = Invoke-ControlledProcess $shell @('-NoProfile','-NonInteractive','-Command','exit 17') `
        $RepositoryRoot @{ 'D11_CHILD_ONLY' = 'synthetic' } -AllowFailure
    $parentSentinel = (Get-Item -Path "Env:$sentinel").Value
    if ($failure.ExitCode -ne 17 -or $env:D11_CHILD_ONLY -or $parentSentinel -ne 'must-not-leak') {
        throw 'Controlled failure-path environment isolation failed.'
    }
    Write-Output 'D11 process isolation tests passed: runtime ownership, stale/mismatched PID rejection, path spaces, foreign-secret exclusion, runtime/bootstrap argv exclusion, parent noncontamination, and failure cleanup.'
}
finally {
    Remove-Item -Path "Env:$sentinel" -ErrorAction SilentlyContinue
    $resolvedScratch = [IO.Path]::GetFullPath($scratch)
    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $resolvedScratch.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The process-isolation scratch directory escaped the operating-system temporary directory.'
    }
    if (Test-Path -LiteralPath $resolvedScratch) { Remove-Item -LiteralPath $resolvedScratch -Recurse -Force }
}
