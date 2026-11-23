#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^assistant01b(?:-[a-z0-9-]+)?$')][string]$Profile,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{32}$')][string]$ExpectedNewInstance,
    [ValidatePattern('^[a-z0-9-]{1,64}$')][string]$BeforeLabel,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]{1,64}$')][string]$AfterLabel,
    [Parameter(Mandatory)][ValidateSet('Delivery','Restart')][string]$Mode,
    [switch]$RequireAllDelivered
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'assistant-01b.owner-common.ps1')
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$folder = Join-Path $repository '.artifacts/assistant-01b-testing/browser'
function Read-OwnerSnapshot([string]$Label) {
    $path = Join-Path $folder "owner-$Label.json"
    Assert-AssistantOwnerPath $path
    $value = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($value.schemaVersion -ne 1 -or $value.profile -cne $Profile -or $value.instanceId -cne $ExpectedNewInstance -or
        $value.label -cne $Label -or @($value.modules).Count -ne 3 -or $value.businessRowsWritten -ne $false) { throw 'Snapshot identity mismatch.' }
    foreach ($service in @('ai-feedback','am-platform','audit-platform')) {
        if (@($value.modules | Where-Object module -CEQ $service).Count -ne 1) { throw 'Snapshot module identity is ambiguous.' }
    }
    return $value
}
function Require-Equal($Left, $Right, [string]$Description) {
    if (($Left | ConvertTo-Json -Depth 25 -Compress) -cne ($Right | ConvertTo-Json -Depth 25 -Compress)) { throw "Owner evidence mismatch: $Description." }
}
function Require-ReceiptEqual($Left, $Right) {
    function Get-UtcTicks($Value) {
        if ($Value -is [DateTimeOffset]) { return $Value.UtcTicks }
        if ($Value -is [DateTime]) { return $Value.ToUniversalTime().Ticks }
        return [DateTimeOffset]::Parse($Value,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::None).UtcTicks
    }
    if ($Left.receiptId -cne $Right.receiptId -or
        (Get-UtcTicks $Left.receiptRecordedAt) -ne (Get-UtcTicks $Right.receiptRecordedAt)) {
        throw 'Central and owner delivery receipt identities differ.'
    }
}
$after = Read-OwnerSnapshot $AfterLabel
$before = if ($BeforeLabel) { Read-OwnerSnapshot $BeforeLabel } else { $null }
if ($Mode -eq 'Restart' -and $null -eq $before) { throw 'Restart comparison requires a before snapshot.' }
$central = @(@($after.modules | Where-Object module -CEQ 'audit-platform')[0].data.events)
$comparedEvents = 0
$accepted = 0
$pending = 0
foreach ($module in @($after.modules | Where-Object module -CNE 'audit-platform')) {
    if ($RequireAllDelivered -and @($module.data.events).Count -eq 0) { throw 'Central delivery evidence must contain actual events from both producer owners.' }
    foreach ($event in @($module.data.events)) {
        $matches = @($central | Where-Object { $_.eventId -ceq $event.eventId -and $_.producer -ceq $module.module })
        if ($matches.Count -gt 1) { throw 'Duplicate central event receipt.' }
        if ($matches.Count -eq 1) {
            if ($matches[0].payloadHash -cne $event.payloadHash) { throw 'Central audit envelope hash differs from owner event.' }
            $comparedEvents++
        }
        if ($event.state -ceq 'Accepted') {
            if ($matches.Count -ne 1) { throw 'Owner claims delivery without matching central evidence.' }
            Require-ReceiptEqual $event $matches[0]
            $accepted++
        } else { $pending++ }
    }
}
if ($RequireAllDelivered -and $pending -ne 0) { throw 'Owner delivery remains pending; central audit completion cannot be claimed.' }
$persistedTopics = 0
$persistedEvents = 0
$persistedRows = 0
if ($Mode -eq 'Restart') {
    foreach ($old in $before.modules) {
        $new = @($after.modules | Where-Object module -CEQ $old.module)[0]
        if (-not $new.runtimeAuthenticated -or $new.expectedUnavailable) { throw 'Restart readback requires restored actual-runtime authentication.' }
        foreach ($property in @('database','databaseOid','owner','databaseAclHash','role','roleOid','login')) {
            Require-Equal $old.roleState.$property $new.roleState.$property "retained $property"
        }
        Require-Equal $old.data.journal $new.data.journal 'migration journal'
        # These rows are immutable, or must stay unchanged during the deliberately bounded restart interval.
        $tables = switch ($old.module) {
            'ai-feedback' { @('operations','audit_recovery_operations') }
            'am-platform' { @('policy','reviewer_grants','command_results','audit_outbox') }
            'audit-platform' { @('inbox','evidence','receipts') }
        }
        foreach ($table in $tables) {
            foreach ($hash in @($old.data.tables.$table.rowHashes)) {
                if ($hash -cnotin @($new.data.tables.$table.rowHashes)) { throw "Persisted row missing or changed in $($old.module)/$table." }
                $persistedRows++
            }
        }
        foreach ($event in @($old.data.events)) {
            $matches = @($new.data.events | Where-Object eventId -CEQ $event.eventId)
            if ($matches.Count -ne 1 -or $matches[0].payloadHash -cne $event.payloadHash) { throw 'Persisted owner/central event changed across restart.' }
            if ($old.module -ceq 'audit-platform' -or $event.state -ceq 'Accepted') { Require-ReceiptEqual $event $matches[0] }
            $persistedEvents++
        }
        if ($old.module -ceq 'ai-feedback') {
            foreach ($topic in @($old.data.topics)) {
                $matches = @($new.data.topics | Where-Object gapId -CEQ $topic.gapId)
                if ($matches.Count -ne 1) { throw 'Persisted gap missing after restart.' }
                Require-Equal $topic $matches[0] 'gap content/revision/creation/expiry'
                $persistedTopics++
            }
        }
    }
    if ($persistedTopics -eq 0 -or $persistedEvents -eq 0) { throw 'Restart evidence must include non-empty topics and durable audit events.' }
}
$report = [ordered]@{schemaVersion=1;profile=$Profile;instanceId=$ExpectedNewInstance;mode=$Mode;before=$BeforeLabel;after=$AfterLabel;
    result='PASS';centralPayloadsCompared=$comparedEvents;acceptedReceiptsCompared=$accepted;pendingEvents=$pending;
    persistedTopics=$persistedTopics;persistedEvents=$persistedEvents;persistedRows=$persistedRows;crossOwnerSql=$false}
Write-AssistantOwnerJson (Join-Path $folder "owner-compare-$($Mode.ToLowerInvariant())-$AfterLabel.json") $report
$report | ConvertTo-Json -Compress
