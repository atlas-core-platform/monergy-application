[CmdletBinding()]
param(
    [ValidateSet('Prepare','Start','Status','Stop','Verify','Credentials','Welcome','RepairPermissions','Reset')][string]$Action = 'Start',
    [ValidatePattern('^[a-z][a-z0-9-]{0,30}$')][string]$Profile = 'default',
    [ValidateRange(1024,65535)][int]$Port = 4173,
    [ValidateSet('T001','T002')][string]$Tenant = 'T001',
    [ValidateSet('A900','A100','A200','A300')][string]$Actor = 'A900',
    [switch]$ConfirmReset,
    [switch]$EnableOnboarding,
    [switch]$EnableEmailAccess,
    [switch]$EnableCustomerRegistration,
    [switch]$EnablePersonaLogin,
    [switch]$EnablePersonaTesterActivation,
    [string]$AccountAgreementFile,
    [string]$EmailConfigurationFile,
    [string]$GoogleDriveConfigurationFile,
    [string]$OneDriveConfigurationFile,
    [string]$AiModelConfigurationFile,
    [string]$KycConfigurationFile,
    [string]$EvidencePersistenceConfigurationFile,
    [string]$FinancialPersistenceConfigurationFile,
    [switch]$RequireRetainedEvidenceAndFinancialProfile,
    [string]$ClassificationConfigurationFile,
    [switch]$RequireCustomerSetup,
    [switch]$EnableCustomerPan
)
$ErrorActionPreference = 'Stop'
if ($EnablePersonaLogin -and (-not $EnableOnboarding -or (-not $EnableEmailAccess -and -not $EnablePersonaTesterActivation))) { throw 'Persona login requires a named onboarding profile and either email activation or protected tester activation.' }
if ($EnablePersonaTesterActivation -and (-not $EnablePersonaLogin -or -not $EnableOnboarding)) { throw 'Tester activation requires -EnableOnboarding and -EnablePersonaLogin.' }
if ($EnablePersonaTesterActivation -and $EnableEmailAccess) { throw 'Select either tester activation without SMTP or the production-like email activation flow.' }
if ($EnableCustomerRegistration -and (-not $EnableEmailAccess -or -not $EnableOnboarding -or [string]::IsNullOrWhiteSpace($AccountAgreementFile))) { throw 'Customer registration requires onboarding, email access and published account agreements.' }
if ($EnableCustomerRegistration) { $RequireCustomerSetup=$true }
if ($RequireCustomerSetup -and (-not $EnableEmailAccess -or -not $EnableOnboarding)) { throw 'Required customer setup needs -EnableOnboarding and -EnableEmailAccess.' }
if ($EnableOnboarding -and $Profile -eq 'default') { throw 'Use a new named profile for onboarding, for example -Profile onboarding -EnableOnboarding. The retained default profile is not upgraded.' }
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$folder = Join-Path $repository ".artifacts/uat/$Profile"
$profileFile = Join-Path $folder 'profile.json'
$windows = $env:OS -eq 'Windows_NT'
function Secret { $bytes = New-Object byte[] 32; $rng = [Security.Cryptography.RandomNumberGenerator]::Create(); try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }; return ([BitConverter]::ToString($bytes)).Replace('-','').ToLowerInvariant() }
# Compatible with Windows PowerShell 5.1 (.NET Framework) and PowerShell 7.
function Convert-HexToBase64([string]$Hex) {
    if ($Hex -notmatch '^[0-9a-fA-F]{64}$') { throw 'Invalid UAT encryption key. Restore the matching profile; do not rotate retained encryption keys.' }
    $bytes = New-Object byte[] 32
    for ($index = 0; $index -lt 32; $index++) {
        $bytes[$index] = [Convert]::ToByte($Hex.Substring($index * 2, 2), 16)
    }
    return [Convert]::ToBase64String($bytes)
}
function Save-Text($name, $value) { [IO.File]::WriteAllText((Join-Path $folder $name), $value, (New-Object Text.UTF8Encoding($false))) }
function Save-Json($name, $value) { Save-Text $name ($value | ConvertTo-Json -Depth 30) }
function Assert-ProfileReadable {
    foreach ($file in Get-ChildItem -LiteralPath $folder -Force -File) {
        $stream = $null
        try { $stream = [IO.File]::OpenRead($file.FullName) }
        catch { throw "Cannot read local UAT file '$($file.Name)'. Run -Action RepairPermissions with the Windows account that created this profile; keep its credentials and database volume intact." }
        finally { if ($null -ne $stream) { $stream.Dispose() } }
    }
}
function Protect-Files {
    if ($windows) {
        # Use SID-based explicit ACLs on each file. Do not recursively remove inherited
        # file permissions while relying on a directory inheritance grant to restore them.
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent().User
        $system = New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
        $directory = Get-Item -LiteralPath $folder -Force
        if (-not $directory.PSIsContainer -or ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'UAT profile must be a real directory, not a link.' }
        $directoryAcl = New-Object Security.AccessControl.DirectorySecurity
        $directoryAcl.SetAccessRuleProtection($true, $false)
        $directoryAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity, 'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow'))
        $directoryAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($system, 'ReadAndExecute', 'ContainerInherit, ObjectInherit', 'None', 'Allow'))
        Set-Acl -LiteralPath $folder -AclObject $directoryAcl
        foreach ($file in Get-ChildItem -LiteralPath $folder -Force) {
            if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Unexpected directory or link in the UAT credential profile; permissions were not followed outside the profile.' }
            $fileAcl = New-Object Security.AccessControl.FileSecurity
            $fileAcl.SetAccessRuleProtection($true, $false)
            $fileAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity, 'FullControl', 'Allow'))
            $fileAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($system, 'Read', 'Allow'))
            Set-Acl -LiteralPath $file.FullName -AclObject $fileAcl
        }
    } else {
        & chmod 700 $folder
        if ($LASTEXITCODE -ne 0) { throw 'Could not restrict the local UAT profile directory.' }
        Get-ChildItem -LiteralPath $folder -File | ForEach-Object {
            & chmod 600 $_.FullName
            if ($LASTEXITCODE -ne 0) { throw 'Could not restrict a local UAT credential file.' }
        }
    }
    Assert-ProfileReadable
}
function Invoke-UatDocker([string[]]$Arguments) {
    & docker @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Docker command failed: $($Arguments[0])" }
}
function Compose([string[]]$Arguments) {
    $composeArgs = @('compose','--env-file',(Join-Path $folder 'compose.env'),'-f',(Join-Path $PSScriptRoot 'compose.yml'),'--project-name',"monergy-uat-$($state.instanceId.Substring(0,8))")
    if ($onboarding) { $composeArgs += @('--profile','document-security') }
    Invoke-UatDocker ($composeArgs + $Arguments)
}
function Connection($database, $scope) {
    $password = $database."${scope}Password"
    return "Host=postgres;Database=$($database.name);Username=$($database.name)_$scope;Password=$password;Timeout=5;Include Error Detail=false"
}
if ($Action -eq 'RepairPermissions') {
    if (-not (Test-Path -LiteralPath $folder -PathType Container)) { throw 'No existing UAT profile to repair. Run Prepare or Start first.' }
    Protect-Files
    Write-Host 'UAT profile permissions repaired and file reads verified. Credentials and file contents are unchanged.'
    return
}
# Restore access before reading an existing profile; never replace an unreadable key file.
if ($Action -in @('Prepare','Start') -and (Test-Path -LiteralPath $folder -PathType Container)) { Protect-Files }
if (-not (Test-Path $profileFile)) {
    if ($Action -notin @('Prepare','Start')) { throw 'No local UAT profile. Run Prepare or Start first.' }
    if (Test-Path $folder) { throw 'Incomplete profile directory exists. Recover its matching credentials before proceeding; do not overwrite a retained database profile.' }
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    Protect-Files
    $databases = @(); $identities = @()
    foreach ($tenantId in @('T001','T002')) {
        $ownerServices = @('am','customer-identity','audit')
        if ($EnableOnboarding) { $ownerServices += 'consent' }
        foreach ($service in $ownerServices) {
            $databases += @{ service=$service; tenant=$tenantId; name=('uat_'+$service.Replace('-','_')+'_'+$tenantId.ToLowerInvariant()); ownerPassword=(Secret); runtimePassword=(Secret); deliveryPassword=$(if ($service -eq 'am') { Secret } else { $null }) }
        }
        $localActors = @('A900','A100','A300')
        if ($EnableOnboarding) { $localActors += 'A200' }
        foreach ($actorId in $localActors) { $identities += @{ tenantId=$tenantId; actorId=$actorId; token=(Secret) } }
    }
    $new = @{ instanceId=[Guid]::NewGuid().ToString('N'); postgresPassword=(Secret); databases=$databases; identities=$identities; tokens=@{}; port=$Port; onboarding=[bool]$EnableOnboarding }
    foreach ($name in @('membership','context','provisioning','identityEvent','auditEvent','jobAuditEvent','financialAuditEvent','customerContext','consent','recipientLabel','evidenceIntake','evidenceProcessing','jobControl','documentControl')) { $new.tokens[$name] = Secret }
    Save-Json 'profile.json' $new
}
$state = Get-Content $profileFile -Raw | ConvertFrom-Json
# Legacy profiles have no onboarding flag. Keep their original mode and port.
# Required persisted identity/credential fields must never be silently regenerated.
foreach ($name in @('instanceId','postgresPassword','databases','identities','tokens','port')) {
    if ($null -eq $state -or $null -eq $state.PSObject.Properties[$name] -or $null -eq $state.$name) {
        throw "UAT profile is missing required field '$name'. Restore its matching profile; do not reset retained data."
    }
}
if ($null -ne $state.PSObject.Properties['onboarding'] -and $state.onboarding -isnot [bool]) {
    throw 'UAT profile onboarding must be a JSON boolean.'
}
foreach ($name in @('membership','context','provisioning','identityEvent','auditEvent')) {
    if ($null -eq $state.tokens.PSObject.Properties[$name] -or [string]::IsNullOrWhiteSpace([string]$state.tokens.$name)) {
        throw "UAT profile is missing retained token '$name'. Restore its matching credentials; existing tokens will not be replaced."
    }
}
$Port = [int]$state.port
$onboarding = ($null -ne $state.PSObject.Properties['onboarding']) -and [bool]$state.onboarding
if ($EnableCustomerPan -and -not $onboarding) { throw 'Customer PAN requires an onboarding profile.' }
if ($EnableEmailAccess -and -not $onboarding) { throw 'Customer email access requires an onboarding profile with verified customer ownership.' }
$emailConfiguration = $null
if ($EnableEmailAccess -and $Action -in @('Prepare','Start')) {
    if (-not [string]::IsNullOrWhiteSpace($EmailConfigurationFile)) {
        $emailFile = Get-Item -LiteralPath $EmailConfigurationFile -ErrorAction Stop
        if ($emailFile.PSIsContainer -or $emailFile.Length -gt 16384) { throw 'Invalid email configuration file.' }
        $emailConfiguration = Get-Content -LiteralPath $emailFile.FullName -Raw -Encoding utf8 | ConvertFrom-Json
        foreach ($name in @('SmtpHost','SmtpUser','SmtpPassword','From')) {
            if ($null -eq $emailConfiguration.PSObject.Properties[$name] -or [string]::IsNullOrWhiteSpace([string]$emailConfiguration.$name)) { throw "Email configuration requires $name." }
        }
    } else {
        foreach ($name in @('MONERGY_EMAIL_SMTP_HOST','MONERGY_EMAIL_SMTP_USER','MONERGY_EMAIL_SMTP_PASSWORD','MONERGY_EMAIL_FROM')) {
            if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) { throw "Customer email delivery requires $name or -EmailConfigurationFile." }
        }
        $emailConfiguration = [pscustomobject]@{ SmtpHost=$env:MONERGY_EMAIL_SMTP_HOST; SmtpUser=$env:MONERGY_EMAIL_SMTP_USER; SmtpPassword=$env:MONERGY_EMAIL_SMTP_PASSWORD; From=$env:MONERGY_EMAIL_FROM; SmtpPort=$env:MONERGY_EMAIL_SMTP_PORT }
    }
}
# Opt-in durable Evidence configuration is retained in the protected profile for subsequent starts.
$evidencePersistence = $null
$evidenceConfigPath = Join-Path $folder 'evidence-persistence.json'
if (-not [string]::IsNullOrWhiteSpace($EvidencePersistenceConfigurationFile)) { $evidenceConfigPath = $EvidencePersistenceConfigurationFile }
if (Test-Path -LiteralPath $evidenceConfigPath) {
    if (-not $onboarding) { throw 'Durable Evidence configuration requires an onboarding profile.' }
    $evidenceFile = Get-Item -LiteralPath $evidenceConfigPath -ErrorAction Stop
    if ($evidenceFile.PSIsContainer -or $evidenceFile.Length -gt 65536) { throw 'Invalid Evidence persistence configuration file.' }
    $evidencePersistence = Get-Content -LiteralPath $evidenceFile.FullName -Raw -Encoding utf8 | ConvertFrom-Json
    if ($null -eq $evidencePersistence -or $null -eq $evidencePersistence.PSObject.Properties['RuntimeConnections'] -or
        $null -eq $evidencePersistence.PSObject.Properties['S3']) { throw 'Evidence persistence requires RuntimeConnections and S3 settings.' }
    $bindings = @($evidencePersistence.RuntimeConnections.PSObject.Properties)
    if ($bindings.Count -eq 0) { throw 'Evidence persistence requires at least one tenant database.' }
    foreach ($binding in $bindings) {
        if ($binding.Name -notin @('T001','T002') -or [string]::IsNullOrWhiteSpace([string]$binding.Value)) { throw 'Invalid Evidence tenant database binding.' }
    }
    foreach ($name in @('Endpoint','Bucket','AccessKey','SecretKey')) {
        if ($null -eq $evidencePersistence.S3.PSObject.Properties[$name] -or [string]::IsNullOrWhiteSpace([string]$evidencePersistence.S3.$name)) { throw "Evidence S3 configuration requires $name." }
    }
    if ($Action -in @('Prepare','Start') -and -not $RequireRetainedEvidenceAndFinancialProfile) {
        Save-Json 'evidence-persistence.json' $evidencePersistence
        Protect-Files
    }
} elseif (-not [string]::IsNullOrWhiteSpace($EvidencePersistenceConfigurationFile)) { throw 'Evidence persistence configuration file was not found.' }
# Optional physical Financial Profile uses the existing D09 PostgreSQL
# repository and an operator-provisioned, migrated, least-privilege database.
# The default onboarding profile deliberately remains in-memory.
$financialPersistence = $null
$financialConfigPath = Join-Path $folder 'financial-persistence.json'
if (-not [string]::IsNullOrWhiteSpace($FinancialPersistenceConfigurationFile)) { $financialConfigPath = $FinancialPersistenceConfigurationFile }
if (Test-Path -LiteralPath $financialConfigPath) {
    if (-not $onboarding) { throw 'Financial Profile persistence requires an onboarding profile.' }
    $financialFile = Get-Item -LiteralPath $financialConfigPath -ErrorAction Stop
    if ($financialFile.PSIsContainer -or $financialFile.Length -gt 16384) { throw 'Invalid Financial Profile persistence file.' }
    $financialPersistence = Get-Content -LiteralPath $financialFile.FullName -Raw -Encoding utf8 | ConvertFrom-Json
    if ($null -eq $financialPersistence -or @($financialPersistence.PSObject.Properties.Name).Count -ne 1 -or
        $financialPersistence.PSObject.Properties.Name -notcontains 'RuntimeConnection' -or
        $financialPersistence.RuntimeConnection -isnot [string] -or
        [string]::IsNullOrWhiteSpace([string]$financialPersistence.RuntimeConnection)) {
        throw 'Financial Profile persistence requires only a nonempty RuntimeConnection.'
    }
    if ($Action -in @('Prepare','Start')) {
        $currentFinancial = Join-Path $folder 'financial-profile.json'
        if (Test-Path -LiteralPath $currentFinancial) {
            $priorFinancial = Get-Content -LiteralPath $currentFinancial -Raw -Encoding utf8 | ConvertFrom-Json
            if ($priorFinancial.Monergy.ReferenceAdapters -eq 'true') {
                throw 'Cannot convert a retained in-memory Financial Profile to durable storage in place. Create a new named UAT profile; temporary facts are not migrated.'
            }
        }
        if (-not $RequireRetainedEvidenceAndFinancialProfile) {
            Save-Json 'financial-persistence.json' $financialPersistence
            Protect-Files
        }
    }
} elseif (-not [string]::IsNullOrWhiteSpace($FinancialPersistenceConfigurationFile)) {
    throw 'Financial Profile persistence configuration file was not found.'
}
if ($RequireRetainedEvidenceAndFinancialProfile) {
    if ($null -eq $evidencePersistence -or $null -eq $financialPersistence) {
        throw 'Restart-retained Evidence AND Financial Profile requires both configured durable owner stores. Named Docker volumes alone do not preserve in-memory business records. Supply protected storage configurations in a new named profile; do not reset the retained profile.'
    }
    if ($Action -in @('Prepare','Start')) {
        # Do not leave a half-selected durable configuration when the caller
        # requests both stores but one validation or profile transition fails.
        Save-Json 'evidence-persistence.json' $evidencePersistence
        Save-Json 'financial-persistence.json' $financialPersistence
        Protect-Files
    }
    if ($Action -eq 'Verify') {
        foreach ($service in @('evidence','financial-profile')) {
            $configFile = Join-Path $folder "$service.json"
            if (-not (Test-Path -LiteralPath $configFile)) { throw "Missing protected $service runtime configuration." }
            $runtimeConfig = Get-Content -LiteralPath $configFile -Raw -Encoding utf8 | ConvertFrom-Json
            if ($runtimeConfig.Monergy.ReferenceAdapters -ne 'false' -or $runtimeConfig.Monergy.Persistence.Provider -ne 'POSTGRESQL_S3') {
                throw "Retained $service runtime still uses a non-durable business adapter. No restart-retention acceptance may be claimed."
            }
        }
    }
}
# Classification owner connections and explicit reviewer assignments remain operator supplied.
$classificationConfiguration = $null
$classificationConfigPath = Join-Path $folder 'classification.json'
if (-not [string]::IsNullOrWhiteSpace($ClassificationConfigurationFile)) { $classificationConfigPath = $ClassificationConfigurationFile }
if (Test-Path -LiteralPath $classificationConfigPath) {
    if (-not $onboarding) { throw 'Classification configuration requires an onboarding profile.' }
    $classificationFile = Get-Item -LiteralPath $classificationConfigPath
    if ($classificationFile.PSIsContainer -or $classificationFile.Length -gt 65536) { throw 'Invalid classification configuration file.' }
    $classificationConfiguration = Get-Content -LiteralPath $classificationFile.FullName -Raw -Encoding utf8 | ConvertFrom-Json
    foreach ($name in @('RuntimeConnections','Reviewers')) {
        if ($null -eq $classificationConfiguration -or $null -eq $classificationConfiguration.PSObject.Properties[$name] -or $null -eq $classificationConfiguration.$name) { throw "Classification configuration requires $name." }
    }
    $classificationBindings = @($classificationConfiguration.RuntimeConnections.PSObject.Properties)
    if ($classificationBindings.Count -eq 0) { throw 'Classification requires at least one tenant database.' }
    foreach ($binding in $classificationBindings) {
        if ($binding.Name -notin @('T001','T002') -or [string]::IsNullOrWhiteSpace([string]$binding.Value)) { throw 'Invalid classification tenant database binding.' }
    }
    foreach ($binding in $classificationConfiguration.Reviewers.PSObject.Properties) {
        if ($binding.Name -notin @($classificationBindings.Name)) { throw 'Classification reviewers require a configured tenant database.' }
        foreach ($reviewer in @($binding.Value)) { if ($reviewer -isnot [string] -or $reviewer -notmatch '^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$') { throw 'Invalid classification reviewer assignment.' } }
    }
    if ($Action -in @('Prepare','Start')) { Save-Json 'classification.json' $classificationConfiguration; Protect-Files }
} elseif (-not [string]::IsNullOrWhiteSpace($ClassificationConfigurationFile)) { throw 'Classification configuration file was not found.' }
$kycTenants = @{}
if (-not [string]::IsNullOrWhiteSpace($KycConfigurationFile)) {
    if (-not $onboarding) { throw 'Basic KYC configuration requires an onboarding profile.' }
    $kycFile = Get-Item -LiteralPath $KycConfigurationFile -ErrorAction Stop
    if ($kycFile.PSIsContainer -or $kycFile.Length -gt 65536) { throw 'Invalid Basic KYC configuration file.' }
    $suppliedKyc = Get-Content -LiteralPath $kycFile.FullName -Raw -Encoding utf8 | ConvertFrom-Json
    foreach ($entry in $suppliedKyc.PSObject.Properties) {
        if ($entry.Name -notin @('T001','T002')) { throw 'Basic KYC has an unsupported tenant binding.' }
        $value = $entry.Value
        if ($null -eq $value.PSObject.Properties['Notice'] -or $null -eq $value.PSObject.Properties['Reviewers']) { throw 'Basic KYC requires published notice and named reviewer configuration.' }
        $kycTenants[$entry.Name] = @{ Enabled='true'; Notice=[string]$value.Notice; Reviewers=@($value.Reviewers) }
    }
}
$googleDriveTenants = @{}
if (-not [string]::IsNullOrWhiteSpace($GoogleDriveConfigurationFile)) {
    if (-not $onboarding) { throw 'Google Drive configuration requires an onboarding profile.' }
    $suppliedDrive = Get-Content -LiteralPath $GoogleDriveConfigurationFile -Raw | ConvertFrom-Json
    foreach ($entry in $suppliedDrive.PSObject.Properties) {
        if ($entry.Name -notin @('T001','T002')) { throw 'Google Drive configuration has an unsupported tenant binding.' }
        $value = $entry.Value
        if ([string]::IsNullOrWhiteSpace($value.ClientId) -or [string]::IsNullOrWhiteSpace($value.ClientSecret) -or
            $value.RedirectUri -ne "http://localhost:$Port/connections-api/local/v1/google-drive/callback") {
            throw 'Google Drive requires a client ID, client secret and the exact localhost callback for this UAT port.'
        }
        $googleDriveTenants[$entry.Name] = @{ Enabled='true'; ClientId=$value.ClientId; ClientSecret=$value.ClientSecret; RedirectUri=$value.RedirectUri }
    }
}
$oneDriveTenants = @{}
if (-not [string]::IsNullOrWhiteSpace($OneDriveConfigurationFile)) {
    if (-not $onboarding) { throw 'OneDrive configuration requires an onboarding profile.' }
    $suppliedDrive = Get-Content -LiteralPath $OneDriveConfigurationFile -Raw | ConvertFrom-Json
    foreach ($entry in $suppliedDrive.PSObject.Properties) {
        if ($entry.Name -notin @('T001','T002')) { throw 'OneDrive configuration has an unsupported tenant binding.' }
        $value = $entry.Value
        if ([string]::IsNullOrWhiteSpace($value.ClientId) -or [string]::IsNullOrWhiteSpace($value.ClientSecret) -or
            $value.RedirectUri -ne "http://localhost:$Port/connections-api/local/v1/onedrive/callback") {
            throw 'OneDrive requires a client ID, client secret and the exact localhost callback for this UAT port.'
        }
        $oneDriveTenants[$entry.Name] = @{ Enabled='true'; ClientId=$value.ClientId; ClientSecret=$value.ClientSecret; RedirectUri=$value.RedirectUri }
    }
}
$aiModelTenants = @{}
if (-not [string]::IsNullOrWhiteSpace($AiModelConfigurationFile)) {
    if (-not $onboarding) { throw 'AI model configuration requires an onboarding profile.' }
    $modelFile = Get-Item -LiteralPath $AiModelConfigurationFile -ErrorAction Stop
    if ($modelFile.PSIsContainer -or $modelFile.Length -gt 16384) { throw 'Invalid AI model configuration file.' }
    $suppliedModels = Get-Content -LiteralPath $modelFile.FullName -Raw -Encoding utf8 | ConvertFrom-Json
    foreach ($entry in $suppliedModels.PSObject.Properties) {
        if ($entry.Name -notin @('T001','T002')) { throw 'AI model configuration has an unsupported tenant binding.' }
        $value = $entry.Value
        foreach ($name in @('Endpoint','ApiKey','Model','ProviderLabel','Notice')) {
            if ([string]::IsNullOrWhiteSpace([string]$value.$name)) { throw "AI model configuration requires $name." }
        }
        $aiModelTenants[$entry.Name] = @{ Enabled='true'; Endpoint=$value.Endpoint; ApiKey=$value.ApiKey;
            Model=$value.Model; ProviderLabel=$value.ProviderLabel; Notice=$value.Notice }
    }
}
if ($EnableOnboarding -and -not $onboarding) { throw 'This profile predates onboarding. Create a new named profile; existing keys and tenant data will not be rewritten.' }
if ($Action -in @('Prepare','Start')) {
    $tokensChanged = $false
    $requiredTokens = @('jobAuditEvent','financialAuditEvent','productAuditEvent')
    if ($EnableEmailAccess) { $requiredTokens += 'emailAccess' }
    if ($EnablePersonaTesterActivation) { $requiredTokens += @('personaTesterAccess','personaTesterChallenge') }
    if ($EnableCustomerRegistration) { $requiredTokens += 'customerEnrollment' }
    if ($EnableCustomerPan) { $requiredTokens += 'panProtection' }
    if ($onboarding) {
        $requiredTokens += @('evidenceIntake','evidenceProcessing','jobControl','documentControl','recipientLabel','googleDriveProtection','oneDriveProtection','aiModelInternal','financialQaProtection')
    }
    foreach ($name in $requiredTokens) {
        if ($null -eq $state.tokens.PSObject.Properties[$name]) {
            $state.tokens | Add-Member -NotePropertyName $name -NotePropertyValue (Secret)
            $tokensChanged = $true
        }
    }
    if ($tokensChanged) { Save-Json 'profile.json' $state; Protect-Files }
}
if ($Action -eq 'Credentials') {
    $identity = $state.identities | Where-Object { $_.tenantId -eq $Tenant -and $_.actorId -eq $Actor }
    if ($null -eq $identity) { throw 'This actor has no key in the selected profile.' }
    $persona = switch ($Actor) {
        'A900' { 'Organization Administrator' }
        'A100' { 'Primary Customer' }
        'A200' { 'Additional Customer' }
        'A300' { 'Financial Advisor / configured reviewer' }
    }
    Write-Host "Local UAT only | Tenant $Tenant | Actor $Actor | Persona $persona"
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
# Regenerate configuration only when preparing/starting, never during read-only status or verification.
if ($Action -in @('Prepare','Start')) {
    # Retain operator keys; never generate replacements.
    Save-Text 'postgres-password' $state.postgresPassword
    $ports = [ordered]@{ 'customer-identity'=5101; audit=5102; consent=5110; evidence=5111; 'document-intelligence'=5112; 'financial-profile'=5113; 'financial-rules'=5114; 'search-retrieval'=5115; reporting=5116; 'integration-gateway'=5117; 'job-management'=5118; 'ai-intelligence'=5119 }
    $am = @{ AllowedHosts='localhost;127.0.0.1'; Logging=@{ LogLevel=@{ Default='Warning' } }; Monergy=@{ ExecutionZone='LOCAL'; AccessManagement=@{ Adapter='postgres-reference'; TrustedContext='customer-identity-reference'; CustomerIdentityUrl='http://127.0.0.1:5101/'; CustomerIdentityToken=$state.tokens.context; IdentityProvisioningToken=$state.tokens.provisioning; MembershipToken=$state.tokens.membership; TenantDatabases=@{} } } }
    foreach ($db in $state.databases | Where-Object service -eq 'am') { $am.Monergy.AccessManagement.TenantDatabases[$db.tenant] = Connection $db 'runtime' }
    if ($onboarding) {
        $am.Monergy.AccessManagement.CustomerRelationships=$true
        $am.Monergy.AccessManagement.CustomerContextToken=$state.tokens.customerContext
        $am.Monergy.AccessManagement.ConsentUrl='http://127.0.0.1:5110/'
        $am.Monergy.AccessManagement.ConsentToken=$state.tokens.consent
    }
    if ($EnableCustomerRegistration) { $am.Monergy.AccessManagement.CustomerEnrollmentToken=$state.tokens.customerEnrollment }
    Save-Json 'access-management.json' $am
    foreach ($service in $ports.Keys) {
        $config = @{ AllowedHosts='localhost;127.0.0.1'; Logging=@{ LogLevel=@{ Default='Warning' } }; Monergy=@{ ExecutionZone='LOCAL'; TenantAccess=@{ AccessManagementUrl='http://127.0.0.1:5088/' }; TenantBoundary=@{ Enabled='true'; ProbeUrl="http://127.0.0.1:$($ports[$service])/"; CustomerTenants=@{ 'reference-customer'='T001'; 'other-customer'='T002'; C001='T001'; C002='T002' } } } }
        if ($onboarding) { $config.Monergy.TenantBoundary.CustomerRelationships='true' }
        if ($service -in @('customer-identity','audit') -or ($onboarding -and $service -eq 'consent')) {
            $integration = @{ Enabled='true'; AccessManagementUrl='http://127.0.0.1:5088/' }
            if ($onboarding -and $service -in @('customer-identity','consent')) {
                $integration.CustomerRelationships='true'; $integration.CustomerContextToken=$state.tokens.customerContext
                $integration.CustomerIdentityUrl='http://127.0.0.1:5101/'; $integration.CustomerIdentityToken=$state.tokens.context
                $integration.MembershipToken=$state.tokens.membership; $integration.ConsentToken=$state.tokens.consent
                $integration.RecipientLabelToken=$state.tokens.recipientLabel
            }
            if ($service -eq 'customer-identity') {
                $integration.MembershipToken=$state.tokens.membership; $integration.CustomerIdentityToken=$state.tokens.context
                $integration.IdentityProvisioningToken=$state.tokens.provisioning; $integration.CustomerIdentityEventToken=$state.tokens.identityEvent
                $integration.Identities=$state.identities; $integration.CustomerIdentityDatabases=@{}
                $integration.CustomerResources=@(
                    @{ TenantId='T001'; ResourceId='reference-customer'; DisplayName='Reference Customer'; SecondaryLabel='Primary UAT customer' },
                    @{ TenantId='T001'; ResourceId='C001'; DisplayName='Sample Customer One'; SecondaryLabel='Additional UAT customer' },
                    @{ TenantId='T002'; ResourceId='other-customer'; DisplayName='Other Customer'; SecondaryLabel='Primary UAT customer' },
                    @{ TenantId='T002'; ResourceId='C002'; DisplayName='Sample Customer Two'; SecondaryLabel='Additional UAT customer' }
                )
                foreach ($db in $state.databases | Where-Object service -eq $service) { $integration.CustomerIdentityDatabases[$db.tenant] = Connection $db 'runtime' }
                $config.Monergy.CustomerPan = @{ Enabled=[bool]$EnableCustomerPan }
                if ($EnableCustomerPan) {
                    $config.Monergy.CustomerPan.CurrentKeyId = 'local-v1'
                    $config.Monergy.CustomerPan.Keys = @{ 'local-v1'=(Convert-HexToBase64 ([string]$state.tokens.panProtection)) }
                }
                $config.Monergy.CustomerWorkspaceSetup = @{ Required=[bool]$RequireCustomerSetup }
                $config.Monergy.BasicKyc = @{ EvidenceBaseUrl='http://127.0.0.1:5111/'; Tenants=$kycTenants }
                if (-not [string]::IsNullOrWhiteSpace($AccountAgreementFile)) {
                    $publicationFile = Get-Item -LiteralPath $AccountAgreementFile -ErrorAction Stop
                    if ($publicationFile.PSIsContainer -or $publicationFile.Length -gt 1048576) { throw 'Invalid account-agreement publication file.' }
                    $publication = Get-Content -LiteralPath $publicationFile.FullName -Raw -Encoding utf8 | ConvertFrom-Json
                    if ($null -eq $publication -or @($publication.PSObject.Properties.Name).Count -ne 1 -or
                        $publication.PSObject.Properties.Name -notcontains 'Publications') { throw 'Account agreements require a Publications object.' }
                    # Owner validates tenant, kinds, exact body hashes and publication
                    # revision at startup; no fabricated content or acceptance is seeded.
                    $config.Monergy.AccountAgreements = $publication
                }
                if ($EnableCustomerRegistration) { $config.Monergy.CustomerRegistration=@{ Enabled='true'; EnrollmentToken=$state.tokens.customerEnrollment } }
                if ($EnablePersonaLogin) {
                    $config.Monergy.PersonaCredentials=@{ Enabled='true' }
                    if ($EnablePersonaTesterActivation) {
                        $config.Monergy.PersonaCredentials.TesterActivation=@{
                            Enabled='true'
                            AccessSecret=(Convert-HexToBase64 ([string]$state.tokens.personaTesterAccess))
                            ChallengeSecret=(Convert-HexToBase64 ([string]$state.tokens.personaTesterChallenge))
                        }
                    }
                }
                if ($EnableEmailAccess) {
                    $smtpPort = 587
                    if ($null -ne $emailConfiguration.PSObject.Properties['SmtpPort'] -and -not [string]::IsNullOrWhiteSpace([string]$emailConfiguration.SmtpPort)) { $smtpPort = [int]$emailConfiguration.SmtpPort }
                    $config.Monergy.CustomerEmailAccess = @{
                        Enabled='true'; SmtpHost=$emailConfiguration.SmtpHost; SmtpPort=$smtpPort
                        SmtpUser=$emailConfiguration.SmtpUser; SmtpPassword=$emailConfiguration.SmtpPassword
                        From=$emailConfiguration.From; Secret=(Convert-HexToBase64 ([string]$state.tokens.emailAccess))
                    }
                }
            } elseif ($service -eq 'consent') {
                $integration.ConsentDatabases=@{}
                foreach ($db in $state.databases | Where-Object service -eq 'consent') { $integration.ConsentDatabases[$db.tenant] = Connection $db 'runtime' }
            } else {
                $integration.AuditEventToken=$state.tokens.auditEvent; $integration.JobAuditEventToken=$state.tokens.jobAuditEvent; $integration.FinancialAuditEventToken=$state.tokens.financialAuditEvent; $integration.ProductAuditEventToken=$state.tokens.productAuditEvent; $integration.AuditDatabases=@{}
                foreach ($db in $state.databases | Where-Object service -eq $service) { $integration.AuditDatabases[$db.tenant] = Connection $db 'runtime' }
            }
            $config.Monergy.AccessIntegration=$integration
        } elseif ($service -in @('evidence','document-intelligence','financial-profile','financial-rules','search-retrieval','reporting','integration-gateway','job-management')) {
            $config.Monergy.ReferenceAdapters='true'
            if ($onboarding -and $service -eq 'integration-gateway') {
                $config.Monergy.ReferenceAdapters='false'
                $config.Monergy.AccessIntegration=@{ Enabled='true'; CustomerRelationships='true';
                    AccessManagementUrl='http://127.0.0.1:5088/'; CustomerIdentityUrl='http://127.0.0.1:5101/';
                    CustomerIdentityToken=$state.tokens.context; CustomerContextToken=$state.tokens.customerContext; MembershipToken=$state.tokens.membership }
                $config.Monergy.GoogleDrive=@{ Enabled='true'; StoreDirectory='/var/lib/monergy-drive/connections';
                    EncryptionKey=(Convert-HexToBase64 ([string]$state.tokens.googleDriveProtection));
                    EvidenceUrl='http://127.0.0.1:5111/'; EvidenceToken=$state.tokens.evidenceIntake; Tenants=$googleDriveTenants }
                $config.Monergy.OneDrive=@{ Enabled='true'; StoreDirectory='/var/lib/monergy-drive/onedrive-connections';
                    EncryptionKey=(Convert-HexToBase64 ([string]$state.tokens.oneDriveProtection));
                    EvidenceUrl='http://127.0.0.1:5111/'; EvidenceToken=$state.tokens.evidenceIntake; Tenants=$oneDriveTenants }
            }
            if ($onboarding -and $service -eq 'evidence') {
                $config.Monergy.OnboardingFixtures='true'
                if ($null -ne $evidencePersistence) {
                    $config.Monergy.ReferenceAdapters='false'
                    $config.Monergy.OnboardingFixtures='false'
                    $config.Monergy.Persistence=@{ Provider='POSTGRESQL_S3'; Evidence=$evidencePersistence }
                }
                $config.Monergy.ProductIntake=@{ Enabled='true'; EvidenceToken=$state.tokens.evidenceIntake; ProcessingToken=$state.tokens.evidenceProcessing; ClamdPort=3310 }
            } elseif ($onboarding -and $service -eq 'document-intelligence') {
                if ($null -ne $classificationConfiguration) { $config.Monergy.Classification=$classificationConfiguration }
                $config.Monergy.DocumentProcessing=@{
                    EvidenceUrl='http://127.0.0.1:5111/'; EvidenceToken=$state.tokens.evidenceProcessing
                    ControlToken=$state.tokens.documentControl
                    OcrExecutable='/opt/monergy-ocr/tesseract'; OcrDataDirectory='/opt/monergy-ocr/tessdata'; OcrLibraryDirectory='/opt/monergy-ocr/lib'
                    PdfInfoExecutable='/opt/monergy-ocr/pdfinfo'; PdfRenderExecutable='/opt/monergy-ocr/pdftoppm'
                }
            } elseif ($onboarding -and $service -eq 'financial-profile') {
                if ($null -ne $financialPersistence) {
                    $config.Monergy.ReferenceAdapters='false'
                    $config.Monergy.Persistence=@{ Provider='POSTGRESQL_S3'; FinancialProfile=@{ RuntimeConnection=[string]$financialPersistence.RuntimeConnection } }
                }
                $config.Monergy.AuditDelivery=@{
                    Enabled='true'; Url='http://127.0.0.1:5102/'; Token=$state.tokens.financialAuditEvent
                }
            } elseif ($onboarding -and $service -eq 'financial-rules') {
                $config.Monergy.NetWorth=@{ Enabled='true'; FinancialProfileUrl='http://127.0.0.1:5113/'; RuleVersion='1.0.0'; Formula='TOTAL_ASSETS_MINUS_TOTAL_LIABILITIES' }
                $config.Monergy.MonthlyCashflow=@{ Enabled='true'; FinancialProfileUrl='http://127.0.0.1:5113/'; RuleVersion='1.0.0' }
            } elseif ($onboarding -and $service -eq 'reporting') {
                $config.Monergy.ProductReporting=@{
                    Enabled='true'; FinancialProfileUrl='http://127.0.0.1:5113/'
                    FinancialRulesUrl='http://127.0.0.1:5114/'
                }
            } elseif ($onboarding -and $service -eq 'search-retrieval') {
                $config.Monergy.ProductSearch=@{
                    Enabled='true'; EvidenceUrl='http://127.0.0.1:5111/'
                    FinancialProfileUrl='http://127.0.0.1:5113/'
                }
            } elseif ($onboarding -and $service -eq 'job-management') {
                $config.Monergy.DocumentProcessing=@{
                    Enabled='true'; DocumentIntelligenceUrl='http://127.0.0.1:5112/'
                    DocumentIntelligenceToken=$state.tokens.documentControl; ControlToken=$state.tokens.jobControl
                }
                $config.Monergy.AuditDelivery=@{
                    Enabled='true'; Url='http://127.0.0.1:5102/'; Token=$state.tokens.jobAuditEvent
                }
            }
        }
        if ($onboarding -and $service -eq 'integration-gateway') {
            $config.Monergy.AiModel=@{ Enabled='true'; InternalToken=$state.tokens.aiModelInternal; Tenants=$aiModelTenants }
        }
        if ($onboarding -and $service -eq 'ai-intelligence') {
            $config.Monergy.AccessIntegration=@{ Enabled='true'; CustomerRelationships='true';
                AccessManagementUrl='http://127.0.0.1:5088/'; CustomerIdentityUrl='http://127.0.0.1:5101/';
                CustomerIdentityToken=$state.tokens.context; CustomerContextToken=$state.tokens.customerContext; MembershipToken=$state.tokens.membership }
            $config.Monergy.FinancialQa=@{ Enabled='true'; DocumentIntelligenceUrl='http://127.0.0.1:5112/'; FinancialRulesUrl='http://127.0.0.1:5114/'; IntegrationGatewayUrl='http://127.0.0.1:5117/';
                FinancialProfileUrl='http://127.0.0.1:5113/'; EvidenceUrl='http://127.0.0.1:5111/'; SearchRetrievalUrl='http://127.0.0.1:5115/';
                InternalToken=$state.tokens.aiModelInternal; StoreDirectory='/var/lib/monergy-drive/financial-qa';
                EncryptionKey=(Convert-HexToBase64 ([string]$state.tokens.financialQaProtection)) }
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
    $workspace = @{ AllowedHosts='localhost;127.0.0.1'; Logging=@{ LogLevel=@{ Default='Warning' } }; Monergy=@{ ExecutionZone='LOCAL'; TenantAccess=@{ AccessManagementUrl='http://127.0.0.1:5088/' }; Onboarding=@{ Enabled=$onboarding; RegistryPath='/var/lib/monergy-onboarding/registry.json'; SetupPath='/var/lib/monergy-onboarding/setup.json' } } }
    if ($onboarding) {
        $workspace.Monergy.AccessIntegration=@{
            Enabled='true'; CustomerRelationships='true'; AccessManagementUrl='http://127.0.0.1:5088/'
            CustomerIdentityUrl='http://127.0.0.1:5101/'; CustomerContextToken=$state.tokens.customerContext
            CustomerIdentityToken=$state.tokens.context; MembershipToken=$state.tokens.membership
        }
        $workspace.Monergy.ProductAudit=@{ Enabled='true'; Url='http://127.0.0.1:5102/'; Token=$state.tokens.productAuditEvent }
        $workspace.Monergy.Product=@{
            Enabled='true'; EvidenceUrl='http://127.0.0.1:5111/'; EvidenceToken=$state.tokens.evidenceIntake
            JobManagementUrl='http://127.0.0.1:5118/'; JobManagementToken=$state.tokens.jobControl
            FinancialProfileUrl='http://127.0.0.1:5113/'
            FinancialRulesUrl='http://127.0.0.1:5114/'
            SearchRetrievalUrl='http://127.0.0.1:5115/'
            ReportingUrl='http://127.0.0.1:5116/'
        }
    }
    Save-Json 'workspace.json' $workspace
    $runtimeUid = if ($windows) { '1654' } else { (& id -u).Trim() }
    $path = $folder.Replace('\','/')
    Save-Text 'compose.env' "UAT_CONFIG_ROOT='$path'`nUAT_PORT=$($state.port)`nUAT_UID=$runtimeUid`n"
    Protect-Files
}
if ($Action -eq 'Prepare') { Write-Host "Local profile prepared. Credentials: -Action Credentials -Tenant T001 -Actor A900"; return }
if ($Action -eq 'Stop') { Compose @('down','--remove-orphans'); Write-Host 'Stopped. Tenant databases and credentials retained.'; return }
if ($Action -eq 'Status') { Compose @('ps','--all'); return }
if ($Action -eq 'Start') {
    $os = & docker info --format '{{.OSType}}'
    if ($LASTEXITCODE -ne 0 -or $os -ne 'linux') { throw 'Start Docker Desktop with Linux containers, then retry.' }
    Compose @('config','--quiet')
    Compose @('up','-d','--build','--remove-orphans')
}
$url = "http://127.0.0.1:$($state.port)"
$ready = $false
for ($attempt=0; $attempt -lt 90; $attempt++) {
    try { $response = Invoke-WebRequest "$url/health/ready" -UseBasicParsing -TimeoutSec 5; if ($response.StatusCode -eq 200) { $ready=$true; break } } catch { }
    Start-Sleep -Seconds 2
}
if (-not $ready) { Compose @('ps','--all'); throw 'UAT readiness timed out. Inspect docker compose service logs using this profile; do not share credentials.' }
# Fixture membership is created through AM once per bootstrap revision; subsequent
# starts preserve operator changes. The revisioned marker repairs retained profiles
# whose older marker exists while one or more advertised local identities are absent.
$memberBootstrapMarker = Join-Path $folder 'reference-members-v2-initialized'
if ($Action -eq 'Start' -and -not (Test-Path $memberBootstrapMarker)) {
    foreach ($tenantId in @('T001','T002')) {
        $identity = $state.identities | Where-Object { $_.tenantId -eq $tenantId -and $_.actorId -eq 'A900' }
        $session = Invoke-RestMethod "$url/identity-api/local/v1/tenant-sessions" -Method Post -ContentType 'application/json' -Headers @{ 'X-Monergy-Reference-Authentication'=$identity.token } -Body (@{tenantId=$tenantId}|ConvertTo-Json) -TimeoutSec 15
        try {
            $headers = @{ 'X-Monergy-Tenant'=$tenantId; 'X-Monergy-Session'=$session.authenticationContextId }
            $memberActors = @('A100','A300')
            if ($onboarding) { $memberActors += 'A200' }
            foreach ($actorId in $memberActors) {
                $members = Invoke-RestMethod "$url/access-api/v1/administration/members?limit=100" -Headers $headers -TimeoutSec 15
                if (-not @($members.items | Where-Object actorId -eq $actorId).Count) {
                    Invoke-RestMethod "$url/access-api/v1/administration/members" -Method Post -ContentType 'application/json' -Headers $headers -Body (@{expectedPolicyVersion=$members.policyVersion;actorId=$actorId}|ConvertTo-Json) -TimeoutSec 15 | Out-Null
                }
            }
            $members = Invoke-RestMethod "$url/access-api/v1/administration/members?limit=100" -Headers $headers -TimeoutSec 15
            foreach ($actorId in $memberActors) {
                if (-not @($members.items | Where-Object actorId -eq $actorId).Count) {
                    throw "Local reference membership reconciliation failed for $tenantId/$actorId."
                }
            }
        } finally {
            Invoke-RestMethod "$url/identity-api/local/v1/tenant-sessions/revoke" -Method Post -ContentType 'application/json' -Body (@{tenantId=$tenantId;authenticationContextId=$session.authenticationContextId}|ConvertTo-Json) -TimeoutSec 15 | Out-Null
        }
    }
    Save-Text 'reference-members-v2-initialized' $state.instanceId
    Protect-Files
}
if ($Action -eq 'Start' -and $onboarding -and -not (Test-Path (Join-Path $folder 'app03-processing-policy-initialized'))) {
    foreach ($tenantId in @('T001','T002')) {
        $identity = $state.identities | Where-Object { $_.tenantId -eq $tenantId -and $_.actorId -eq 'A900' }
        $session = Invoke-RestMethod "$url/identity-api/local/v1/tenant-sessions" -Method Post -ContentType 'application/json' -Headers @{ 'X-Monergy-Reference-Authentication'=$identity.token } -Body (@{tenantId=$tenantId}|ConvertTo-Json) -TimeoutSec 15
        try {
            $headers = @{ 'X-Monergy-Tenant'=$tenantId; 'X-Monergy-Session'=$session.authenticationContextId }
            $pack = Invoke-RestMethod "$url/access-api/v1/administration/default-access-pack" -Headers $headers -TimeoutSec 15
            if ($pack.installedVersion -notin @('2.0','2.1','2.2','2.3','2.4','2.5')) {
                throw 'The Access Management owner has not confirmed a supported Default Access Pack.'
            }

            $roles = Invoke-RestMethod "$url/access-api/v1/administration/roles?limit=100" -Headers $headers -TimeoutSec 15
            $customerRole = $roles.items | Where-Object code -eq 'customer'
            $advisorRole = $roles.items | Where-Object code -eq 'financial-advisor'
            if ($null -eq $customerRole -or $null -eq $advisorRole) { throw 'The owner-confirmed Default Access Pack role set is incomplete.' }

            foreach ($binding in @(@{ actor='A100'; role=$customerRole.id },@{ actor='A200'; role=$customerRole.id },@{ actor='A300'; role=$advisorRole.id })) {
                $members = Invoke-RestMethod "$url/access-api/v1/administration/members?limit=100" -Headers $headers -TimeoutSec 15
                $member = $members.items | Where-Object actorId -eq $binding.actor
                if ($null -ne $member -and $null -eq $member.businessRoleId) {
                    Invoke-RestMethod "$url/access-api/v1/administration/members/$($binding.actor)" -Method Put -ContentType 'application/json' -Headers $headers -Body (@{expectedPolicyVersion=$members.policyVersion;businessRoleId=$binding.role;active=$true;tenantAdmin=$false}|ConvertTo-Json) -TimeoutSec 15 | Out-Null
                }
            }
        } finally {
            Invoke-RestMethod "$url/identity-api/local/v1/tenant-sessions/revoke" -Method Post -ContentType 'application/json' -Body (@{tenantId=$tenantId;authenticationContextId=$session.authenticationContextId}|ConvertTo-Json) -TimeoutSec 15 | Out-Null
        }
    }
    Save-Text 'app03-processing-policy-initialized' $state.instanceId
    Protect-Files
}
Write-Host "Local customer workspace is ready: $url/"
Write-Host "Local access administration is ready: $url/access"
Write-Host 'Administration, identities, sessions and access audit persist across Stop/Start.'
Write-Host 'Business reference data is illustrative and may reset. Production readiness remains blocked.'
Write-Host "Show the administrator key: ./build/uat/Invoke-MonergyUat.ps1 -Action Credentials -Profile $Profile -Tenant T001 -Actor A900"
if ($onboarding) { Write-Host "Show the customer key: ./build/uat/Invoke-MonergyUat.ps1 -Action Credentials -Profile $Profile -Tenant T001 -Actor A100" }
if ($Action -eq 'Welcome') {
    foreach ($name in @('personaTesterAccess','personaTesterChallenge')) {
        if ($null -eq $state.tokens.PSObject.Properties[$name] -or [string]::IsNullOrWhiteSpace([string]$state.tokens.$name)) {
            throw 'This profile does not contain protected tester activation credentials.'
        }
    }
    $email = $Actor.ToLowerInvariant() + '@example.test'
    $returnPath = switch ($Actor) { 'A900' { '/access' } 'A300' { '/advisor' } default { '/' } }
    $request = @{ tenantId=$Tenant; email=$email; returnPath=$returnPath; requestId=[guid]::NewGuid().ToString('N') } | ConvertTo-Json -Compress
    $headers = @{ 'X-Monergy-Tester-Activation'=(Convert-HexToBase64 ([string]$state.tokens.personaTesterAccess)) }
    $welcome = Invoke-RestMethod "$url/identity-api/local/v1/persona-credentials/tester/welcome-link" -Method Post -ContentType 'application/json' -Headers $headers -Body $request -TimeoutSec 15
    if ($welcome.welcomePath -isnot [string] -or $welcome.welcomePath -notmatch '^/welcome#tenant=[A-Za-z0-9_.:-]+&ticket=[0-9a-f]{64}&return=%2F') {
        throw 'The identity owner did not return a valid protected welcome path.'
    }
    $target = $url + $welcome.welcomePath
    if ($windows) { Start-Process $target }
    else { & xdg-open $target | Out-Null }
    Write-Host "Opened a single-use five-minute welcome page for $Tenant/$Actor. The activation credential was not printed."
    return
}
if ($EnablePersonaTesterActivation) { Write-Host "Open an invited user's protected welcome page: ./build/uat/Invoke-MonergyUat.ps1 -Action Welcome -Profile $Profile -Tenant T001 -Actor A300" }
