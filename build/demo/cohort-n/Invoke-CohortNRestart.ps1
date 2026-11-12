[CmdletBinding()]
param(
    [string]$RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
)

$ErrorActionPreference = 'Stop'
$base = 'http://127.0.0.1:4291'
$private = Get-Content (Join-Path $RepositoryRoot '.artifacts/cohort-n/private/registration-private.json') -Raw | ConvertFrom-Json
$profile = Get-Content (Join-Path $RepositoryRoot '.artifacts/uat/p2n-admin-connectivity/profile.json') -Raw | ConvertFrom-Json
$providers = Get-Content (Join-Path $RepositoryRoot '.artifacts/cohort-n/provider-physical-evidence.json') -Raw | ConvertFrom-Json
$adminHeaders = @{ 'X-Monergy-Tenant'='T001'; 'X-Monergy-Session'=$private.AdminSessionId }
$customerHeaders = @{ 'X-Monergy-Tenant'='T001'; 'X-Monergy-Session'=$private.SessionId }

function Get-Json([string]$path, [hashtable]$headers) {
    Invoke-RestMethod ($base+$path) -Headers $headers -TimeoutSec 20
}
function Credential-Version {
    $database = $profile.databases | Where-Object { $_.tenant -eq 'T001' -and $_.service -eq 'customer-identity' }
    $sql = "SELECT credential_version FROM customer_identity.registered_credentials WHERE actor_id='$($private.ActorId)';"
    $value = docker exec --env "PGPASSWORD=$($profile.postgresPassword)" monergy-uat-7426abf4-postgres-1 `
        psql -X -q -A -t -v ON_ERROR_STOP=1 -U postgres -d $database.name -c $sql
    if ($LASTEXITCODE -ne 0 -or $value -notmatch '^\d+$') { throw 'Could not read the sanitized credential version.' }
    [long]$value
}
function Consent-Count {
    $database = $profile.databases | Where-Object { $_.tenant -eq 'T001' -and $_.service -eq 'consent' }
    $value = docker exec --env "PGPASSWORD=$($profile.postgresPassword)" monergy-uat-7426abf4-postgres-1 `
        psql -X -q -A -t -v ON_ERROR_STOP=1 -U postgres -d $database.name -c 'SELECT count(*) FROM consent.provider_grants;'
    if ($LASTEXITCODE -ne 0 -or $value -notmatch '^\d+$') { throw 'Could not read the sanitized provider-grant count.' }
    [long]$value
}
function Snapshot([object]$expected = $null) {
    $customers = Get-Json '/identity-api/local/v1/administration/customers' $adminHeaders
    $customer = @($customers.items | Where-Object customerId -eq $private.CustomerId)
    if ($customer.Count -ne 1) { throw 'Registered customer is missing from Customer Management.' }
    if ($null -eq $expected) {
        $processing = Get-Json '/jobs-api/local/v1/administration/processing' $adminHeaders
        $stableJob = @($processing.items | Where-Object { $_.state -in @('COMPLETED','FAILED','CANCELLED') } | Select-Object -First 1)
        if ($stableJob.Count -ne 1) { throw 'No terminal processing identity was available for restart readback.' }
        $audit = Get-Json '/audit-api/local/v1/administration/audit-log' $adminHeaders
        $auditItem = @($audit.items | Select-Object -First 1)
        if ($auditItem.Count -ne 1) { throw 'No Audit identity was available for restart readback.' }
    }
    else {
        $stableJob = @(Get-Json ('/jobs-api/local/v1/administration/processing/'+$expected.processing.id) $adminHeaders)
        $auditItem = @(Get-Json ('/audit-api/local/v1/administration/audit-log/'+$expected.audit.id) $adminHeaders)
    }
    $googleDocument = Get-Json ('/evidence-api/local/v1/administration/documents/'+$providers.GoogleDrive.DocumentVersionId) $adminHeaders
    $oneDriveDocument = Get-Json ('/evidence-api/local/v1/administration/documents/'+$providers.OneDrive.DocumentVersionId) $adminHeaders
    $google = Get-Json '/connections-api/local/v1/google-drive' $customerHeaders
    $oneDrive = Get-Json '/connections-api/local/v1/onedrive' $customerHeaders
    [ordered]@{
        customer=[ordered]@{ id=$customer[0].customerId; active=$customer[0].active; version=[long]$customer[0].version }
        processing=[ordered]@{ id=$stableJob[0].jobId; state=$stableJob[0].state }
        googleDocument=[ordered]@{ id=$googleDocument.documentVersionId; scan=$googleDocument.scanStatus; tenant=$googleDocument.tenantId }
        oneDriveDocument=[ordered]@{ id=$oneDriveDocument.documentVersionId; scan=$oneDriveDocument.scanStatus; tenant=$oneDriveDocument.tenantId }
        audit=[ordered]@{ id=$auditItem[0].evidenceId; request=$auditItem[0].requestId; phase=$auditItem[0].phase }
        credentialVersion=(Credential-Version)
        providerGrantCount=(Consent-Count)
        google=[ordered]@{ status=$google.status; connection=$google.connectionId; version=$google.version; imports=@($google.imports).Count }
        oneDrive=[ordered]@{ status=$oneDrive.status; connection=$oneDrive.connectionId; version=$oneDrive.version; imports=@($oneDrive.imports).Count }
    }
}
function Wait-ContainerHealthy([string]$container) {
    for ($attempt=0; $attempt -lt 60; $attempt++) {
        $status = docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' $container
        if ($status -in @('healthy','running')) { return }
        Start-Sleep -Milliseconds 500
    }
    throw "$container did not recover."
}
function Wait-Ready {
    for ($attempt=0; $attempt -lt 120; $attempt++) {
        try {
            $ready = Invoke-WebRequest "$base/health/ready" -SkipHttpErrorCheck -TimeoutSec 5
            if ($ready.StatusCode -eq 200) { return ($ready.Content | ConvertFrom-Json) }
        }
        catch { }
        Start-Sleep -Milliseconds 500
    }
    throw 'Aggregate readiness did not recover after retained restart.'
}

