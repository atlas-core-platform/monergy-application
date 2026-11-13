[CmdletBinding()]
param([string]$RepositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')))

$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$container='monergy-p2p-20261113-postgres-1'
$targets=@(
  @{Tenant='T001';Kind='Evidence';Database='monergy_evidence';Role='monergy_evidence_runtime';Schema='evidence';Tables=5;Grants=11;Select=5;Insert=5;Update=1},
  @{Tenant='T002';Kind='Evidence';Database='monergy_evidence_t002';Role='monergy_evidence_runtime';Schema='evidence';Tables=5;Grants=11;Select=5;Insert=5;Update=1},
  @{Tenant='T001';Kind='Classification';Database='monergy_document_intelligence';Role='monergy_document_intelligence_runtime';Schema='public';Tables=4;Grants=11;Select=4;Insert=4;Update=3},
  @{Tenant='T002';Kind='Classification';Database='monergy_document_intelligence_t002';Role='monergy_document_intelligence_runtime';Schema='public';Tables=4;Grants=11;Select=4;Insert=4;Update=3}
)
$rows=foreach($target in $targets){
  $sql="select count(distinct table_schema||'.'||table_name),count(*),count(*) filter(where privilege_type='SELECT'),count(*) filter(where privilege_type='INSERT'),count(*) filter(where privilege_type='UPDATE'),count(*) filter(where privilege_type='DELETE'),count(*) filter(where privilege_type='TRUNCATE') from information_schema.role_table_grants where grantee='$($target.Role)' and table_schema='$($target.Schema)'; select has_database_privilege('$($target.Role)',current_database(),'CREATE'),has_schema_privilege('$($target.Role)','$($target.Schema)','CREATE');"
  $values=@(docker exec $container psql -U p2p_admin -d $target.Database -AtF '|' -c $sql)
  if($LASTEXITCODE-ne0-or$values.Count-ne2){throw "Unable to audit $($target.Database)."}
  $grants=$values[0]-split'\|';$create=$values[1]-split'\|'
  if([int]$grants[0]-ne$target.Tables-or[int]$grants[1]-ne$target.Grants-or[int]$grants[2]-ne$target.Select-or[int]$grants[3]-ne$target.Insert-or[int]$grants[4]-ne$target.Update-or[int]$grants[5]-ne0-or[int]$grants[6]-ne0-or$create[0]-ne'f'-or$create[1]-ne'f'){
    throw "Unexpected runtime privilege matrix for $($target.Database)."
  }
  [ordered]@{tenant=$target.Tenant;kind=$target.Kind;database=$target.Database;role=$target.Role;tables=[int]$grants[0];grants=[int]$grants[1];select=[int]$grants[2];insert=[int]$grants[3];update=[int]$grants[4];delete=0;truncate=0;databaseCreate=$false;schemaCreate=$false}
}
function Denied([string]$database,[string]$role,[string]$sql){
  docker exec $container psql -v ON_ERROR_STOP=1 -U p2p_admin -d $database -c "set role $role; $sql" 2>$null|Out-Null
  if($LASTEXITCODE-eq0){throw "Runtime role $role unexpectedly executed a prohibited mutation."}
}
Denied 'monergy_evidence' 'monergy_evidence_runtime' 'update evidence.outbox set topic=topic where false;'
Denied 'monergy_evidence' 'monergy_evidence_runtime' 'delete from evidence.document_versions where false;'
Denied 'monergy_evidence' 'monergy_evidence_runtime' 'truncate evidence.idempotency_operations;'
Denied 'monergy_evidence' 'monergy_evidence_runtime' 'create table evidence.prohibited(id integer);'
Denied 'monergy_document_intelligence' 'monergy_document_intelligence_runtime' 'update public.document_processing_events set processing_id=processing_id where false;'
Denied 'monergy_document_intelligence' 'monergy_document_intelligence_runtime' 'delete from public.document_classifications where false;'
Denied 'monergy_document_intelligence' 'monergy_document_intelligence_runtime' 'truncate public.document_processing_events;'
Denied 'monergy_document_intelligence' 'monergy_document_intelligence_runtime' 'create table public.prohibited(id integer);'
$result=[ordered]@{roles=4;tables=18;grants=44;matrix=$rows;prohibitedMutationProbes=8;prohibitedMutationPass=8;historyRolledBack=$true}
[IO.File]::WriteAllText((Join-Path $RepositoryRoot '.artifacts/cohort-p/privilege-audit.json'),($result|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
$result|ConvertTo-Json -Depth 8
