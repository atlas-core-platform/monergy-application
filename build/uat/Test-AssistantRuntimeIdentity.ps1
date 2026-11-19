[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Assistant.Local.ps1')
$passed = 0
function Assert-Identity($actual, [string]$uid, [string]$gid) {
    if ($actual.uid -ne $uid -or $actual.gid -ne $gid) { throw 'Runtime secret reader identity differs from its owner.' }
    $script:passed++
}
Assert-Identity (Resolve-AssistantRuntimeIdentity $true '' '') '1000' '1000'
Assert-Identity (Resolve-AssistantRuntimeIdentity $false '23456' '23457') '23456' '23457'
Assert-Identity (Resolve-AssistantRuntimeIdentity $false '1000' '1000') '1000' '1000'
foreach ($pair in @(@('0','1000'),@('1000','0'),@('root','1000'),@('1000',''),@('-1','1000'),@('2147483648','1000'),@('01','1000'))) {
    $rejected = $false
    try { Resolve-AssistantRuntimeIdentity $false $pair[0] $pair[1] | Out-Null }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'An unsupported or root Runtime identity was accepted.' }
    $passed++
}
$tokens = $null; $errors = $null
foreach ($name in @('Assistant.Local.ps1','Invoke-MonergyUat.ps1')) {
    [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $name), [ref]$tokens, [ref]$errors) | Out-Null
    if ($errors.Count -ne 0) { throw "PowerShell parse failed: $name" }
    $passed++
}
$priorOs=$env:OS
$fixture='assistant-root-reject-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$fixturePath=Join-Path $repository ".artifacts/uat/$fixture"
try {
    # This child process emulates only the native identity probe, not any owner or database boundary.
    $env:OS='Linux'
    function id { $global:LASTEXITCODE=0; '0' }
    $rejected=$false
    try { & (Join-Path $PSScriptRoot 'Invoke-MonergyUat.ps1') -Action Prepare -Profile $fixture -EnableOnboarding -EnableAssistant -SystemExpertRoot 'unused-root-fixture' }
    catch { $rejected=$_.Exception.Message -like '*non-root operator UID and GID*' }
    if (-not $rejected -or (Test-Path -LiteralPath $fixturePath)) { throw 'Root identity was not rejected before creating profile material.' }
    $passed++
} finally { $env:OS=$priorOs }
Write-Output "Assistant Runtime identity checks: $passed passed; 0 failed. No profile created or permission changed."
