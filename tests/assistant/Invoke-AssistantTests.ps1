#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExpertRoot,
    [Parameter(Mandatory)][string]$PostgresConnectionFile,
    [ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$RunName = 'assistant-final',
    [string]$Filter
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$dependency = Join-Path $repository 'build/uat/dependencies'
$manifest = Get-Content -LiteralPath (Join-Path $dependency 'access-management-source.json') -Raw | ConvertFrom-Json
if ($manifest.storageMode -ne 'verified-files' -or $manifest.commit -cnotmatch '^[a-f0-9]{40}$') { throw 'A governed immutable AM source manifest is required.' }
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $dependency $manifest.sourceFolder))
if ($sourceRoot -ne [IO.Path]::GetFullPath((Join-Path $dependency 'source'))) { throw 'Unexpected AM source boundary.' }
$buildRoot = Join-Path $repository ('.artifacts/assistant-01b-build/' + $manifest.commit)
$evidence = Join-Path $repository '.artifacts/assistant-01b-testing'
function Get-GitBlobDigest([byte[]]$Bytes) {
    $prefix = [Text.Encoding]::ASCII.GetBytes('blob ' + $Bytes.Length + [char]0)
    $payload = [byte[]]::new($prefix.Length + $Bytes.Length)
    [Array]::Copy($prefix, 0, $payload, 0, $prefix.Length)
    [Array]::Copy($Bytes, 0, $payload, $prefix.Length, $Bytes.Length)
    return [Convert]::ToHexString([Security.Cryptography.SHA1]::HashData($payload)).ToLowerInvariant()
}
$expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($file in $manifest.files) {
    if ([IO.Path]::IsPathRooted($file.path) -or $file.path.Split('/') -contains '..' -or -not $expected.Add($file.path)) { throw 'Invalid AM manifest path.' }
    $path = Join-Path $sourceRoot $file.path
    if (-not [IO.File]::Exists($path) -or (Get-GitBlobDigest ([IO.File]::ReadAllBytes($path))) -cne $file.gitBlobSha) { throw "AM immutable source verification failed: $($file.path)" }
}
$actual = @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File -Force | ForEach-Object { [IO.Path]::GetRelativePath($sourceRoot, $_.FullName).Replace('\','/') })
if ($actual.Count -ne $expected.Count -or @($actual | Where-Object { -not $expected.Contains($_) }).Count -ne 0) { throw 'Unexpected files in immutable AM source. Build output must remain outside the bundle.' }
foreach ($file in $manifest.files) {
    $target = Join-Path $buildRoot $file.path
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
    [IO.File]::Copy((Join-Path $sourceRoot $file.path), $target, $true)
    if ((Get-GitBlobDigest ([IO.File]::ReadAllBytes($target))) -cne $file.gitBlobSha) { throw 'Verified AM build-copy equality failed.' }
}
if (-not (Test-Path -LiteralPath $PostgresConnectionFile -PathType Leaf)) { throw 'Protected disposable PostgreSQL connection file is required.' }
if (-not (Test-Path -LiteralPath (Join-Path $ExpertRoot 'generated/assistant-runtime/validation-catalogue.json') -PathType Leaf)) { throw 'Exact Expert catalogue fixture is required.' }
[IO.Directory]::CreateDirectory($evidence) | Out-Null
Write-Output "Verified $($expected.Count)/$($expected.Count) AM source files at $($manifest.commit); compilation uses a separate disposable copy."
$priorExpert = $env:MONERGY_ASSISTANT_EXPERT_ROOT
$priorPostgres = $env:MONERGY_ASSISTANT_PG_FILE
try {
    $env:MONERGY_ASSISTANT_EXPERT_ROOT = [IO.Path]::GetFullPath($ExpertRoot)
    $env:MONERGY_ASSISTANT_PG_FILE = [IO.Path]::GetFullPath($PostgresConnectionFile)
    $arguments = @('test',(Join-Path $PSScriptRoot 'Monergy.Assistant.Tests/Monergy.Assistant.Tests.csproj'),'-c','Release','-m:1','-p:UseSharedCompilation=false',"-p:AssistantAmSourceRoot=$buildRoot",'--logger',"trx;LogFileName=$RunName.trx",'--results-directory',$evidence)
    if ($Filter) { $arguments += @('--filter',$Filter) }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Assistant tests failed with exit code $LASTEXITCODE." }
} finally {
    $env:MONERGY_ASSISTANT_EXPERT_ROOT = $priorExpert
    $env:MONERGY_ASSISTANT_PG_FILE = $priorPostgres
}
