[CmdletBinding()]
param(
    [ValidateSet('Restore', 'FormatCheck', 'Lint', 'StaticAnalysis', 'Test', 'CiConfig', 'SecretScan', 'DependencyScan', 'Build', 'All')]
    [string]$Task = 'All',
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-SourceFiles {
    return @(Get-ChildItem -LiteralPath $RepositoryRoot -Recurse -File | Where-Object {
        $_.FullName -notmatch '[\\/]\.git[\\/]' -and $_.FullName -notmatch '[\\/]\.artifacts[\\/]'
    })
}

function Invoke-DependencyScan {
    $names = @('package.json', 'package-lock.json', 'pnpm-lock.yaml', 'yarn.lock', 'requirements.txt', 'poetry.lock', 'pom.xml', 'build.gradle', 'Cargo.toml', 'go.mod')
    $found = @(Get-SourceFiles | Where-Object { $_.Name -in $names })
    if ($found.Count -gt 0) { throw 'Unapproved product dependency manifest detected before technology approval.' }
    $state = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/supply-chain/dependency-state.json') -Raw | ConvertFrom-Json
    if ($state.productDependencyCount -ne 0 -or $state.state -cne 'NO_PRODUCT_DEPENDENCIES_YET') { throw 'Dependency state is inconsistent.' }
    Write-Output 'Dependency/composition/license scan PASS: zero product dependencies; external advisory scan is not applicable yet.'
}

function Invoke-Restore {
    Invoke-DependencyScan
    Write-Output 'Restore PASS: the D01 bootstrap has zero external product dependencies and requires no package download.'
}

function Invoke-FormatCheck {
    $violations = New-Object System.Collections.Generic.List[string]
    foreach ($file in Get-SourceFiles) {
        $content = [IO.File]::ReadAllText($file.FullName)
        if ($content.Length -gt 0 -and $content -notmatch '(\r?\n)$') { $violations.Add("missing-final-newline:$($file.FullName)") }
        if ([regex]::IsMatch($content, '[ \t]+(?=\r?$)', [Text.RegularExpressions.RegexOptions]::Multiline)) { $violations.Add("trailing-whitespace:$($file.FullName)") }
    }
    if ($violations.Count -gt 0) { throw "Format check failed: $($violations -join '; ')" }
    Write-Output 'Format check PASS.'
}

function Invoke-Lint {
    $jsonFiles = @(Get-SourceFiles | Where-Object Extension -ceq '.json')
    foreach ($file in $jsonFiles) { $null = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json }
    Write-Output "Lint PASS: $($jsonFiles.Count) JSON files parsed."
}

function Invoke-StaticAnalysis {
    & (Join-Path $RepositoryRoot 'build/verify-bootstrap.ps1') -RepositoryRoot $RepositoryRoot
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Output 'Static analysis PASS.'
}

function Invoke-Test {
    & (Join-Path $RepositoryRoot 'tests/bootstrap/Bootstrap.Tests.ps1') -RepositoryRoot $RepositoryRoot
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Invoke-CiConfig {
    $gates = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/ci/gates.json') -Raw | ConvertFrom-Json
    $pipeline = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/ci/pipeline.json') -Raw | ConvertFrom-Json
    if (@($gates.gates).Count -ne 11 -or @($pipeline.stages).Count -ne 5) { throw 'CI contract cardinality is invalid.' }
    Write-Output 'CI configuration PASS: 11 gates across five vendor-neutral stages.'
}

function Invoke-SecretScan {
    $findings = New-Object System.Collections.Generic.List[string]
    $assignment = '(?im)(password|secret|token|api[_-]?key|private[_-]?key)\s*[:=]\s*["''][^"''$\s][^"'']+["'']'
    foreach ($file in Get-SourceFiles) {
        $content = [IO.File]::ReadAllText($file.FullName)
        if ($content -match $assignment -or $content -match '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----' -or $content -match '\bAKIA[0-9A-Z]{16}\b') {
            $findings.Add($file.FullName)
        }
    }
    if ($findings.Count -gt 0) { throw "Secret scan failed: $($findings -join ', ')" }
    Write-Output 'Secret scan PASS: no literal credential pattern or private-key material detected.'
}

function Get-TextDigest {
    param([string]$Text)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
        return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally { $sha.Dispose() }
}

function Invoke-Build {
    $artifactRoot = Join-Path $RepositoryRoot '.artifacts'
    $componentRoot = Join-Path $artifactRoot 'components'
    $sbomRoot = Join-Path $artifactRoot 'sbom'
    $provenanceRoot = Join-Path $artifactRoot 'provenance'
    foreach ($path in @($componentRoot, $sbomRoot, $provenanceRoot)) { $null = New-Item -ItemType Directory -Path $path -Force }

    $services = (Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/catalog.json') -Raw | ConvertFrom-Json).services
    $apps = (Get-Content -LiteralPath (Join-Path $RepositoryRoot 'apps/catalog.json') -Raw | ConvertFrom-Json).applications
    $components = New-Object System.Collections.Generic.List[object]
    foreach ($service in $services) {
        $components.Add([pscustomobject]@{ name = $service.artifact; kind = 'SERVICE'; source = "services/$($service.id)"; status = 'BOOTSTRAP_DESCRIPTOR_ONLY'; digest = (Get-TextDigest "SERVICE:$($service.id):RESERVED") })
    }
    foreach ($app in $apps) {
        $artifactName = 'monergy-' + $app.folder
        $components.Add([pscustomobject]@{ name = $artifactName; kind = 'APPLICATION'; source = "apps/$($app.folder)"; status = 'BOOTSTRAP_DESCRIPTOR_ONLY'; digest = (Get-TextDigest "APPLICATION:$($app.folder):RESERVED") })
    }
    foreach ($component in $components) {
        $component | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $componentRoot "$($component.name).json") -Encoding utf8
    }
    [pscustomobject]@{ schemaVersion = '1.0.0'; status = 'BOOTSTRAP_DEPENDENCY_INVENTORY'; dependencies = @() } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $sbomRoot 'dependency-inventory.json') -Encoding utf8
    $sourceRevision = (& git -C $RepositoryRoot rev-parse --verify --quiet HEAD)
    if ($LASTEXITCODE -ne 0) { $sourceRevision = 'UNCOMMITTED' }
    [pscustomobject]@{
        schemaVersion = '1.0.0'
        status = 'LOCAL_NON_DEPLOYABLE_EVIDENCE'
        sourceRevision = $sourceRevision
        componentCount = $components.Count
        architecturePublication = '193667fc7ad4d7f919f213f9a96260afa0f09fb9'
        productionArtifact = $false
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $provenanceRoot 'build.json') -Encoding utf8
    Write-Output "Build smoke PASS: $($components.Count) non-deployable bootstrap descriptors and zero-dependency inventory emitted."
}

$taskOrder = if ($Task -ceq 'All') {
    @('Restore', 'FormatCheck', 'Lint', 'StaticAnalysis', 'Test', 'CiConfig', 'SecretScan', 'DependencyScan', 'Build')
} else { @($Task) }

foreach ($current in $taskOrder) {
    switch ($current) {
        'Restore' { Invoke-Restore }
        'FormatCheck' { Invoke-FormatCheck }
        'Lint' { Invoke-Lint }
        'StaticAnalysis' { Invoke-StaticAnalysis }
        'Test' { Invoke-Test }
        'CiConfig' { Invoke-CiConfig }
        'SecretScan' { Invoke-SecretScan }
        'DependencyScan' { Invoke-DependencyScan }
        'Build' { Invoke-Build }
    }
}

Write-Output "Bootstrap task '$Task' completed."
