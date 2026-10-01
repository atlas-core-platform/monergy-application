[CmdletBinding()]
param([string]$RepositoryRoot)

$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if([string]::IsNullOrWhiteSpace($RepositoryRoot)){$RepositoryRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)}
$controller=Join-Path $RepositoryRoot 'build/local/Invoke-MonergyLocal.ps1'
$probe=Join-Path $RepositoryRoot 'build/local/.d11-stale-source-probe'
if(Test-Path -LiteralPath $probe){throw 'Prepared-identity probe path already exists.'}
try {
    [IO.File]::WriteAllText($probe,'bounded stale-source probe',[Text.UTF8Encoding]::new($false))
    $failed=$false
    try { & $controller -Action Start -Profile persisted-reporting -RepositoryRoot $RepositoryRoot | Out-Null }
    catch {
        $failed=$_.Exception.Message -match 'source/build identity is stale'
        if(-not $failed){throw}
    }
    if(-not $failed){throw 'Start did not reject a dirty-source identity created after Prepare.'}
    Write-Output 'D11 prepared-identity test passed: stale dirty source was rejected before runtime start.'
}
finally {
    $resolved=[IO.Path]::GetFullPath($probe)
    $allowed=[IO.Path]::GetFullPath((Join-Path $RepositoryRoot 'build/local'))
    if(-not $resolved.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'Probe cleanup escaped build/local.'}
    if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Force}
}
