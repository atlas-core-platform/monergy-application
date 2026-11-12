[CmdletBinding()]
param([string]$RepositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')))
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest

$profile=Get-Content (Join-Path $RepositoryRoot '.artifacts/uat/p2o-customer-completion/profile.json') -Raw|ConvertFrom-Json
$uatProject="monergy-uat-$($profile.instanceId.Substring(0,8))"
$uatPostgres="$uatProject-postgres-1"
$infra=Get-Content (Join-Path $RepositoryRoot '.artifacts/cohort-o/private/runtime-state.json') -Raw|ConvertFrom-Json
$infraPostgres="$($infra.project)-postgres-1"
$results=[Collections.Generic.List[object]]::new()

function Test-Privilege([string]$Container,[string]$Admin,[string]$Database,[string]$Role,
    [string]$Schema,[string]$Table,[bool]$Insert,[bool]$Update,[bool]$Delete,[bool]$Truncate) {
    $sql="SELECT has_table_privilege('$Role','$Schema.$Table','SELECT'),has_table_privilege('$Role','$Schema.$Table','INSERT'),has_table_privilege('$Role','$Schema.$Table','UPDATE'),has_table_privilege('$Role','$Schema.$Table','DELETE'),has_table_privilege('$Role','$Schema.$Table','TRUNCATE'),has_schema_privilege('$Role','$Schema','CREATE'),has_database_privilege('$Role','$Database','CREATE');"
    $actual=(docker exec $Container psql -U $Admin -d $Database -At -F ',' -c $sql|Out-String).Trim()
    if($LASTEXITCODE-ne 0){throw "Privilege inspection failed for $Schema.$Table."}
    $expected="t,$(if($Insert){'t'}else{'f'}),$(if($Update){'t'}else{'f'}),$(if($Delete){'t'}else{'f'}),$(if($Truncate){'t'}else{'f'}),f,f"
    if($actual-ne $expected){throw "Runtime privilege mismatch for $Schema.$Table; expected $expected, got $actual."}
    $results.Add([pscustomobject]@{Owner="$Schema.$Table";Select=$true;Insert=$Insert;Update=$Update;Delete=$Delete;Truncate=$Truncate;SchemaCreate=$false;DatabaseCreate=$false})
}

$ciDb='uat_customer_identity_t001';$ciRole='uat_customer_identity_t001_runtime'
Test-Privilege $uatPostgres 'postgres' $ciDb $ciRole 'customer_identity' 'kyc_events' $true $false $false $false
Test-Privilege $uatPostgres 'postgres' $ciDb $ciRole 'customer_identity' 'customer_profile_receipts' $true $false $false $false
Test-Privilege $uatPostgres 'postgres' $ciDb $ciRole 'customer_identity' 'customer_pan_audit' $true $false $false $false
Test-Privilege $uatPostgres 'postgres' $ciDb $ciRole 'customer_identity' 'customer_profiles' $true $true $false $false

$e=$infra.services.evidence
Test-Privilege $infraPostgres $infra.adminUser $e.database $e.runtime 'evidence' 'document_versions' $true $false $false $false
Test-Privilege $infraPostgres $infra.adminUser $e.database $e.runtime 'evidence' 'vault_operations' $true $false $false $false
Test-Privilege $infraPostgres $infra.adminUser $e.database $e.runtime 'evidence' 'vault_metadata' $true $true $false $false
$fp=$infra.services.financial_profile
Test-Privilege $infraPostgres $infra.adminUser $fp.database $fp.runtime 'financial_profile' 'monthly_cashflow_positions' $true $false $false $false
$fr=$infra.services.financial_rules
Test-Privilege $infraPostgres $infra.adminUser $fr.database $fr.runtime 'financial_rules' 'monthly_cashflow_results' $true $false $false $false

[pscustomobject]@{Checks=$results.Count;Passed=$results.Count;Failed=0;AppendOnly=7;NarrowState=2;SchemaCreateDenied=$results.Count;DatabaseCreateDenied=$results.Count;Results=$results}|ConvertTo-Json -Depth 4
