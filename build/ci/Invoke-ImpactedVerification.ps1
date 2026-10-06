[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ClassificationPath,
    [string]$RepositoryRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not (Test-Path -LiteralPath $ClassificationPath)) {
    throw "Impact classification '$ClassificationPath' does not exist."
}
$impact = Get-Content -LiteralPath $ClassificationPath -Raw -Encoding utf8 | ConvertFrom-Json
if ($impact.schemaVersion -cne '1.0.0' -or -not $impact.gates.universal) {
    throw 'Invalid impact classification document.'
}

$localDotnet = Join-Path $RepositoryRoot '.toolcache/dotnet/dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
$localPnpm = Join-Path $RepositoryRoot '.toolcache/node-v24.21.0-win-x64/node_modules/corepack/shims/pnpm.cmd'
$pnpm = if (Test-Path -LiteralPath $localPnpm) { $localPnpm } else { (Get-Command pnpm -ErrorAction Stop).Source }

foreach ($project in @($impact.testProjects)) {
    $projectPath = Join-Path $RepositoryRoot ([string]$project)
    if (-not (Test-Path -LiteralPath $projectPath)) { throw "Selected test project '$project' does not exist." }
    Write-Output "=== Impacted test: $project ==="
    & $dotnet test $projectPath --configuration Release --no-build --filter 'Category!=Physical' --logger 'console;verbosity=minimal'
    if ($LASTEXITCODE -ne 0) { throw "Impacted test project '$project' failed." }
}

if ($impact.gates.frontendTests) {
    Write-Output '=== Impacted frontend component tests ==='
    & $pnpm --dir $RepositoryRoot run test
    if ($LASTEXITCODE -ne 0) { throw 'Impacted frontend tests failed.' }
}

$taskByVerifier = [ordered]@{
    D01 = 'D01Verification'
    D02 = 'D02Verification'
    D03 = 'D03Verification'
    D04 = 'D04Verification'
    D05 = 'D05Verification'
    D06 = 'D06Verification'
    D07 = 'D07Verification'
    D08 = 'D08Verification'
    D09 = 'D09Verification'
    D10 = 'D10Verification'
    D11 = 'D11Verification'
    D12 = 'D12Verification'
    D13 = 'D13Verification'
    D14 = 'D14Verification'
    D16 = 'D16Verification'
}
$selected = @($impact.verifiers | ForEach-Object { [string]$_ })
foreach ($entry in $taskByVerifier.GetEnumerator()) {
    if ($selected -notcontains $entry.Key) { continue }
    Write-Output "=== Impacted governed verifier: $($entry.Key) ==="
    & (Join-Path $RepositoryRoot 'build/Invoke-Toolchain.ps1') -Task $entry.Value -RepositoryRoot $RepositoryRoot
    if ($LASTEXITCODE -ne 0) { throw "Impacted verifier '$($entry.Key)' failed." }
}

if ($selected -contains 'D15') {
    Write-Output 'D15 is executed by the universal policy gate and is not duplicated by the impacted runner.'
}
Write-Output "Impacted verification passed: $(@($impact.testProjects).Count) test projects, $(@($selected | Where-Object { $_ -cne 'D15' }).Count) governed verifiers."
