#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Profile,
    [Parameter(Mandatory)][string]$ExpectedNewInstance,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]{1,64}$')][string]$Label,
    [ValidateSet('ai-feedback','am-platform','audit-platform')][string]$ExpectedUnavailableModule,
    [Parameter(Mandatory)][switch]$ConfirmGatesABC
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'assistant-01b.owner-common.ps1')
$context = Get-AssistantOwnerContext $Profile $ExpectedNewInstance ([bool]$ConfirmGatesABC)
$definitions = @{
    'ai-feedback' = @{schema='ai_feedback';journal='assistant_feedback_schema_history';migrations=2;tables=@('topics','operations','counters','audit_outbox','audit_recovery_operations')}
    'am-platform' = @{schema='am_platform';journal='am_platform_schema_history';migrations=2;tables=@('policy','principal_references','reviewer_grants','command_results','audit_outbox','audit_delivery')}
    'audit-platform' = @{schema='audit_platform';journal='platform_audit_schema_history';migrations=1;tables=@('inbox','evidence','receipts')}
}
$snapshots = @()
foreach ($module in $context.Modules) {
    $definition = $definitions[$module.service]
    $schema = $definition.schema
    $role = $module.name + '_runtime'
    $roleState = Get-AssistantOwnerRoleState $context $module
    $expectedDown = $module.service -ceq $ExpectedUnavailableModule
    if ($roleState.login -eq $expectedDown) { throw 'Runtime login state does not match the declared fault boundary.' }
    $tableExpressions = @()
    $countGuards = @()
    foreach ($table in $definition.tables) {
        $qualified = "$schema.$table"
        $countGuards += "IF (SELECT count(*) FROM $qualified)>10000 THEN RAISE EXCEPTION 'bounded inspection limit'; END IF;"
        $tableExpressions += "'$table',(SELECT jsonb_build_object('count',count(*),'rowHashes',coalesce(jsonb_agg(h ORDER BY h),'[]'::jsonb)) FROM (SELECT encode(sha256(convert_to(to_jsonb(t)::text,'UTF8')),'hex') h FROM $qualified t) hashed_rows)"
    }
    $extra = switch ($module.service) {
        'ai-feedback' { @'
'topics',coalesce((SELECT jsonb_agg(jsonb_build_object('gapId',gap_id,'audience',source_audience,'status',status,'revision',revision,
 'createdAt',created_at,'expiresAt',expires_at,'deletedAt',deleted_at,'payloadHash',encode(sha256(convert_to(payload::text,'UTF8')),'hex')) ORDER BY gap_id) FROM ai_feedback.topics),'[]'::jsonb),
'operations',coalesce((SELECT jsonb_agg(jsonb_build_object('operationHash',encode(sha256(convert_to(operation_key,'UTF8')),'hex'),
 'gapId',gap_id,'expiresAt',expires_at,'auditEventId',audit_event_id) ORDER BY operation_key) FROM ai_feedback.operations),'[]'::jsonb),
'events',coalesce((SELECT jsonb_agg(jsonb_build_object('eventId',event_id,'producer','ai-feedback',
 'resourceId',payload->>'resourceId','action',payload->>'action','outcome',payload->>'outcome',
 'payloadHash',encode(sha256(convert_to(payload::text,'UTF8')),'hex'),'createdAt',created_at,'attempts',attempt_count,'recoveries',recovery_count,
 'deliveredAt',delivered_at,'state',CASE WHEN delivered_at IS NULL THEN 'Pending' ELSE 'Accepted' END,
 'receiptId',receipt->>'receiptId','receiptRecordedAt',receipt->>'recordedAt') ORDER BY event_id) FROM ai_feedback.audit_outbox),'[]'::jsonb)
'@ }
        'am-platform' { @'
'policy', (SELECT jsonb_build_object('schemaVersion',schema_version,'revision',revision) FROM am_platform.policy WHERE singleton),
'grants',coalesce((SELECT jsonb_agg(jsonb_build_object('entitlementId',entitlement_id,'principalHash',encode(sha256(convert_to(principal_id,'UTF8')),'hex'),
 'granted',granted,'revision',revision,'changedAt',changed_at) ORDER BY entitlement_id) FROM am_platform.reviewer_grants),'[]'::jsonb),
'events',coalesce((SELECT jsonb_agg(jsonb_build_object('eventId',o.event_id,'producer','am-platform',
 'payloadHash',encode(sha256(convert_to(o.payload_json::text,'UTF8')),'hex'),'createdAt',o.occurred_at,
 'state',d.state,'attempts',d.attempts,'recoveries',d.recovery_count,'receiptId',d.receipt_id,'receiptRecordedAt',d.recorded_at) ORDER BY o.event_id)
 FROM am_platform.audit_outbox o LEFT JOIN am_platform.audit_delivery d USING(event_id)),'[]'::jsonb)
'@ }
        'audit-platform' { @'
'events',coalesce((SELECT jsonb_agg(jsonb_build_object('eventId',e.event_id,'producer',e.producer,
 'payloadHash',encode(sha256(convert_to(e.envelope::text,'UTF8')),'hex'),'inboxPayloadHash',i.payload_hash,
 'receiptId',r.receipt_id,'receiptRecordedAt',r.recorded_at) ORDER BY e.event_id)
 FROM audit_platform.evidence e JOIN audit_platform.inbox i USING(event_id) JOIN audit_platform.receipts r USING(event_id)),'[]'::jsonb)
'@ }
    }
    $journal = $definition.journal
    $sql = @"
BEGIN READ ONLY; SET LOCAL statement_timeout='15s'; SET LOCAL timezone='UTC';
DO `$guard`$ BEGIN $($countGuards -join ' ') END `$guard`$;
SELECT jsonb_build_object('journal', (SELECT jsonb_build_object('name','$journal','count',count(*),
 'rowHashes',coalesce(jsonb_agg(encode(sha256(convert_to(to_jsonb(j)::text,'UTF8')),'hex') ORDER BY to_jsonb(j)::text),'[]'::jsonb)) FROM public.$journal j),
 'tables',jsonb_build_object($($tableExpressions -join ',')), $extra);
ROLLBACK;
"@
    $data = Invoke-AssistantOwnerSql $context $module.name ($module.name+'_owner') $module.ownerPassword $sql | ConvertFrom-Json
    if ($data.journal.count -ne $definition.migrations) { throw 'Owner migration journal count differs from the candidate migrations.' }
    if ($module.service -ceq 'am-platform' -and $data.policy.schemaVersion -ne 2) { throw 'AM platform schema version differs from the candidate.' }
    $runtimeSql = @"
BEGIN READ ONLY; SET LOCAL statement_timeout='15s';
DO `$read`$ BEGIN $((@($definition.tables | ForEach-Object { "PERFORM 1 FROM $schema.$_ LIMIT 1;" })) -join ' ') END `$read`$;
SELECT jsonb_build_object('authenticatedUser',current_user,'schemaCreate',has_schema_privilege(current_user,'$schema','CREATE'),
 'publicCreate',has_schema_privilege(current_user,'public','CREATE'),
 'markerReadable',has_table_privilege(current_user,'public.monergy_assistant_store','SELECT'),
 'tables',(SELECT jsonb_agg(jsonb_build_object('table',c.relname,'select',has_table_privilege(current_user,c.oid,'SELECT'),
 'insert',has_table_privilege(current_user,c.oid,'INSERT'),'update',has_table_privilege(current_user,c.oid,'UPDATE'),
 'delete',has_table_privilege(current_user,c.oid,'DELETE'),'truncate',has_table_privilege(current_user,c.oid,'TRUNCATE'),
 'updateColumns',(SELECT coalesce(jsonb_agg(a.attname ORDER BY a.attname),'[]'::jsonb) FROM pg_attribute a
 WHERE a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped AND has_column_privilege(current_user,c.oid,a.attnum,'UPDATE'))) ORDER BY c.relname)
 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='$schema' AND c.relkind='r'));
ROLLBACK;
"@
    $privileges = $null
    $runtimeAuthenticated = $false
    try {
        $privileges = Invoke-AssistantOwnerSql $context $module.name $role $module.runtimePassword $runtimeSql | ConvertFrom-Json
        $runtimeAuthenticated = $true
    } catch { if (-not $expectedDown) { throw } }
    if ($expectedDown -and $runtimeAuthenticated) { throw 'The declared runtime outage did not deny authentication.' }
    if (-not $expectedDown) {
        if ($privileges.authenticatedUser -cne $role -or $privileges.schemaCreate -or $privileges.publicCreate -or $privileges.markerReadable -or
            @($privileges.tables).Count -ne $definition.tables.Count) { throw 'Actual runtime schema/marker privileges failed.' }
        foreach ($table in $privileges.tables) {
            if ($table.table -cnotin $definition.tables -or -not $table.select -or -not $table.insert -or $table.truncate) { throw 'Actual runtime table privilege mismatch.' }
            $update = ($module.service -ceq 'ai-feedback' -and $table.table -cin @('topics','operations','counters')) -or
                ($module.service -ceq 'am-platform' -and $table.table -cin @('policy','principal_references','reviewer_grants','audit_delivery'))
            $delete = $module.service -ceq 'ai-feedback' -and $table.table -cin @('topics','operations','counters','audit_recovery_operations')
            if ($table.update -ne $update -or $table.delete -ne $delete) { throw 'Actual runtime mutation privilege mismatch.' }
            if ($module.service -ceq 'ai-feedback' -and $table.table -ceq 'audit_outbox') {
                if ((@($table.updateColumns) -join ',') -cne 'attempt_count,delivered_at,next_attempt_at,receipt,recovery_count') { throw 'Outbox column privileges differ from the delivery-only grant.' }
            } elseif (-not $update -and @($table.updateColumns).Count -ne 0) { throw 'Unexpected runtime column UPDATE privilege.' }
        }
    }
    $snapshots += [ordered]@{module=$module.service;database=$module.name;roleState=$roleState;runtimeAuthenticated=$runtimeAuthenticated;
        expectedUnavailable=$expectedDown;runtimePrivileges=$privileges;data=$data}
}
$result = [ordered]@{schemaVersion=1;profile=$Profile;instanceId=$ExpectedNewInstance;container=$context.Container;
    label=$Label;observedAt=[DateTimeOffset]::UtcNow.ToString('O');modules=$snapshots;businessRowsWritten=$false}
$output = Join-Path $context.Repository '.artifacts/assistant-01b-testing/browser'
Assert-AssistantOwnerPath $output
New-Item -ItemType Directory -Path $output -Force | Out-Null
Write-AssistantOwnerJson (Join-Path $output "owner-$Label.json") $result
$result | ConvertTo-Json -Depth 30 -Compress
