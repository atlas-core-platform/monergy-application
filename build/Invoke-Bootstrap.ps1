[CmdletBinding()]
param(
    [ValidateSet('Restore', 'FormatCheck', 'Lint', 'StaticAnalysis', 'Test', 'CiConfig', 'SecretScan', 'DependencyScan', 'Build', 'All')]
    [string]$Task = 'All',
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$toolchain = Join-Path $RepositoryRoot 'build/Invoke-Toolchain.ps1'
$mapping = @{
    Restore = @('Restore')
    FormatCheck = @('FormatCheck')
    Lint = @('Lint')
    StaticAnalysis = @('D01Verification', 'D02Verification')
    Test = @('Test', 'ArchitectureTest')
    CiConfig = @('D02Verification')
    SecretScan = @('SecretScan')
    DependencyScan = @('Sbom', 'VulnerabilityScan')
    Build = @('Build')
    All = @('Restore', 'FormatCheck', 'Lint', 'Build', 'Test', 'ArchitectureTest', 'D01Verification', 'D02Verification')
}
foreach ($mappedTask in $mapping[$Task]) {
    & $toolchain -Task $mappedTask -RepositoryRoot $RepositoryRoot
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
Write-Output "D01-compatible bootstrap task '$Task' completed through the D02 toolchain."