$before = Snapshot
$appContainers = @(docker ps -a --filter 'name=monergy-uat-7426abf4-' --format '{{.Names}}')
$infraContainers = @(docker ps -a --filter 'name=monergy-p2n-20261112-' --format '{{.Names}}')
if ($appContainers.Count -lt 20 -or $infraContainers.Count -ne 2) { throw 'Unexpected Cohort N retained container inventory.' }
docker stop $appContainers | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not stop the retained Cohort N application containers.' }
docker stop $infraContainers | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not stop the retained Cohort N owner infrastructure.' }

docker start $infraContainers | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not restart the retained Cohort N owner infrastructure.' }
foreach ($container in $infraContainers) { Wait-ContainerHealthy $container }
$profilePostgres = @($appContainers | Where-Object { $_ -match '-postgres-1$' })
$workspace = @($appContainers | Where-Object { $_ -match '-workspace-1$' })
$services = @($appContainers | Where-Object { $_ -notmatch '-(postgres|workspace|bootstrap)-1$' })
if ($profilePostgres.Count -ne 1 -or $workspace.Count -ne 1 -or $services.Count -lt 18) {
    throw 'Unexpected Cohort N application dependency inventory.'
}
docker start $profilePostgres[0] | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not restart the retained profile PostgreSQL container.' }
Wait-ContainerHealthy $profilePostgres[0]
docker start $workspace[0] | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not restart the retained workspace network container.' }
Wait-ContainerHealthy $workspace[0]
docker start $services | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not restart the retained Cohort N service containers.' }
$ready = Wait-Ready
$after = Snapshot $before

foreach ($name in @('customer','processing','googleDocument','oneDriveDocument','audit','credentialVersion','providerGrantCount','google','oneDrive')) {
    $left = $before.$name | ConvertTo-Json -Compress -Depth 6
    $right = $after.$name | ConvertTo-Json -Compress -Depth 6
    if ($left -ne $right) { throw "Retained restart changed $name identity/state." }
}
$profileResponse = Invoke-WebRequest "$base/identity-api/local/v1/me/customer-profile" -Headers $customerHeaders -SkipHttpErrorCheck -TimeoutSec 15
if ($profileResponse.StatusCode -ne 200) { throw 'Recovered registration session did not survive the retained restart.' }
$topology = Get-Json '/uat-api/v1/topology' $adminHeaders

$evidence = [ordered]@{
    recordedAt=[DateTimeOffset]::UtcNow.ToString('O')
    sameContainers=$true
    sameVolumes=$true
    before=$before
    after=$after
    readiness=[ordered]@{ serviceCount=$ready.serviceCount; status='Ready' }
    topology=[ordered]@{ serviceCount=@($topology.services).Count; healthy=@($topology.services | Where-Object healthy).Count }
    customerSession=[int]$profileResponse.StatusCode
}
$output = Join-Path $RepositoryRoot '.artifacts/cohort-n/restart-evidence.json'
[IO.File]::WriteAllText($output, ($evidence | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
[pscustomobject]@{
    ApplicationContainers=$appContainers.Count
    InfrastructureContainers=$infraContainers.Count
    Readiness=$ready.serviceCount
    Topology=@($topology.services).Count
    Readback='EXACT'
    CustomerSession=[int]$profileResponse.StatusCode
}
