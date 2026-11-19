#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Profile,
    [Parameter(Mandatory)][string]$ExpectedNewInstance,
    [Parameter(Mandatory)][ValidateSet('ai-feedback','am-platform','audit-platform')][string]$Module,
    [Parameter(Mandatory)][ValidateSet('Apply','Restore')][string]$Action,
    [Parameter(Mandatory)][guid]$FaultId,
    [Parameter(Mandatory)][switch]$ConfirmGatesABC
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'assistant-01b.owner-common.ps1')
$context = Get-AssistantOwnerContext $Profile $ExpectedNewInstance ([bool]$ConfirmGatesABC)
$store = @($context.Modules | Where-Object service -CEQ $Module)[0]
$folder = Join-Path $context.Repository '.artifacts/assistant-01b-private-fixtures/owner-faults'
Protect-AssistantOwnerDirectory $folder
$path = Join-Path $folder "$ExpectedNewInstance-$($FaultId.ToString('N')).json"
$mutex = [Threading.Mutex]::new($false,"monergy-assistant-owner-$ExpectedNewInstance")
$locked = $false
$baseline = $null
$baselineWritten = $false
$completed = $false

function Restore-OwnerFault {
    $current = Get-AssistantOwnerRoleState $context $store
    foreach ($property in @('database','databaseOid','owner','databaseAclHash','role','roleOid')) {
        if ($current.$property -cne $baseline.roleState.$property) { throw 'Fault restoration binding changed; no automatic mutation is safe.' }
    }
    $login = if ($baseline.roleState.login) { 'LOGIN' } else { 'NOLOGIN' }
    $sql = 'ALTER ROLE "' + $current.role + '" ' + $login + ';'
    $null = Invoke-AssistantOwnerSql $context 'postgres' 'postgres' $context.State.postgresPassword $sql
    $restored = Get-AssistantOwnerRoleState $context $store
    if ($restored.login -ne $baseline.roleState.login -or $restored.databaseAclHash -cne $baseline.roleState.databaseAclHash) {
        throw 'Runtime restoration verification failed.'
    }
    $baseline.state = 'Restored'
    $baseline.restoredAt = [DateTimeOffset]::UtcNow.ToString('O')
    Write-AssistantOwnerJson $path $baseline -Replace
}

try {
    try { $locked = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked = $true }
    if (-not $locked) { throw 'Another owner-fault operation is in progress for this new profile.' }
    if ($Action -eq 'Apply') {
        foreach ($existing in @(Get-ChildItem -LiteralPath $folder -Filter "$ExpectedNewInstance-*.json" -File)) {
            Assert-AssistantOwnerPath $existing.FullName
            $prior = Get-Content -LiteralPath $existing.FullName -Raw | ConvertFrom-Json
            if ($prior.module -ceq $Module -and $prior.state -cne 'Restored') { throw 'An unrestored fault for this module already exists.' }
        }
        $roleState = Get-AssistantOwnerRoleState $context $store
        if (-not $roleState.login) { throw 'The baseline runtime role is already unavailable; refusing to hide an existing failure.' }
        $baseline = [pscustomobject]@{schemaVersion=1;profile=$Profile;instanceId=$ExpectedNewInstance;container=$context.Container;
            module=$Module;faultId=$FaultId.ToString('D');roleState=$roleState;state='Prepared';preparedAt=[DateTimeOffset]::UtcNow.ToString('O');restoredAt=$null}
        Write-AssistantOwnerJson $path $baseline
        $baselineWritten = $true
        # Only this module role changes. Existing CONNECT grants, passwords, databases and business rows are untouched.
        $sql = 'ALTER ROLE "' + $roleState.role + '" NOLOGIN; SELECT count(*) FROM (SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname=''' +
            $store.name + ''' AND usename=''' + $roleState.role + ''' AND pid<>pg_backend_pid()) terminated;'
        $null = Invoke-AssistantOwnerSql $context 'postgres' 'postgres' $context.State.postgresPassword $sql
        $during = Get-AssistantOwnerRoleState $context $store
        if ($during.login -or $during.databaseAclHash -cne $roleState.databaseAclHash) { throw 'Owner-store fault did not preserve the intended boundary.' }
        $baseline.state = 'Applied'
        Write-AssistantOwnerJson $path $baseline -Replace
        $completed = $true
    } else {
        Assert-AssistantOwnerPath $path
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            $current = Get-AssistantOwnerRoleState $context $store
            if (-not $current.login) { throw 'Runtime is unavailable without its protected restoration baseline.' }
            [ordered]@{schemaVersion=1;profile=$Profile;instanceId=$ExpectedNewInstance;module=$Module;faultId=$FaultId.ToString('D');
                action=$Action;state='NoFaultApplied';credentialsChanged=$false;businessRowsWritten=$false} | ConvertTo-Json -Compress
            return
        }
        $baseline = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        if ($baseline.schemaVersion -ne 1 -or $baseline.profile -cne $Profile -or $baseline.instanceId -cne $ExpectedNewInstance -or
            $baseline.module -cne $Module -or $baseline.faultId -cne $FaultId.ToString('D') -or $baseline.container -cne $context.Container) {
            throw 'Protected fault baseline identity mismatch.'
        }
        Restore-OwnerFault
        $completed = $true
    }
    [ordered]@{schemaVersion=1;profile=$Profile;instanceId=$ExpectedNewInstance;module=$Module;faultId=$FaultId.ToString('D');
        action=$Action;state=$baseline.state;databaseAclUnchanged=$true;credentialsChanged=$false;businessRowsWritten=$false} | ConvertTo-Json -Compress
} finally {
    try {
        # A partially failed Apply is immediately rolled back. A successful Apply deliberately remains active
        # for browser assertions; callers MUST put their corresponding Restore in a try/finally block.
        if ($Action -eq 'Apply' -and $baselineWritten -and -not $completed) { Restore-OwnerFault }
    } finally {
        if ($locked) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}
