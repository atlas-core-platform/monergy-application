[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
)

$ErrorActionPreference = 'Stop'
$profile = Get-Content (Join-Path $RepositoryRoot '.artifacts/uat/p2n-admin-connectivity/profile.json') -Raw | ConvertFrom-Json
$external = Get-Content (Join-Path $RepositoryRoot '.artifacts/cohort-n/private/runtime-state.json') -Raw | ConvertFrom-Json

function Invoke-PrivilegeQuery {
    param(
        [string]$Container,
        [string]$AdminUser,
        [string]$AdminPassword,
        [string]$Database,
        [string]$RuntimeRole,
        [string]$Owner
    )

    foreach ($identifier in @($Database, $RuntimeRole)) {
        if ($identifier -notmatch '^[a-z0-9_]+$') { throw "Unsafe PostgreSQL identifier." }
    }

    $sql = @"
SET ROLE "$RuntimeRole";
SELECT json_build_object(
  'owner', '$Owner',
  'database', current_database(),
  'role', current_user,
  'databaseCreate', has_database_privilege(current_user,current_database(),'CREATE'),
  'schemas', (
    SELECT coalesce(json_agg(json_build_object(
      'schema', s.schema_name,
      'create', has_schema_privilege(current_user,s.schema_name,'CREATE')
    ) ORDER BY s.schema_name),'[]'::json)
    FROM information_schema.schemata s
    WHERE s.schema_name NOT IN ('pg_catalog','information_schema','pg_toast')
  ),
  'tables', (
    SELECT coalesce(json_agg(json_build_object(
      'schema', t.table_schema,
      'table', t.table_name,
      'select', has_table_privilege(current_user,format('%I.%I',t.table_schema,t.table_name),'SELECT'),
      'insert', has_table_privilege(current_user,format('%I.%I',t.table_schema,t.table_name),'INSERT'),
      'update', has_table_privilege(current_user,format('%I.%I',t.table_schema,t.table_name),'UPDATE'),
      'delete', has_table_privilege(current_user,format('%I.%I',t.table_schema,t.table_name),'DELETE'),
      'truncate', has_table_privilege(current_user,format('%I.%I',t.table_schema,t.table_name),'TRUNCATE')
    ) ORDER BY t.table_schema,t.table_name),'[]'::json)
    FROM information_schema.tables t
    WHERE t.table_schema NOT IN ('pg_catalog','information_schema')
  )
);
"@
    $json = docker exec --env "PGPASSWORD=$AdminPassword" $Container psql -X -q -A -t -v ON_ERROR_STOP=1 -U $AdminUser -d $Database -c $sql
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($json)) { throw "Privilege query failed for $Owner." }
    return $json | ConvertFrom-Json
}

$results = [System.Collections.Generic.List[object]]::new()
foreach ($database in $profile.databases) {
    $runtimeRole = "$($database.name)_runtime"
    $results.Add((Invoke-PrivilegeQuery -Container 'monergy-uat-7426abf4-postgres-1' -AdminUser 'postgres' `
        -AdminPassword $profile.postgresPassword -Database $database.name -RuntimeRole $runtimeRole `
        -Owner "$($database.service):$($database.tenant)"))
}

foreach ($name in @('job_management','evidence','audit','document_intelligence','reporting')) {
    $service = $external.services.$name
    $results.Add((Invoke-PrivilegeQuery -Container 'monergy-p2n-20261112-postgres-1' -AdminUser $external.adminUser `
        -AdminPassword $external.adminPassword -Database $service.database -RuntimeRole $service.runtime -Owner $name))
}

$violations = [System.Collections.Generic.List[string]]::new()
foreach ($result in $results) {
    if ($result.databaseCreate) { $violations.Add("$($result.owner): database CREATE allowed") }
    foreach ($schema in $result.schemas) {
        if ($schema.create) { $violations.Add("$($result.owner): schema CREATE allowed on $($schema.schema)") }
    }
    foreach ($table in $result.tables) {
        if ($table.delete) { $violations.Add("$($result.owner): DELETE allowed on $($table.schema).$($table.table)") }
        if ($table.truncate) { $violations.Add("$($result.owner): TRUNCATE allowed on $($table.schema).$($table.table)") }
    }
}

$evidence = [ordered]@{
    recordedAt = [DateTimeOffset]::UtcNow.ToString('O')
    owners = $results
    universalDenyViolations = $violations
}
$output = Join-Path $RepositoryRoot '.artifacts/cohort-n/privilege-evidence.json'
[IO.File]::WriteAllText($output, ($evidence | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
[pscustomobject]@{
    Owners = $results.Count
    Tables = @($results | ForEach-Object { $_.tables }).Count
    UniversalDenyViolations = $violations.Count
    Evidence = $output
}
