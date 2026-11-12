[CmdletBinding()]
param(
    [string]$RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')),
    [string]$Profile = 'p2n-admin-connectivity',
    [string]$HarnessRoot = 'E:\Projects\Atlas\monergy-application\build\demo'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$templateRoot = Join-Path $HarnessRoot 'cohort-l'
$templatePath = Join-Path $templateRoot 'Initialize-CohortLProfile.ps1'
$source = Get-Content -LiteralPath $templatePath -Raw -Encoding utf8
$replacements = [ordered]@{
    'p2-cohort-l-acceptance' = 'p2-cohort-n-acceptance'
    'p2l-goals' = 'p2n-admin-connectivity'
    '4277' = '4291'
    'Cohort L' = 'Cohort N'
}
foreach ($entry in $replacements.GetEnumerator()) {
    $source = $source.Replace([string]$entry.Key, [string]$entry.Value)
}
$templateRootLiteral = "'$($templateRoot.Replace("'", "''"))'"
$source = $source.Replace('$PSScriptRoot', $templateRootLiteral)
$bootstrap = [scriptblock]::Create($source)
& $bootstrap -RepositoryRoot $RepositoryRoot -Profile $Profile
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
