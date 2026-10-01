[CmdletBinding()]
param([string]$RepositoryRoot)

$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if([string]::IsNullOrWhiteSpace($RepositoryRoot)){$RepositoryRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)}
$controller=Join-Path $RepositoryRoot 'build/local/Invoke-MonergyLocal.ps1'
$probe=Join-Path $RepositoryRoot 'build/local/.d11-stale-source-probe'
Import-Module (Join-Path $RepositoryRoot 'build/local/Monergy.LocalProcess.psm1') -Force

$identityScratch=Join-Path ([IO.Path]::GetTempPath()) "monergy-d11-clean-identity-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $identityScratch | Out-Null
try {
    $git=(Get-Command git -ErrorAction Stop).Source
    & $git -C $identityScratch init --quiet
    if($LASTEXITCODE -ne 0){throw 'Unable to initialize the clean-identity Git repository.'}
    & $git -C $identityScratch config user.name 'Monergy D11 Test'
    & $git -C $identityScratch config user.email 'd11-test@invalid.local'
    & $git -C $identityScratch config commit.gpgSign false
    [IO.File]::WriteAllText((Join-Path $identityScratch 'tracked.txt'),'clean',[Text.UTF8Encoding]::new($false))
    & $git -C $identityScratch add -- tracked.txt
    & $git -C $identityScratch commit --quiet -m 'clean identity fixture'
    if($LASTEXITCODE -ne 0){throw 'Unable to commit the clean-identity fixture.'}

    $clean=Get-CanonicalDirtySourceIdentity -RepositoryRoot $identityScratch
    if([string]::IsNullOrWhiteSpace([string]$clean.head) -or $clean.dirtyFileCount -ne 0 -or
        $clean.dirtyDigest -cne 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855' -or
        @($clean.files).Count -ne 0){
        throw 'A clean Git repository did not produce the canonical zero-file identity.'
    }
    [IO.File]::WriteAllText((Join-Path $identityScratch 'tracked.txt'),'dirty',[Text.UTF8Encoding]::new($false))
    $dirty=Get-CanonicalDirtySourceIdentity -RepositoryRoot $identityScratch
    if($dirty.head -cne $clean.head -or $dirty.dirtyFileCount -ne 1 -or
        $dirty.dirtyDigest -ceq $clean.dirtyDigest -or @($dirty.files).Count -ne 1){
        throw 'Dirty source was not distinguished from the canonical clean identity.'
    }
}
finally {
    $resolvedScratch=[IO.Path]::GetFullPath($identityScratch)
    $temporaryRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if(-not $resolvedScratch.StartsWith($temporaryRoot,[StringComparison]::OrdinalIgnoreCase)){
        throw 'Clean-identity test cleanup escaped the operating-system temporary directory.'
    }
    if(Test-Path -LiteralPath $resolvedScratch){Remove-Item -LiteralPath $resolvedScratch -Recurse -Force}
}

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
    Write-Output 'D11 prepared-identity tests passed: clean zero-file identity is canonical and stale dirty source was rejected before runtime start.'
}
finally {
    $resolved=[IO.Path]::GetFullPath($probe)
    $allowed=[IO.Path]::GetFullPath((Join-Path $RepositoryRoot 'build/local'))
    if(-not $resolved.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'Probe cleanup escaped build/local.'}
    if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Force}
}
