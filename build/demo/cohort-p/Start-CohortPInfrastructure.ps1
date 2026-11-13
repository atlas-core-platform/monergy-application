[CmdletBinding()]
param([string]$RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')))

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function New-Secret {
    $bytes = [byte[]]::new(32)
    [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    [Convert]::ToHexString($bytes).ToLowerInvariant()
}
function Save-Json([string]$Path, [object]$Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
}

$template = Join-Path $RepositoryRoot 'build/demo/cohort-n/Start-CohortNInfrastructure.ps1'
$source = Get-Content -LiteralPath $template -Raw -Encoding utf8
$replace = [ordered]@{
    'p2-cohort-n-acceptance' = 'p2-cohort-p-acceptance'
    'monergy-p2n-20261112' = 'monergy-p2p-20261113'
    '55642' = '55662'
    '58543' = '58563'
    'monergy-evidence-n' = 'monergy-evidence-p'
    '.artifacts/cohort-n' = '.artifacts/cohort-p'
    'p2n_admin' = 'p2p_admin'
    'p2n-fixture' = 'p2p-fixture'
    'p2n-document-intelligence-migrations' = 'p2p-document-intelligence-migrations'
    'P2N' = 'P2P'
    'Cohort N' = 'Cohort P'
    'build/demo/cohort-n/compose.override.yml' = 'build/demo/cohort-p/compose.override.yml'
}
foreach ($entry in $replace.GetEnumerator()) { $source = $source.Replace([string]$entry.Key, [string]$entry.Value) }
$bootstrap = [scriptblock]::Create($source)
& $bootstrap -RepositoryRoot $RepositoryRoot
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$privateRoot = Join-Path $RepositoryRoot '.artifacts/cohort-p/private'
$statePath = Join-Path $privateRoot 'runtime-state.json'
$state = Get-Content -LiteralPath $statePath -Raw -Encoding utf8 | ConvertFrom-Json
$postgresContainer = "$($state.project)-postgres-1"
$evidence = $state.services.evidence
$classification = $state.services.document_intelligence

if ($null -eq $state.PSObject.Properties['tenantDatabases']) {
    $state | Add-Member -NotePropertyName tenantDatabases -NotePropertyValue ([pscustomobject]@{
        evidenceT002 = 'monergy_evidence_t002'
        classificationT002 = 'monergy_document_intelligence_t002'
    })
    Save-Json $statePath $state
}

foreach ($database in @(
    [pscustomobject]@{ Name=$state.tenantDatabases.evidenceT002; Owner=$evidence.owner; Runtime=$evidence.runtime },
    [pscustomobject]@{ Name=$state.tenantDatabases.classificationT002; Owner=$classification.owner; Runtime=$classification.runtime }
)) {
    $exists = docker exec $postgresContainer psql -U $state.adminUser -d postgres -Atc "SELECT 1 FROM pg_database WHERE datname='$($database.Name)'"
    if ($LASTEXITCODE -ne 0) { throw "Unable to inspect isolated database $($database.Name)." }
    if ([string]::IsNullOrWhiteSpace(($exists | Out-String))) {
        docker exec $postgresContainer psql -U $state.adminUser -d postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE $($database.Name) OWNER $($database.Owner)"
        if ($LASTEXITCODE -ne 0) { throw "Unable to create isolated database $($database.Name)." }
    }
    docker exec $postgresContainer psql -U $state.adminUser -d postgres -v ON_ERROR_STOP=1 -c "REVOKE CONNECT ON DATABASE $($database.Name) FROM PUBLIC; GRANT CONNECT ON DATABASE $($database.Name) TO $($database.Owner), $($database.Runtime)"
    if ($LASTEXITCODE -ne 0) { throw "Unable to isolate database $($database.Name)." }
}

$env:P2P_OWNER_CONNECTION = "Host=127.0.0.1;Port=$($state.postgresPort);Database=$($state.tenantDatabases.evidenceT002);Username=$($evidence.owner);Password=$($evidence.ownerPassword);SSL Mode=Disable;Include Error Detail=false"
try {
    dotnet run --project (Join-Path $RepositoryRoot 'build/Monergy.DatabaseMigrator/Monergy.DatabaseMigrator.csproj') --configuration Release -- --service evidence --repository-root $RepositoryRoot --connection-environment P2P_OWNER_CONNECTION
    if ($LASTEXITCODE -ne 0) { throw 'The T002 Evidence migrations failed.' }
}
finally { Remove-Item Env:P2P_OWNER_CONNECTION -ErrorAction SilentlyContinue }

$migrationTarget = '/tmp/p2p-document-intelligence-migrations'
docker exec $postgresContainer sh -c "rm -rf $migrationTarget && mkdir -p $migrationTarget"
docker cp (Join-Path $RepositoryRoot 'services/document-intelligence/migrations/.') "${postgresContainer}:$migrationTarget"
foreach ($migration in @('001-document-classifications.sql','002-document-ocr.sql','003-document-processing.sql')) {
    docker exec -e "PGPASSWORD=$($classification.ownerPassword)" $postgresContainer psql -v ON_ERROR_STOP=1 -U $classification.owner -d $state.tenantDatabases.classificationT002 -f "$migrationTarget/$migration"
    if ($LASTEXITCODE -ne 0) { throw "T002 Document Intelligence migration $migration failed." }
}
docker exec -e "PGPASSWORD=$($classification.ownerPassword)" $postgresContainer psql -v ON_ERROR_STOP=1 -U $classification.owner -d $state.tenantDatabases.classificationT002 -c "REVOKE CREATE ON SCHEMA public FROM PUBLIC; GRANT USAGE ON SCHEMA public TO $($classification.runtime); GRANT SELECT,INSERT,UPDATE ON document_classifications,document_ocr,document_processing TO $($classification.runtime); GRANT SELECT,INSERT ON document_processing_events TO $($classification.runtime); REVOKE DELETE,TRUNCATE ON document_classifications,document_ocr,document_processing,document_processing_events FROM $($classification.runtime); REVOKE UPDATE ON document_processing_events FROM $($classification.runtime)"
if ($LASTEXITCODE -ne 0) { throw 'Unable to establish T002 Document Intelligence runtime privileges.' }
docker exec $postgresContainer sh -c "rm -rf $migrationTarget"

$evidenceConnection = "Host=host.docker.internal;Port=$($state.postgresPort);Database=$($evidence.database);Username=$($evidence.runtime);Password=$($evidence.runtimePassword);SSL Mode=Disable;Timeout=5;Include Error Detail=false"
$evidenceT002Connection = "Host=host.docker.internal;Port=$($state.postgresPort);Database=$($state.tenantDatabases.evidenceT002);Username=$($evidence.runtime);Password=$($evidence.runtimePassword);SSL Mode=Disable;Timeout=5;Include Error Detail=false"
$classificationConnection = "Host=host.docker.internal;Port=$($state.postgresPort);Database=$($classification.database);Username=$($classification.runtime);Password=$($classification.runtimePassword);SSL Mode=Disable;Timeout=5;Include Error Detail=false"
$classificationT002Connection = "Host=host.docker.internal;Port=$($state.postgresPort);Database=$($state.tenantDatabases.classificationT002);Username=$($classification.runtime);Password=$($classification.runtimePassword);SSL Mode=Disable;Timeout=5;Include Error Detail=false"
Save-Json (Join-Path $privateRoot 'evidence-persistence.json') @{
    RuntimeConnections=@{T001=$evidenceConnection;T002=$evidenceT002Connection}
    S3=@{Endpoint="http://host.docker.internal:$($state.s3Port)";Bucket=$state.bucket;AccessKey=$state.s3AccessKey;SecretKey=$state.s3SecretKey}
}
Save-Json (Join-Path $privateRoot 'classification.json') @{
    RuntimeConnections=@{T001=$classificationConnection;T002=$classificationT002Connection}
    Reviewers=@{T001=@('A900');T002=@('A900')}
}

if ($env:OS -eq 'Windows_NT') {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    & icacls (Join-Path $RepositoryRoot '.artifacts/cohort-p') /inheritance:r /grant:r "${identity}:(OI)(CI)F" 'SYSTEM:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Unable to protect the Cohort P credential directory.' }
}
[pscustomobject]@{
    Project=$state.project
    PostgreSqlPort=$state.postgresPort
    S3Port=$state.s3Port
    Bucket=$state.bucket
    TenantEvidenceDatabases=@($evidence.database,$state.tenantDatabases.evidenceT002)
    TenantClassificationDatabases=@($classification.database,$state.tenantDatabases.classificationT002)
} | ConvertTo-Json -Depth 4
