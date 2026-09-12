[CmdletBinding()]
param(
    [ValidateSet('Sbom', 'VulnerabilityScan', 'SecretScan')]
    [string]$Task,
    [string]$RepositoryRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$artifactRoot = Join-Path $RepositoryRoot '.artifacts/security'
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null

function Resolve-Tool {
    param([string]$Name)
    $local = Join-Path $RepositoryRoot ".toolcache/security/$Name.exe"
    if (Test-Path -LiteralPath $local) { return $local }
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    Write-Error "NOT_RUN: required pinned tool '$Name' is unavailable; this check is not PASS."
    exit 2
}

function Write-Status {
    param([string]$Name, [string]$Result, [string]$Evidence)
    [pscustomobject]@{
        schemaVersion = '1.0.0'
        check = $Name
        result = $Result
        evidence = $Evidence
        recordedAt = (Get-Date).ToUniversalTime().ToString('o')
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $artifactRoot "$Name-status.json") -Encoding utf8
}

switch ($Task) {
    'Sbom' {
        $syft = Resolve-Tool 'syft'
        $sbom = Join-Path $artifactRoot 'repository.cyclonedx.json'
        & $syft "dir:$RepositoryRoot" --exclude './.git/**' --exclude './.toolcache/**' --exclude './node_modules/**' --exclude './**/bin/**' --exclude './**/obj/**' --output "cyclonedx-json=$sbom"
        if ($LASTEXITCODE -ne 0) { Write-Status 'sbom' 'FAIL' 'Syft failed'; throw 'SBOM generation failed.' }
        Write-Status 'sbom' 'PASS' $sbom
        Write-Output "SBOM PASS: CycloneDX JSON generated with pinned Syft at $sbom."
    }
    'VulnerabilityScan' {
        $grype = Resolve-Tool 'grype'
        $sbom = Join-Path $artifactRoot 'repository.cyclonedx.json'
        if (-not (Test-Path -LiteralPath $sbom)) { throw 'Run Sbom before VulnerabilityScan.' }

        $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
        if (-not $dotnet) { Write-Error 'NOT_RUN: .NET runtime unavailable for native NuGet advisory scan.'; exit 2 }
        $dotnetResult = & $dotnet.Source package list --project (Join-Path $RepositoryRoot 'Monergy.Application.slnx') --vulnerable --include-transitive --format json --output-version 1 2>&1
        $dotnetResult | Set-Content -LiteralPath (Join-Path $artifactRoot 'dotnet-vulnerabilities.json') -Encoding utf8
        if ($LASTEXITCODE -ne 0) { Write-Status 'vulnerability' 'FAIL' 'NuGet advisory scan failed'; throw 'NuGet advisory scan failed.' }

        $pnpm = Get-Command pnpm -ErrorAction SilentlyContinue
        if (-not $pnpm) { Write-Error 'NOT_RUN: pinned pnpm unavailable for native advisory scan.'; exit 2 }
        $pnpmResult = & $pnpm.Source audit --json 2>&1
        $pnpmResult | Set-Content -LiteralPath (Join-Path $artifactRoot 'pnpm-audit.json') -Encoding utf8
        if ($LASTEXITCODE -ne 0) { Write-Status 'vulnerability' 'FAIL' 'pnpm audit reported findings or failed'; throw 'pnpm advisory scan failed.' }

        $grypeOutput = Join-Path $artifactRoot 'grype.json'
        & $grype "sbom:$sbom" --output json --file $grypeOutput --fail-on high
        if ($LASTEXITCODE -ne 0) { Write-Status 'vulnerability' 'FAIL' 'Grype reported high-or-greater findings or failed'; throw 'Grype vulnerability scan failed.' }
        Write-Status 'vulnerability' 'PASS' 'NuGet, pnpm, and Grype evidence recorded'
        Write-Output 'Vulnerability scan PASS: native NuGet/pnpm and pinned Grype checks completed.'
    }
    'SecretScan' {
        $gitleaks = Resolve-Tool 'gitleaks'
        $historyReport = Join-Path $artifactRoot 'gitleaks-history.json'
        $candidateReport = Join-Path $artifactRoot 'gitleaks-candidate.json'
        & $gitleaks git $RepositoryRoot --no-banner --redact --report-format json --report-path $historyReport --exit-code 1
        if ($LASTEXITCODE -ne 0) { Write-Status 'secret' 'FAIL' 'Gitleaks history scan reported findings or failed'; throw 'Gitleaks history scan failed.' }
        & $gitleaks dir $RepositoryRoot --no-banner --redact --report-format json --report-path $candidateReport --exit-code 1
        if ($LASTEXITCODE -ne 0) { Write-Status 'secret' 'FAIL' 'Gitleaks candidate scan reported findings or failed'; throw 'Gitleaks candidate scan failed.' }
        Write-Status 'secret' 'PASS' "$historyReport; $candidateReport"
        Write-Output 'Secret scan PASS: pinned Gitleaks inspected repository history and candidate content.'
    }
}
