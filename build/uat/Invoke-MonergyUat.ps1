[CmdletBinding()]
param(
    [ValidateSet('Prepare','Start','Status','Stop','Verify','Credentials','Reset')][string]$Action = 'Start',
    [ValidatePattern('^[a-z][a-z0-9-]{0,30}$')][string]$Profile = 'default',
    [ValidateRange(1024,65535)][int]$Port = 4173,
    [ValidateSet('T001','T002')][string]$Tenant = 'T001',
    [ValidateSet('A900','A100','A300')][string]$Actor = 'A900',
    [switch]$ConfirmReset
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$folder = Join-Path $repository ".artifacts/uat/$Profile"
$profileFile = Join-Path $folder 'profile.json'
$windows = $env:OS -eq 'Windows_NT'
function Secret { $bytes = New-Object byte[] 32; $rng = [Security.Cryptography.RandomNumberGenerator]::Create(); try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }; return ([BitConverter]::ToString($bytes)).Replace('-','').ToLowerInvariant() }
function Save-Text($name, $value) { [IO.File]::WriteAllText((Join-Path $folder $name), $value, (New-Object Text.UTF8Encoding($false))) }
function Save-Json($name, $value) { Save-Text $name ($value | ConvertTo-Json -Depth 30) }
function Protect-Files {
    if ($windows) {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
        & icacls $folder '/inheritance:r' '/grant:r' "${identity}:(OI)(CI)F" /T /Q | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not restrict local UAT credentials.' }
    } else { & chmod 700 $folder; Get-ChildItem $folder -File | ForEach-Object { & chmod 600 $_.FullName } }
}
function Invoke-UatDocker([string[]]$Arguments) {
    & docker @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Docker command failed: $($Arguments[0])" }
}
function Compose([string[]]$Arguments) {
    Invoke-UatDocker (@('compose','--env-file',(Join-Path $folder 'compose.env'),'-f',(Join-Path $PSScriptRoot 'compose.yml'),'--project-name',"monergy-uat-$($state.instanceId.Substring(0,8))") + $Arguments)
}
function Connection($database, $scope) {
    $password = $database."${scope}Password"
    return "Host=postgres;Database=$($database.name);Username=$($database.name)_$scope;Password=$password;Timeout=5;Include Error Detail=false"
}
if (-not (Test-Path $profileFile)) {
    if ($Action -notin @('Prepare','Start')) { throw 'No local UAT profile. Run Prepare or Start first.' }
    if (Test-Path $folder) { throw 'Incomplete profile directory exists. Recover its matching credentials before proceeding; do not overwrite a retained database profile.' }
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    Protect-Files
    $databases = @(); $identities = @()
    foreach ($tenantId in @('T001','T002')) {
        foreach ($service in @('am','customer-identity','audit')) {
            $databases += @{ service=$service; tenant=$tenantId; name=('uat_'+$service.Replace('-','_')+'_'+$tenantId.ToLowerInvariant()); ownerPassword=(Secret); runtimePassword=(Secret); deliveryPassword=$(if ($service -eq 'am') { Secret } else { $null }) }
        }
        foreach ($actorId in @('A900','A100','A300')) { $identities += @{ tenantId=$tenantId; actorId=$actorId; token=(Secret) } }
    }
    $new = @{ instanceId=[Guid]::NewGuid().ToString('N'); postgresPassword=(Secret); databases=$databases; identities=$identities; tokens=@{}; port=$Port }
    foreach ($name in @('membership','context','provisioning','identityEvent','auditEvent')) { $new.tokens[$name] = Secret }
    Save-Json 'profile.json' $new
}
$state = Get-Content $profileFile -Raw | ConvertFrom-Json
if ($Action -eq 'Credentials') {
    $identity = $state.identities | Where-Object { $_.tenantId -eq $Tenant -and $_.actorId -eq $Actor }
    Write-Host "Local UAT only | Tenant $Tenant | Actor $Actor"
    Write-Output $identity.token
    return
}
if ($Action -eq 'Reset') {
    if (-not $ConfirmReset) { throw 'Reset deletes this profile UAT database volume and credentials. Repeat with -ConfirmReset only when that deletion is intended.' }
    Compose @('down','--volumes','--remove-orphans')
    Remove-Item $folder -Recurse -Force
    Write-Host 'Selected local UAT profile reset.'
    return
}
# Regenerate service-specific files from the retained operator profile, never new keys.
Save-Text 'postgres-password' $state.postgresPassword
$ports = [ordered]@{ 'customer-identity'=5101; audit=5102; consent=5110; evidence=5111; 'document-intelligence'=5112; 'financial-profile'=5113; 'financial-rules'=5114; 'search-retrieval'=5115; reporting=5116; 'integration-gateway'=5117; 'job-management'=5118; 'ai-intelligence'=5119 }
$am = @{ AllowedHosts='localhost;127.0.0.1'; Logging=@{ LogLevel=@{ Default='Warning' } }; Monergy=@{ ExecutionZone='LOCAL'; AccessManagement=@{ Adapter='postgres-reference'; TrustedContext='customer-identity-reference'; CustomerIdentityUrl='http://127.0.0.1:5101/'; CustomerIdentityToken=$state.tokens.context; IdentityProvisioningToken=$state.tokens.provisioning; MembershipToken=$state.tokens.membership; TenantDatabases=@{} } } }
foreach ($db in $state.databases | Where-Object service -eq 'am') { $am.Monergy.AccessManagement.TenantDatabases[$db.tenant] = Connection $db 'runtime' }
Save-Json 'access-management.json' $am
foreach ($service in $ports.Keys) {
    $config = @{ AllowedHosts='localhost;127.0.0.1'; Logging=@{ LogLevel=@{ Default='Warning' } }; Monergy=@{ ExecutionZone='LOCAL'; TenantAccess=@{ AccessManagementUrl='http://127.0.0.1:5088/' }; TenantBoundary=@{ Enabled='true'; ProbeUrl="http://127.0.0.1:$($ports[$service])/"; CustomerTenants=@{ 'reference-customer'='T001'; 'other-customer'='T002'; C001='T001'; C002='T002' } } } }
    if ($service -in @('customer-identity','audit')) {
        $integration = @{ Enabled='true'; AccessManagementUrl='http://127.0.0.1:5088/' }
        if ($service -eq 'customer-identity') {
            $integration.MembershipToken=$state.tokens.membership; $integration.CustomerIdentityToken=$state.tokens.context
            $integration.IdentityProvisioningToken=$state.tokens.provisioning; $integration.CustomerIdentityEventToken=$state.tokens.identityEvent
            $integration.Identities=$state.identities; $integration.CustomerIdentityDatabases=@{}
            foreach ($db in $state.databases | Where-Object service -eq $service) { $integration.CustomerIdentityDatabases[$db.tenant] = Connection $db 'runtime' }
        } else {
            $integration.AuditEventToken=$state.tokens.auditEvent; $integration.AuditDatabases=@{}
            foreach ($db in $state.databases | Where-Object service -eq $service) { $integration.AuditDatabases[$db.tenant] = Connection $db 'runtime' }
        }
        $config.Monergy.AccessIntegration=$integration
    } elseif ($service -in @('evidence','financial-profile','financial-rules','search-retrieval','reporting','integration-gateway')) {
        $config.Monergy.ReferenceAdapters='true'
    }
    Save-Json "$service.json" $config
}
foreach ($tenantId in @('T001','T002')) {
    $delivery = @{}
    $database = $state.databases | Where-Object { $_.service -eq 'am' -and $_.tenant -eq $tenantId }
    $delivery["MONERGY_AM_${tenantId}_DELIVERY"] = Connection $database 'delivery'
    foreach ($destination in @('AUTHORIZATION','SESSIONS','AUDIT')) {
        $audit = $destination -eq 'AUDIT'
        $delivery["MONERGY_AM_REFERENCE_${destination}_URL"] = $(if ($audit) { 'http://127.0.0.1:5102/' } else { 'http://127.0.0.1:5101/' })
        $delivery["MONERGY_AM_REFERENCE_${destination}_TOKEN"] = $(if ($audit) { $state.tokens.auditEvent } else { $state.tokens.identityEvent })
    }
    Save-Json "delivery-$tenantId.json" $delivery
}
Save-Json 'workspace.json' @{ AllowedHosts='localhost;127.0.0.1'; Logging=@{ LogLevel=@{ Default='Warning' } }; Monergy=@{ ExecutionZone='LOCAL'; TenantAccess=@{ AccessManagementUrl='http://127.0.0.1:5088/' } } }
$runtimeUid = if ($windows) { '1654' } else { (& id -u).Trim() }
$path = $folder.Replace('\','/')
Save-Text 'compose.env' "UAT_CONFIG_ROOT='$path'`nUAT_PORT=$($state.port)`nUAT_UID=$runtimeUid`n"
Protect-Files
if ($Action -eq 'Prepare') { Write-Host "Local profile prepared. Credentials: -Action Credentials -Tenant T001 -Actor A900"; return }
if ($Action -eq 'Stop') { Compose @('down','--remove-orphans'); Write-Host 'Stopped. Tenant databases and credentials retained.'; return }
if ($Action -eq 'Status') { Compose @('ps','--all'); return }
if ($Action -eq 'Start') {
    $os = & docker info --format '{{.OSType}}'
    if ($LASTEXITCODE -ne 0 -or $os -ne 'linux') { throw 'Start Docker Desktop with Linux containers, then retry.' }
    Compose @('up','-d','--build','--remove-orphans')
}
$url = "http://127.0.0.1:$($state.port)"
$ready = $false
for ($attempt=0; $attempt -lt 90; $attempt++) {
    try { $response = Invoke-WebRequest "$url/health/ready" -UseBasicParsing -TimeoutSec 5; if ($response.StatusCode -eq 200) { $ready=$true; break } } catch { }
    Start-Sleep -Seconds 2
}
if (-not $ready) { Compose @('ps','--all'); throw 'UAT readiness timed out. Inspect docker compose service logs using this profile; do not share credentials.' }
# Fixture membership is created through AM once; subsequent starts preserve operator changes.
if ($Action -eq 'Start' -and -not (Test-Path (Join-Path $folder 'members-initialized'))) {
    foreach ($tenantId in @('T001','T002')) {
        $identity = $state.identities | Where-Object { $_.tenantId -eq $tenantId -and $_.actorId -eq 'A900' }
        $session = Invoke-RestMethod "$url/identity-api/local/v1/tenant-sessions" -Method Post -ContentType 'application/json' -Headers @{ 'X-Monergy-Reference-Authentication'=$identity.token } -Body (@{tenantId=$tenantId}|ConvertTo-Json) -TimeoutSec 15
        try {
            $headers = @{ 'X-Monergy-Tenant'=$tenantId; 'X-Monergy-Session'=$session.authenticationContextId }
            foreach ($actorId in @('A100','A300')) {
                $members = Invoke-RestMethod "$url/access-api/v1/administration/members?limit=100" -Headers $headers -TimeoutSec 15
                if (-not @($members.items | Where-Object actorId -eq $actorId).Count) {
                    Invoke-RestMethod "$url/access-api/v1/administration/members" -Method Post -ContentType 'application/json' -Headers $headers -Body (@{expectedPolicyVersion=$members.policyVersion;actorId=$actorId}|ConvertTo-Json) -TimeoutSec 15 | Out-Null
                }
            }
        } finally {
            Invoke-RestMethod "$url/identity-api/local/v1/tenant-sessions/revoke" -Method Post -ContentType 'application/json' -Body (@{tenantId=$tenantId;authenticationContextId=$session.authenticationContextId}|ConvertTo-Json) -TimeoutSec 15 | Out-Null
        }
    }
    Save-Text 'members-initialized' $state.instanceId
    Protect-Files
}
Write-Host "Local UAT is ready: $url/access"
Write-Host 'Administration, identities, sessions and access audit persist across Stop/Start.'
Write-Host 'Business reference data is illustrative and may reset. Production readiness remains blocked.'
Write-Host 'Show the administrator key: ./build/uat/Invoke-MonergyUat.ps1 -Action Credentials -Tenant T001 -Actor A900'
