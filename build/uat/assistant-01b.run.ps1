#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$FixtureFile,
    [Parameter(Mandatory)][ValidateSet('activate','setup','baseline','reviewer','rates','qa-setup','qa-continuity','runtime-outage','feedback-outage','am-outage','audit-outage','owner-faults','unknown-outcome','workspace-switch','restart','disabled','reenabled','auth-capture','auth-expiry','auth-live-expiry','credential-recovery')][string]$Phase,
    [Parameter(Mandatory)][switch]$ConfirmGatesABC,
    [ValidateSet('initial','retry')][string]$ExpiryRun='initial',
    [ValidateSet('A600','A800','ci-60000000000000000000000000000001','ci-60000000000000000000000000000002','ci-60000000000000000000000000000003')][string]$RecoveryActor='A800',
    [switch]$Visible
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if (-not $ConfirmGatesABC) { throw 'The exact candidate must pass Gates A, B and C before physical execution.' }
if ($ExpiryRun -eq 'retry' -and $Phase -notin @('auth-capture','auth-expiry','auth-live-expiry')) { throw 'Expiry retry is valid only for a natural-expiry phase.' }
if ($RecoveryActor -ne 'A800' -and $Phase -ne 'credential-recovery') { throw 'Alternate recovery actor is valid only for the recovery phase.' }
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$privateRoot=[IO.Path]::GetFullPath((Join-Path $repository '.artifacts/assistant-01b-private-fixtures'))+[IO.Path]::DirectorySeparatorChar
$fixture=[IO.Path]::GetFullPath($FixtureFile)
if (-not $fixture.StartsWith($privateRoot,[StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture must stay inside the ignored protected private fixture root.' }
$entry=Get-Item -LiteralPath $fixture -Force
if ($entry.PSIsContainer -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'An ordinary protected fixture file is required.' }
if ($IsWindows) {
    $allowed=@([Security.Principal.WindowsIdentity]::GetCurrent().User.Value,'S-1-5-18')
    $acl=Get-Acl -LiteralPath $fixture
    foreach ($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Value -notin $allowed) { throw 'Fixture is readable outside its protected operator/System boundary.' }
    }
} else {
    $mode=[IO.File]::GetUnixFileMode($fixture)
    if (($mode -band ([IO.UnixFileMode]::GroupRead -bor [IO.UnixFileMode]::GroupWrite -bor [IO.UnixFileMode]::GroupExecute -bor [IO.UnixFileMode]::OtherRead -bor [IO.UnixFileMode]::OtherWrite -bor [IO.UnixFileMode]::OtherExecute)) -ne 0) { throw 'Fixture must not have group or world permissions.' }
}
$rawOutput=& (Join-Path $PSScriptRoot 'assistant-01b.private-artifacts.ps1') -Mode Prepare -FixtureFile $fixture
$old=@{}
foreach ($name in @('MONERGY_ASSISTANT_GATE_D_AUTHORIZED','MONERGY_ASSISTANT_UAT_FIXTURE','MONERGY_ASSISTANT_PHASE','MONERGY_ASSISTANT_VISIBLE','MONERGY_ASSISTANT_RAW_ARTIFACTS','MONERGY_ASSISTANT_LOCAL_DNS','MONERGY_ASSISTANT_EXPIRY_RUN','MONERGY_ASSISTANT_RECOVERY_ACTOR','NODE_OPTIONS')) { $old[$name]=[Environment]::GetEnvironmentVariable($name,'Process') }
Push-Location $repository
try {
    $env:MONERGY_ASSISTANT_GATE_D_AUTHORIZED='true'
    $env:MONERGY_ASSISTANT_UAT_FIXTURE=$fixture
    $env:MONERGY_ASSISTANT_PHASE=$Phase
    $env:MONERGY_ASSISTANT_VISIBLE=if($Visible){'true'}else{'false'}
    $env:MONERGY_ASSISTANT_EXPIRY_RUN=$ExpiryRun
    $env:MONERGY_ASSISTANT_RECOVERY_ACTOR=$RecoveryActor
    $env:MONERGY_ASSISTANT_RAW_ARTIFACTS=$rawOutput
    $env:MONERGY_ASSISTANT_LOCAL_DNS='true'
    $preload=([Uri](Join-Path $PSScriptRoot 'assistant-01b.local-dns.mjs')).AbsoluteUri
    # Preserve existing Node options; append a process-local test resolver only.
    # The file URI encodes spaces and never contains fixture content or secrets.
    $env:NODE_OPTIONS=(@($old['NODE_OPTIONS'],"--import=$preload") | Where-Object { $_ }) -join ' '
    & pnpm exec playwright test --config build/uat/assistant-01b.playwright.config.ts
    if ($LASTEXITCODE -ne 0) { throw 'Physical Assistant phase failed. See sanitized scenario outcomes; no acceptance is claimed.' }
} finally {
    foreach ($name in $old.Keys) { [Environment]::SetEnvironmentVariable($name,$old[$name],'Process') }
    Pop-Location
}
