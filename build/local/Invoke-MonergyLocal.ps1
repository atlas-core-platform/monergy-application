[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Prepare', 'Start', 'Seed', 'Status', 'Verify', 'Stop')]
    [string]$Action,
    [ValidateSet('persisted-reporting')]
    [string]$Profile = 'persisted-reporting',
    [switch]$Json,
    [string]$RepositoryRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Net.Http
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
}
$stateRoot = Join-Path $RepositoryRoot '.artifacts/d11/persisted-reporting'
$secretPath = Join-Path $stateRoot 'secrets.json'
$preparedPath = Join-Path $stateRoot 'prepared.json'
$processPath = Join-Path $stateRoot 'processes.json'
$scenarioPath = Join-Path $stateRoot 'scenario-state.json'
$verificationPath = Join-Path $stateRoot 'verification-latest.json'
$lockPath = Join-Path $stateRoot 'controller.lock'
$composePath = Join-Path $RepositoryRoot 'build/local/compose.persisted-reporting.yml'
$fixturePath = Join-Path $RepositoryRoot 'build/local/fixtures/persisted-reporting.synthetic.json'
$profilePath = Join-Path $RepositoryRoot 'build/local/persisted-reporting.profile.json'
$ports = [ordered]@{ customerWeb = 5173; reporting = 5189; evidence = 5190; financialProfile = 5191; financialRules = 5192; audit = 5193; postgres = 55434; s3 = 58334 }
$runningOnWindows = $env:OS -eq 'Windows_NT'
Import-Module (Join-Path $RepositoryRoot 'build/local/Monergy.LocalProcess.psm1') -Force

function Initialize-RestrictedDirectory([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { New-Item -ItemType Directory -Path $Path | Out-Null }
    if ($runningOnWindows) {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
        $acl = Get-Acl -LiteralPath $Path
        $acl.SetAccessRuleProtection($true, $false)
        $acl.SetOwner([Security.Principal.NTAccount]::new($identity))
        $acl.SetAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $identity, [Security.AccessControl.FileSystemRights]::FullControl,
            [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit',
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow))
        Set-Acl -LiteralPath $Path -AclObject $acl
    }
    else {
        & chmod 700 -- $Path
        if ($LASTEXITCODE -ne 0) { throw "Unable to restrict D11 state directory '$Path'." }
    }
}

function Write-Result([object]$Value) {
    if ($Json) { $Value | ConvertTo-Json -Depth 12 -Compress }
    else { $Value | Format-List | Out-String | Write-Output }
}

function New-Secret {
    $bytes = [byte[]]::new(32)
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($bytes) }
    finally { $generator.Dispose() }
    return ([BitConverter]::ToString($bytes)).Replace('-', '').ToLowerInvariant()
}

function Protect-SecretFile([string]$Path) {
    if ($runningOnWindows) {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
        $acl = Get-Acl -LiteralPath $Path
        $acl.SetAccessRuleProtection($true, $false)
        $rule = [Security.AccessControl.FileSystemAccessRule]::new(
            $identity, [Security.AccessControl.FileSystemRights]::FullControl,
            [Security.AccessControl.AccessControlType]::Allow)
        $acl.SetAccessRule($rule)
        Set-Acl -LiteralPath $Path -AclObject $acl
    }
    else {
        & chmod 600 -- $Path
        if ($LASTEXITCODE -ne 0) { throw "Unable to restrict D11 secret file '$Path'." }
    }
}

function Make-ContainerSecretReadable([string]$Path) {
    if ($runningOnWindows) { return }
    & chmod 644 -- $Path
    if ($LASTEXITCODE -ne 0) { throw "Unable to make D11 container secret '$Path' readable by the non-root provider." }
}

function Save-Json([string]$Path, [object]$Value, [switch]$Secret) {
    $parent = Split-Path -Parent $Path
    if ($Secret) { Initialize-RestrictedDirectory $parent }
    elseif (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent | Out-Null }
    $temporary = Join-Path $parent ".$([IO.Path]::GetFileName($Path)).$([Guid]::NewGuid().ToString('N')).tmp"
    [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth 15), [Text.UTF8Encoding]::new($false))
    if ($Secret) { Protect-SecretFile $temporary }
    Move-Item -LiteralPath $temporary -Destination $Path -Force
    if ($Secret) { Protect-SecretFile $Path }
}

function Read-Json([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { throw "Required D11 state '$Path' is unavailable." }
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Get-Dotnet {
    $local = Join-Path $RepositoryRoot '.toolcache/dotnet/dotnet.exe'
    if (Test-Path -LiteralPath $local) { return $local }
    return (Get-Command dotnet -ErrorAction Stop).Source
}

function Get-Pnpm {
    $local = Join-Path $RepositoryRoot '.toolcache/node-v24.21.0-win-x64/node_modules/corepack/shims/pnpm.cmd'
    if (Test-Path -LiteralPath $local) { return $local }
    return (Get-Command pnpm -ErrorAction Stop).Source
}

function Get-Node {
    $local = Join-Path $RepositoryRoot '.toolcache/node-v24.21.0-win-x64/node.exe'
    if (Test-Path -LiteralPath $local) { return $local }
    return (Get-Command node -ErrorAction Stop).Source
}

function Test-Port([int]$Port) {
    $client = [Net.Sockets.TcpClient]::new()
    try {
        $task = $client.ConnectAsync('127.0.0.1', $Port)
        return $task.Wait(250) -and $client.Connected
    }
    catch { return $false }
    finally { $client.Dispose() }
}

function Wait-Http([string]$Uri, [int]$Seconds = 45) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri $Uri -TimeoutSec 2 -UseBasicParsing
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 300) { return }
        }
        catch { }
        Start-Sleep -Milliseconds 500
    }
    throw "Runtime dependency '$Uri' did not become ready within $Seconds seconds."
}

function Wait-TcpPort([int]$Port, [int]$Seconds = 45, [int]$ConsecutiveConnections = 3) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    $consecutive = 0
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if (Test-Port $Port) {
            $consecutive++
            if ($consecutive -ge $ConsecutiveConnections) { return }
        }
        else { $consecutive = 0 }
        Start-Sleep -Milliseconds 500
    }
    throw "Runtime TCP port '$Port' did not accept $ConsecutiveConnections consecutive loopback connections within $Seconds seconds."
}

function New-ComposeEnvironment([object]$State) {
    $environment = @{
        'D11_POSTGRES_ADMIN_USER' = [string]$State.adminUser
        'D11_POSTGRES_ADMIN_PASSWORD' = [string]$State.adminPassword
    }
    foreach ($service in @('evidence', 'financial_profile', 'financial_rules', 'reporting', 'audit')) {
        $upper = $service.ToUpperInvariant()
        $environment["MONERGY_${upper}_OWNER_PASSWORD"] = [string]$State.services.$service.ownerPassword
        $environment["MONERGY_${upper}_RUNTIME_PASSWORD"] = [string]$State.services.$service.runtimePassword
    }
    $s3ConfigPath = Join-Path $stateRoot 'seaweed-s3.json'
    $s3Config = @{ identities = @(@{ name = 'd11-persisted-reporting'; credentials = @(@{
        accessKey = $State.s3AccessKey; secretKey = $State.s3SecretKey }); actions = @('Admin', 'Read', 'List', 'Write') }) }
    Save-Json $s3ConfigPath $s3Config -Secret
    Make-ContainerSecretReadable $s3ConfigPath
    $environment['D11_S3_CONFIG_PATH'] = $s3ConfigPath.Replace('\', '/')
    return $environment
}

function Invoke-Tool([string]$FilePath, [string[]]$Arguments, [hashtable]$Environment = @{}, [switch]$AllowFailure) {
    $result = Invoke-ControlledProcess $FilePath $Arguments $RepositoryRoot $Environment -AllowFailure:$AllowFailure
    if (-not [string]::IsNullOrWhiteSpace($result.StandardOutput)) { Write-Output $result.StandardOutput.TrimEnd() }
    if (-not [string]::IsNullOrWhiteSpace($result.StandardError)) { Write-Warning $result.StandardError.TrimEnd() }
    return $result
}

function Invoke-Compose([string[]]$Arguments, [object]$State, [switch]$AllowFailure) {
    $docker = (Get-Command docker -ErrorAction Stop).Source
    return Invoke-Tool $docker (@('compose','-f',$composePath) + $Arguments) (New-ComposeEnvironment $State) -AllowFailure:$AllowFailure
}

function New-RuntimeSecrets {
    $state = [ordered]@{
        profile = $Profile
        adminUser = 'd11_admin'
        adminPassword = New-Secret
        s3AccessKey = "d11$((New-Secret).Substring(0, 20))"
        s3SecretKey = New-Secret
        localSetupToken = New-Secret
        localTransportToken = New-Secret
        localControllerToken = New-Secret
        services = [ordered]@{}
    }
    foreach ($service in @('evidence', 'financial_profile', 'financial_rules', 'reporting', 'audit')) {
        $state.services[$service] = [ordered]@{
            database = "monergy_$service"
            owner = "monergy_${service}_owner"
            ownerPassword = New-Secret
            runtime = "monergy_${service}_runtime"
            runtimePassword = New-Secret
        }
    }
    Save-Json $secretPath $state -Secret
    return Read-Json $secretPath
}

function Get-RuntimeSecrets {
    $volumeState = @(Get-ExpectedVolumeState)
    $retained = Resolve-D11RetainedState -SecretsPresent:(Test-Path -LiteralPath $secretPath) -Volumes @{
        postgres = [bool](@($volumeState | Where-Object name -eq 'monergy-d11-persisted-reporting_d11-postgres')[0].exists)
        seaweed = [bool](@($volumeState | Where-Object name -eq 'monergy-d11-persisted-reporting_d11-seaweed')[0].exists)
    }
    if ($retained -eq 'BLOCKED_RECOVERY_REQUIRED') {
        throw 'BLOCKED_RECOVERY_REQUIRED: D11 secrets, PostgreSQL volume and SeaweedFS volume are a single retained-state set; refusing credential regeneration or implicit reprovisioning.'
    }
    if ($retained -eq 'CLEAN_FIRST_RUN') { return New-RuntimeSecrets }
    $existing = Read-Json $secretPath
    foreach ($property in @('adminPassword','s3AccessKey','s3SecretKey','localSetupToken','localTransportToken','localControllerToken')) {
        if (-not ($existing.PSObject.Properties.Name -contains $property) -or
            [string]::IsNullOrWhiteSpace([string]$existing.$property)) {
            throw "BLOCKED_RECOVERY_REQUIRED: retained D11 secret state is missing '$property'; refusing to regenerate credentials over retained data."
        }
    }
    if (-not (Test-D11RetainedServiceCredentials -State $existing)) {
        throw 'BLOCKED_RECOVERY_REQUIRED: retained D11 per-service credential state is incomplete or invalid; refusing to regenerate credentials over retained data.'
    }
    return $existing
}

function Get-FileIdentity([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    return [ordered]@{ path = $Path.Substring($RepositoryRoot.Length + 1).Replace('\','/'); bytes = (Get-Item -LiteralPath $Path).Length; sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
}

function Get-SourceIdentity {
    return Get-CanonicalDirtySourceIdentity -RepositoryRoot $RepositoryRoot
}

function Get-BuildIdentity {
    $paths = @(
        'services/evidence/bin/Release/net10.0/Monergy.Services.Evidence.dll',
        'services/financial-profile/bin/Release/net10.0/Monergy.Services.FinancialProfile.dll',
        'services/financial-rules/bin/Release/net10.0/Monergy.Services.FinancialRules.dll',
        'services/reporting/bin/Release/net10.0/Monergy.Services.Reporting.dll',
        'build/local/Monergy.LocalAuditHost/bin/Release/net10.0/Monergy.LocalAuditHost.dll',
        'apps/customer-web/dist/index.html')
    foreach ($path in $paths) {
        if (-not (Test-Path -LiteralPath (Join-Path $RepositoryRoot $path) -PathType Leaf)) {
            throw "Prepared D11 artifact '$path' is unavailable."
        }
    }
    $identity = Get-CanonicalFileSetIdentity -RepositoryRoot $RepositoryRoot -RelativePaths $paths
    return [ordered]@{ algorithm = $identity.algorithm; definition = $identity.definition; digest = $identity.digest; artifacts = $identity.files }
}

function Connection([object]$Entry, [ValidateSet('owner', 'runtime')]$Role) {
    $user = if ($Role -eq 'owner') { $Entry.owner } else { $Entry.runtime }
    $password = if ($Role -eq 'owner') { $Entry.ownerPassword } else { $Entry.runtimePassword }
    return "Host=127.0.0.1;Port=$($ports.postgres);Database=$($Entry.database);Username=$user;Password=$password;SSL Mode=Disable"
}

function Invoke-Prepare {
    foreach ($command in @('docker', 'node')) { [void](Get-Command $command -ErrorAction Stop) }
    $dotnet = Get-Dotnet
    $pnpm = Get-Pnpm
    $node = Get-Node
    if ((Invoke-ControlledProcess $dotnet @('--version') $RepositoryRoot).StandardOutput.Trim() -cne '10.0.401') { throw '.NET SDK 10.0.401 is required.' }
    if ((Invoke-ControlledProcess $node @('--version') $RepositoryRoot).StandardOutput.Trim().TrimStart('v') -cne '24.21.0') { throw 'Node.js 24.21.0 is required.' }
    if ((Invoke-ControlledProcess $pnpm @('--version') $RepositoryRoot).StandardOutput.Trim() -cne '12.4.1') { throw 'pnpm 12.4.1 is required.' }
    $state = Get-RuntimeSecrets
    [void](Invoke-Compose @('up','-d','--wait') $state)
    Wait-TcpPort $ports.s3
    foreach ($name in @('evidence', 'financial_profile', 'financial_rules', 'reporting', 'audit')) {
        $servicePath = $name.Replace('_', '-')
        $owner = Connection $state.services.$name owner
        [void](Invoke-Tool $dotnet @('run','--project',
            (Join-Path $RepositoryRoot 'build/Monergy.DatabaseMigrator/Monergy.DatabaseMigrator.csproj'),'--',
            '--service',$servicePath,'--connection-environment','MONERGY_MIGRATION_CONNECTION',
            '--repository-root',$RepositoryRoot) @{ 'MONERGY_MIGRATION_CONNECTION' = $owner })
    }
    [void](Invoke-Tool $dotnet @('restore',(Join-Path $RepositoryRoot 'Monergy.Application.slnx'),'--locked-mode'))
    [void](Invoke-Tool $pnpm @('install','--dir',$RepositoryRoot,'--frozen-lockfile'))
    [void](Invoke-Tool $dotnet @('build',(Join-Path $RepositoryRoot 'Monergy.Application.slnx'),'--configuration','Release','--no-restore'))
    [void](Invoke-Tool $node @((Join-Path $RepositoryRoot 'shared/platform/frontend-ui/scripts/generate-css.mjs'),'--check'))
    [void](Invoke-Tool $pnpm @('--dir',(Join-Path $RepositoryRoot 'shared/platform/frontend-ui'),'exec','tsc','-p','tsconfig.json'))
    [void](Invoke-Tool $pnpm @('--dir',(Join-Path $RepositoryRoot 'apps/customer-web'),'run','build'))
    $sourceIdentity = Get-SourceIdentity
    $buildIdentity = Get-BuildIdentity
    Save-Json $preparedPath ([ordered]@{
        profile = $Profile; state = 'PREPARED'; preparedAt = [DateTimeOffset]::UtcNow
        sourceIdentity = $sourceIdentity; buildIdentity = $buildIdentity; descriptor = $profilePath
    })
    Write-Result ([ordered]@{ profile = $Profile; state = 'PREPARED'; next = 'Start'; sourceIdentity = $sourceIdentity; buildDigest = $buildIdentity.digest })
}

function New-ServiceEnvironment([object]$State, [string]$Service) {
    $environment = @{
        'ASPNETCORE_ENVIRONMENT' = 'Development'
        'Monergy__ExecutionZone' = 'LOCAL'
        'Monergy__Persistence__Provider' = 'POSTGRESQL_S3'
        'Monergy__D11__Profile' = $Profile
        'Monergy__D11__SyntheticCustomers' = 'd11-customer-a,d11-customer-b'
        'Monergy__D11__ReportingWorkloadIdentityId' = 'd11-reporting-workload'
        'Monergy__D11__ControllerToken' = $State.localControllerToken
    }
    switch ($Service) {
        'evidence' {
            $environment['ASPNETCORE_URLS'] = "http://127.0.0.1:$($ports.evidence)"
            $environment['Monergy__Persistence__Evidence__RuntimeConnection'] = Connection $State.services.evidence runtime
            $environment['Monergy__Persistence__Evidence__S3__Endpoint'] = "http://127.0.0.1:$($ports.s3)"
            $environment['Monergy__Persistence__Evidence__S3__AccessKey'] = $State.s3AccessKey
            $environment['Monergy__Persistence__Evidence__S3__SecretKey'] = $State.s3SecretKey
            $environment['Monergy__Persistence__Evidence__S3__Bucket'] = 'monergy-d11-evidence'
            $environment['Monergy__D11__LocalSetupToken'] = $State.localSetupToken
        }
        'financial-profile' {
            $environment['ASPNETCORE_URLS'] = "http://127.0.0.1:$($ports.financialProfile)"
            $environment['Monergy__Persistence__FinancialProfile__RuntimeConnection'] = Connection $State.services.financial_profile runtime
        }
        'financial-rules' {
            $environment['ASPNETCORE_URLS'] = "http://127.0.0.1:$($ports.financialRules)"
            $environment['Monergy__Persistence__FinancialRules__RuntimeConnection'] = Connection $State.services.financial_rules runtime
            $environment['Monergy__FinancialProfileBaseAddress'] = "http://127.0.0.1:$($ports.financialProfile)/"
        }
        'audit' {
            $environment['ASPNETCORE_URLS'] = "http://127.0.0.1:$($ports.audit)"
            $environment['Monergy__Persistence__Audit__RuntimeConnection'] = Connection $State.services.audit runtime
            $environment['Monergy__D11__LocalTransportToken'] = $State.localTransportToken
            $environment['Monergy__D11__AuditStallOncePath'] = (Join-Path $stateRoot 'inject-audit-stall.once')
            $environment['Monergy__D11__AuditLoseReceiptOncePath'] = (Join-Path $stateRoot 'inject-audit-lost-receipt.once')
        }
        'reporting' {
            $environment['ASPNETCORE_URLS'] = "http://127.0.0.1:$($ports.reporting)"
            $environment['Monergy__Persistence__Reporting__RuntimeConnection'] = Connection $State.services.reporting runtime
            $environment['Monergy__D11__EvidenceBaseAddress'] = "http://127.0.0.1:$($ports.evidence)/"
            $environment['Monergy__D11__FinancialProfileBaseAddress'] = "http://127.0.0.1:$($ports.financialProfile)/"
            $environment['Monergy__D11__FinancialRulesBaseAddress'] = "http://127.0.0.1:$($ports.financialRules)/"
            $environment['Monergy__D11__AuditBaseAddress'] = "http://127.0.0.1:$($ports.audit)/"
            $environment['Monergy__D11__LocalTransportToken'] = $State.localTransportToken
            $environment['Monergy__D11__ScenarioStatePath'] = $scenarioPath
        }
        'customer-web' {
            $environment = @{
                'VITE_MONERGY_REPORTING_MODE' = 'PERSISTED_REPORTING'
                'VITE_MONERGY_D11_CUSTOMER_ID' = 'd11-customer-a'
            }
        }
    }
    return $environment
}

function Start-OwnedProcess([string]$Name, [string]$FilePath, [string[]]$Arguments,
    [string]$WorkingDirectory, [hashtable]$Environment) {
    $process = Start-ControlledProcess $FilePath $Arguments $WorkingDirectory $Environment
    try {
        $actualExecutable = try { $process.Path } catch { $null }
        if ([string]::IsNullOrWhiteSpace($actualExecutable)) { $actualExecutable = $FilePath }
        return [ordered]@{
            name = $Name; pid = $process.Id; state = 'RUNNING'
            startTimeUtc = $process.StartTime.ToUniversalTime().ToString('O')
            startTimeTicks = $process.StartTime.ToUniversalTime().Ticks
            startIdentity = Get-ControlledProcessStartIdentity $process
            executable = [IO.Path]::GetFullPath($actualExecutable)
            arguments = @($Arguments)
            argumentIdentity = Get-StringSha256 ((@($Arguments) -join "`n") + "`n")
        }
    }
    finally { $process.Dispose() }
}

function Get-OwnedProcessState([object]$Entry) {
    return Get-ControlledProcessOwnership $Entry
}

function Test-OwnedProcess([object]$Entry) { return (Get-OwnedProcessState $Entry) -eq 'VERIFIED_RUNNING' }

function Get-ExpectedVolumeState {
    $docker = (Get-Command docker -ErrorAction Stop).Source
    $values = @()
    foreach ($name in @('monergy-d11-persisted-reporting_d11-postgres','monergy-d11-persisted-reporting_d11-seaweed')) {
        $result = Invoke-ControlledProcess $docker @('volume','inspect',$name,'--format','{{.Name}}') $RepositoryRoot @{} -AllowFailure
        $values += [ordered]@{ name = $name; exists = $result.ExitCode -eq 0 -and $result.StandardOutput.Trim() -eq $name }
    }
    return $values
}

function Assert-PreparedIdentity {
    $prepared = Read-Json $preparedPath
    $source = Get-SourceIdentity
    $build = Get-BuildIdentity
    if ($prepared.sourceIdentity.head -ne $source.head -or
        $prepared.sourceIdentity.dirtyDigest -ne $source.dirtyDigest -or
        [int]$prepared.sourceIdentity.dirtyFileCount -ne [int]$source.dirtyFileCount -or
        $prepared.buildIdentity.digest -ne $build.digest) {
        throw 'Prepared D11 source/build identity is stale. Run Prepare explicitly after source or artifact changes.'
    }
}

function Invoke-Start {
    if (-not (Test-Path -LiteralPath $preparedPath)) { throw 'Profile is NOT_PREPARED. Run Prepare explicitly.' }
    Assert-PreparedIdentity
    $state = Read-Json $secretPath
    $volumes = @(Get-ExpectedVolumeState)
    if (@($volumes | Where-Object { -not $_.exists }).Count -gt 0) {
        throw 'One or more prepared D11 PostgreSQL/object-store volumes are unavailable; refusing implicit reprovisioning.'
    }
    [void](Invoke-Compose @('up','-d','--wait') $state)
    Wait-TcpPort $ports.s3

    if (Test-Path -LiteralPath $processPath) {
        $existing = Read-Json $processPath
        $states = @($existing.processes | ForEach-Object { Get-OwnedProcessState $_ })
        $journalResolution = Resolve-D11StartJournalState -OwnershipStates $states
        if ($journalResolution -eq 'ALREADY_RUNNING') {
            Write-Result ([ordered]@{ profile = $Profile; state = 'RUNNING'; message = 'Owned runtime already active.' })
            return
        }
        if ($journalResolution -eq 'BLOCKED_RECOVERY_REQUIRED') {
            throw 'BLOCKED_RECOVERY_REQUIRED: a partial or unresolved D11 ownership journal exists; reconcile with Status/Stop before Start.'
        }
    }
    foreach ($port in @($ports.evidence, $ports.financialProfile, $ports.financialRules, $ports.reporting, $ports.audit, $ports.customerWeb)) {
        if (Test-Port $port) { throw "Port $port is occupied by an unowned listener; no process was terminated." }
    }

    $dotnet = Get-Dotnet
    $journal = [ordered]@{
        profile = $Profile; profileInstanceId = [Guid]::NewGuid().ToString('N'); controller = 'Invoke-MonergyLocal.ps1'
        state = 'STARTING'; updatedAt = [DateTimeOffset]::UtcNow; processes = @()
    }
    Save-Json $processPath $journal
    $definitions = @(
        @{ name = 'evidence'; dll = 'services/evidence/bin/Release/net10.0/Monergy.Services.Evidence.dll'; port = $ports.evidence },
        @{ name = 'financial-profile'; dll = 'services/financial-profile/bin/Release/net10.0/Monergy.Services.FinancialProfile.dll'; port = $ports.financialProfile },
        @{ name = 'financial-rules'; dll = 'services/financial-rules/bin/Release/net10.0/Monergy.Services.FinancialRules.dll'; port = $ports.financialRules },
        @{ name = 'audit'; dll = 'build/local/Monergy.LocalAuditHost/bin/Release/net10.0/Monergy.LocalAuditHost.dll'; port = $ports.audit },
        @{ name = 'reporting'; dll = 'services/reporting/bin/Release/net10.0/Monergy.Services.Reporting.dll'; port = $ports.reporting }
    )
    try {
        foreach ($definition in $definitions) {
            $dll = Join-Path $RepositoryRoot $definition.dll
            if (-not (Test-Path -LiteralPath $dll)) { throw "Prepared binary '$dll' is unavailable." }
            $pending = [ordered]@{ name = $definition.name; state = 'LAUNCHING'; pid = $null; executable = $dotnet; arguments = @($dll); recordedAt = [DateTimeOffset]::UtcNow }
            $journal.processes = @($journal.processes) + $pending
            $journal.updatedAt = [DateTimeOffset]::UtcNow
            Save-Json $processPath $journal
            $started = Start-OwnedProcess $definition.name $dotnet @($dll) $RepositoryRoot (New-ServiceEnvironment $state $definition.name)
            $journal.processes = @($journal.processes | Where-Object name -ne $definition.name) + $started
            $journal.updatedAt = [DateTimeOffset]::UtcNow
            Save-Json $processPath $journal
            Wait-Http "http://127.0.0.1:$($definition.port)/health/ready"
        }
        $webEnvironment = New-ServiceEnvironment $state 'customer-web'
        $node = Get-Node
        $vite = Join-Path $RepositoryRoot 'apps/customer-web/node_modules/vite/bin/vite.js'
        $webArguments = @($vite, '--host', '127.0.0.1', '--port', [string]$ports.customerWeb, '--strictPort')
        $journal.processes = @($journal.processes) + [ordered]@{ name = 'customer-web'; state = 'LAUNCHING'; pid = $null; executable = $node; arguments = $webArguments; recordedAt = [DateTimeOffset]::UtcNow }
        Save-Json $processPath $journal
        $startedWeb = Start-OwnedProcess 'customer-web' $node $webArguments (Join-Path $RepositoryRoot 'apps/customer-web') $webEnvironment
        $journal.processes = @($journal.processes | Where-Object name -ne 'customer-web') + $startedWeb
        Save-Json $processPath $journal
        Wait-Http "http://127.0.0.1:$($ports.customerWeb)/reports"
        $journal.state = 'RUNNING'
        $journal.updatedAt = [DateTimeOffset]::UtcNow
        Save-Json $processPath $journal
    }
    catch {
        $journal.state = 'START_FAILED'
        foreach ($entry in @($journal.processes)) {
            if (Test-OwnedProcess $entry) {
                Stop-Process -Id $entry.pid -Force
                $entry.state = 'FORCED_AFTER_START_FAILURE'
            }
        }
        $journal.updatedAt = [DateTimeOffset]::UtcNow
        Save-Json $processPath $journal
        throw
    }
    Write-Result ([ordered]@{
        profile = $Profile; state = 'RUNNING'; reportsUrl = "http://127.0.0.1:$($ports.customerWeb)/reports"
        serviceUrls = [ordered]@{ evidence = "http://127.0.0.1:$($ports.evidence)"; financialProfile = "http://127.0.0.1:$($ports.financialProfile)"; financialRules = "http://127.0.0.1:$($ports.financialRules)"; reporting = "http://127.0.0.1:$($ports.reporting)"; audit = "http://127.0.0.1:$($ports.audit)" }
    })
}

function New-Security([string]$CustomerId, [string]$WorkloadId = 'local-fixture-seeder', [string]$WorkloadIdentity = 'd11-local-fixture-seeder') {
    return [ordered]@{
        actor = [ordered]@{ actorId = "d11-synthetic-actor-$CustomerId"; actorType = 'SYNTHETIC_HUMAN'; authenticatedAt = '1970-01-01T00:00:00Z'; authenticationContextId = "d11-authentication-$CustomerId" }
        workload = [ordered]@{ workloadId = $WorkloadId; workloadIdentityId = $WorkloadIdentity }
        access = [ordered]@{ purpose = 'D11_PERSISTED_REPORTING'; consentReferenceId = "d11-consent-$CustomerId"; authorizationContextId = "d11-$WorkloadId-$CustomerId-authorization"; customerId = $CustomerId }
    }
}

function New-Request([string]$Name, [object]$Payload, [string]$CustomerId,
    [string]$IdempotencyKey, [string]$WorkloadId = 'local-fixture-seeder', [string]$WorkloadIdentity = 'd11-local-fixture-seeder') {
    $requestId = "d11-$($Name.ToLowerInvariant())-$CustomerId"
    return [ordered]@{
        contractName = $Name; contractVersion = '1.0.0'; requestId = $requestId
        correlationId = "d11-seed-$CustomerId"; causationId = $null
        security = New-Security $CustomerId $WorkloadId $WorkloadIdentity
        idempotencyKey = $IdempotencyKey; payload = $Payload
    }
}

function Invoke-Contract([string]$Uri, [object]$Body, [hashtable]$Headers = @{}) {
    $client = [Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromSeconds(8)
    try {
        $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, $Uri)
        try {
            foreach ($entry in $Headers.GetEnumerator()) { [void]$request.Headers.TryAddWithoutValidation($entry.Key, [string]$entry.Value) }
            $json = $Body | ConvertTo-Json -Depth 15 -Compress
            $request.Content = [Net.Http.StringContent]::new($json, [Text.Encoding]::UTF8, 'application/json')
            $wire = $client.SendAsync($request).GetAwaiter().GetResult()
            try {
                $content = $wire.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                try { $response = $content | ConvertFrom-Json }
                catch {
                    $invalid = [InvalidOperationException]::new("Contract call '$Uri' returned malformed JSON.")
                    $invalid.Data['ContractCode'] = 'contract.response.malformed'
                    $invalid.Data['StatusCode'] = [int]$wire.StatusCode
                    throw $invalid
                }
                if (-not $wire.IsSuccessStatusCode -or
                    ($response.PSObject.Properties.Name -contains 'outcome' -and $response.outcome -ne 'Success')) {
                    $code = if ($response.error.code) { $response.error.code } elseif ($response.code) { $response.code } else { 'CONTRACT_FAILURE' }
                    $category = if ($response.error.category) { [string]$response.error.category } else { 'Unknown' }
                    $failure = [InvalidOperationException]::new("Contract call '$Uri' failed with $code ($category, HTTP $([int]$wire.StatusCode)).")
                    $failure.Data['ContractCode'] = [string]$code
                    $failure.Data['ContractCategory'] = $category
                    $failure.Data['StatusCode'] = [int]$wire.StatusCode
                    throw $failure
                }
                return $response
            }
            finally { $wire.Dispose() }
        }
        finally { $request.Dispose() }
    }
    finally { $client.Dispose() }
}

function Get-ContractFailure([Management.Automation.ErrorRecord]$ErrorRecord) {
    $exception = $ErrorRecord.Exception
    while ($null -ne $exception) {
        if ($exception.Data.Contains('ContractCode')) {
            return [ordered]@{
                code=[string]$exception.Data['ContractCode'];category=[string]$exception.Data['ContractCategory'];statusCode=[int]$exception.Data['StatusCode']
            }
        }
        $exception = $exception.InnerException
    }
    return $null
}

function Invoke-Seed {
    Assert-RuntimeReady
    $state = Read-Json $secretPath
    $fixture = Read-Json $fixturePath
    $scenarios = @()
    foreach ($customer in $fixture.customers) {
        $security = New-Security $customer.customerId
        $evidenceQuery = New-Request 'GetEvidenceReference' ([ordered]@{
            documentVersionId = $customer.documentVersionId; customerId = $customer.customerId
        }) $customer.customerId $null
        $reference = $null
        try {
            $referenceResponse = Invoke-Contract "http://127.0.0.1:$($ports.evidence)/contracts/cid-022/v1" $evidenceQuery
            $reference = $referenceResponse.data
        }
        catch {
            $failure = Get-ContractFailure $_
            if ($null -eq $failure -or $failure.code -ne 'evidence.reference.not-found' -or
                $failure.category -ne 'NotFound') { throw }
            $contentBytes = [Text.Encoding]::UTF8.GetBytes([string]$customer.content)
            $createPayload = [ordered]@{
                documentId = $customer.documentId; documentVersionId = $customer.documentVersionId
                evidenceId = $customer.evidenceId; customerId = $customer.customerId
                sourceName = 'D11 synthetic fixture'; originalFileName = "$($customer.documentId).txt"
                declaredContentType = 'text/plain'; contentReference = 'staged-by-local-owner-adapter'
                contentSha256 = ('0' * 64); receivedAt = '2026-10-01T00:00:00Z'
            }
            $create = New-Request 'CreateDocumentVersion' $createPayload $customer.customerId "d11-evidence-$($customer.customerId)-v1"
            $stage = Invoke-Contract "http://127.0.0.1:$($ports.evidence)/operations/local/evidence/stage" `
                ([ordered]@{ request = $create; contentBase64 = [Convert]::ToBase64String($contentBytes) }) `
                @{ 'X-Monergy-Local-Setup' = $state.localSetupToken }
            $reference = [ordered]@{ evidenceId = $stage.registration.data.evidenceId; documentVersionId = $stage.registration.data.documentVersionId }
        }

        $sourceFacts = @($customer.facts | ForEach-Object {
            [ordered]@{
                sourceFactId = $_.sourceFactId; customerId = $customer.customerId; factType = $_.factType
                label = $_.label; candidateValue = $_.value; currency = $_.currency
                effectiveDate = '2026-09-30'; sourceLocation = "fixture/$($_.sourceFactId)"; confidence = 1.0
                evidenceId = $reference.evidenceId; documentVersionId = $reference.documentVersionId
                extractionVersion = 'd11.synthetic/1.0.0'; validationVersion = 'd11.synthetic/1.0.0'
            }
        })
        $normalize = New-Request 'NormalizeSourceFacts' ([ordered]@{
            financialProfileId = $customer.financialProfileId; customerId = $customer.customerId
            processingId = "d11-synthetic-processing-$($customer.customerId)"; facts = $sourceFacts
            normalizationVersion = 'd11.synthetic/1.0.0'; requestedAt = '2026-10-01T00:00:00Z'
        }) $customer.customerId "d11-normalize-$($customer.customerId)-v1"
        $normalized = Invoke-Contract "http://127.0.0.1:$($ports.financialProfile)/contracts/cid-031/v1" $normalize
        $profileQuery = New-Request 'GetFinancialProfile' ([ordered]@{
            financialProfileId = $customer.financialProfileId; customerId = $customer.customerId
        }) $customer.customerId $null
        $currentProfile = Invoke-Contract "http://127.0.0.1:$($ports.financialProfile)/contracts/cid-030/v1" $profileQuery
        $inputReferences = @()
        foreach ($fixtureFact in $customer.facts) {
            $currentFacts = @($currentProfile.data.facts | Where-Object { $_.label -ceq $fixtureFact.label })
            if ($currentFacts.Count -ne 1) {
                throw "Financial Profile did not return exactly one current synthetic fact labelled '$($fixtureFact.label)'."
            }
            $inputReferences += [ordered]@{ role = $fixtureFact.role; financialFactId = $currentFacts[0].financialFactId; expectedRevision = $currentFacts[0].revision }
        }
        $calculationIdentity = @($inputReferences | ForEach-Object {
            "$($_.role):$($_.financialFactId):$($_.expectedRevision)"
        }) -join '|'
        $calculationKey = "d11-calculation-$($customer.customerId)-$((Get-StringSha256 $calculationIdentity).ToLowerInvariant())"
        $calculate = New-Request 'ExecuteCalculation' ([ordered]@{
            customerId = $customer.customerId; ruleId = 'engineering.sum'; ruleVersion = '1.0.0'; inputs = $inputReferences
        }) $customer.customerId $calculationKey
        $calculation = Invoke-Contract "http://127.0.0.1:$($ports.financialRules)/contracts/cid-037/v1" $calculate
        $scenarios += [ordered]@{
            customerId = $customer.customerId; financialProfileId = $customer.financialProfileId
            financialFactIds = @($inputReferences.financialFactId)
            calculationResultId = $calculation.data.calculationResultId
            documentId = $customer.documentId; documentVersionId = $reference.documentVersionId
            evidenceId = $reference.evidenceId
        }
    }
    Save-Json $scenarioPath ([ordered]@{ profile = $Profile; customers = $scenarios })
    Write-Result ([ordered]@{ profile = $Profile; state = 'SEEDED'; classification = $fixture.classification; customers = @($scenarios.customerId); scenarioState = $scenarioPath })
}

function Assert-RuntimeReady {
    if (-not (Test-Path -LiteralPath $processPath)) { throw 'The D11 runtime is STOPPED.' }
    $processState = Read-Json $processPath
    if (@($processState.processes | Where-Object { Test-OwnedProcess $_ }).Count -ne @($processState.processes).Count) {
        throw 'The D11 runtime is DEGRADED; one or more owned processes are unavailable.'
    }
    foreach ($entry in @(
        "http://127.0.0.1:$($ports.evidence)/health/ready",
        "http://127.0.0.1:$($ports.financialProfile)/health/ready",
        "http://127.0.0.1:$($ports.financialRules)/health/ready",
        "http://127.0.0.1:$($ports.audit)/health/ready",
        "http://127.0.0.1:$($ports.reporting)/health/ready")) { Wait-Http $entry 5 }
}

function Invoke-Verify {
    Assert-RuntimeReady
    $scenario = Read-Json $scenarioPath
    $customer = $scenario.customers | Where-Object customerId -eq 'd11-customer-a'
    $key = "d11-verify-$([Guid]::NewGuid().ToString('N'))"
    $payload = [ordered]@{ customerId = $customer.customerId }
    $request = New-Request 'GenerateReport' $payload $customer.customerId $key 'customer-web' 'd11-customer-web'
    $first = Invoke-Contract "http://127.0.0.1:$($ports.reporting)/contracts/cid-051/v1" $request
    $second = Invoke-Contract "http://127.0.0.1:$($ports.reporting)/contracts/cid-051/v1" $request
    if (($first.data | ConvertTo-Json -Depth 15 -Compress) -cne ($second.data | ConvertTo-Json -Depth 15 -Compress)) {
        throw 'Same-key report replay did not return the exact committed report.'
    }
    if ($first.data.customerId -ne $customer.customerId -or @($first.data.items).Count -lt 3 -or $first.data.aiResponseTraceReference) {
        throw 'The persisted report source/provenance result is invalid.'
    }
    $calculation = @($first.data.items | Where-Object { $_.source.sourceType -eq 'FinancialCalculation' })
    if ($calculation.Count -ne 1 -or $calculation[0].source.sourceId -ne $customer.calculationResultId) {
        throw 'The report does not identify the persisted Financial Rules result.'
    }
    $get = New-Request 'GetReport' ([ordered]@{ customerId = $customer.customerId; reportId = $first.data.reportId }) `
        $customer.customerId $null 'customer-web' 'd11-customer-web'
    $retrieved = Invoke-Contract "http://127.0.0.1:$($ports.reporting)/contracts/cid-052/v1" $get
    if ($retrieved.data.export.sha256 -ne $first.data.export.sha256) { throw 'Persisted report export hash changed on retrieval.' }
    $exportAlgorithm = [Security.Cryptography.SHA256]::Create()
    try {
        $actualExportHash = ([BitConverter]::ToString($exportAlgorithm.ComputeHash(
            [Text.Encoding]::UTF8.GetBytes([string]$retrieved.data.export.content)))).Replace('-','')
    }
    finally { $exportAlgorithm.Dispose() }
    if ($actualExportHash -ne $retrieved.data.export.sha256) { throw 'Returned report bytes do not match the persisted export SHA-256.' }

    $crossCustomer = New-Request 'GenerateReport' ([ordered]@{ customerId = 'd11-customer-b' }) `
        'd11-customer-a' "d11-cross-$([Guid]::NewGuid().ToString('N'))" 'customer-web' 'd11-customer-web'
    try {
        [void](Invoke-Contract "http://127.0.0.1:$($ports.reporting)/contracts/cid-051/v1" $crossCustomer)
        throw 'Cross-customer report generation unexpectedly succeeded.'
    }
    catch {
        if ($_.Exception.Message -eq 'Cross-customer report generation unexpectedly succeeded.') { throw }
        $failure = Get-ContractFailure $_
        if ($null -eq $failure -or $failure.code -ne 'report.customer.invalid' -or
            $failure.category -ne 'AccessDenied' -or $failure.statusCode -ne 403) {
            throw 'Cross-customer verification did not produce the expected classified HTTP 403 AccessDenied result.'
        }
    }
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    do {
        $dispatch = Invoke-RestMethod "http://127.0.0.1:$($ports.reporting)/operations/local/reporting/status"
        if ($dispatch.pendingEvents -eq 0 -and $dispatch.state -eq 'DELIVERED') { break }
        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    if ($dispatch.pendingEvents -ne 0 -or $dispatch.state -ne 'DELIVERED') {
        throw "Reporting CID-053 Audit state is $($dispatch.state); delivery remains UNKNOWN/DEGRADED after the bounded recovery window."
    }
    $eventId = "report-generated-$($first.data.reportId)"
    $audit = Invoke-RestMethod -Uri "http://127.0.0.1:$($ports.audit)/operations/local/audit/events/$eventId" `
        -Headers @{ 'X-Monergy-Local-Transport' = (Read-Json $secretPath).localTransportToken } -TimeoutSec 5
    if ($audit.sourceEventId -ne $eventId -or $audit.sourceContractId -ne 'CID-053' -or
        $audit.subjectId -ne $first.data.reportId -or $audit.recipient -ne 'Audit Service LOCAL composition') {
        throw 'Audit did not return the exact event-bound evidence for the generated report.'
    }
    $evidence = [ordered]@{
        profile = $Profile; classification = 'SYNTHETIC_DEMONSTRATION'; generatedReportId = $first.data.reportId
        exportSha256 = $first.data.export.sha256; calculationResultId = $customer.calculationResultId
        sourceFinancialReferences = @($first.data.sourceFinancialReferences); evidenceReferences = @($first.data.evidenceReferences)
        financialProvenanceReferences = @($first.data.financialProvenanceReferences)
        calculationLineageReferences = @($first.data.calculationLineageReferences)
        idempotentReplay = $true; auditDelivery = 'DELIVERED'; auditEvidenceId = $audit.auditEvidenceId
        auditSourceEventId = $audit.sourceEventId; returnedExportSha256 = $actualExportHash
        verifiedAt = [DateTimeOffset]::UtcNow
    }
    Save-Json $verificationPath $evidence
    Write-Result $evidence
}

function Get-Status {
    if (-not (Test-Path -LiteralPath $preparedPath)) {
        return [ordered]@{ profile = $Profile; state = 'NOT_PREPARED' }
    }
    $volumes = @(Get-ExpectedVolumeState)
    $stores = @(
        [ordered]@{ name='postgres'; ready=Test-Port $ports.postgres },
        [ordered]@{ name='object-store'; ready=Test-Port $ports.s3 })
    if (-not (Test-Path -LiteralPath $processPath)) {
        return [ordered]@{ profile = $Profile; state = 'STOPPED'; dataPreserved = $true; providersRetained = @($stores | Where-Object ready).Count -gt 0; stores = $stores; volumes = $volumes }
    }
    $processState = Read-Json $processPath
    $owned = @($processState.processes | ForEach-Object {
        [ordered]@{ name = $_.name; pid = $_.pid; ownership = Get-OwnedProcessState $_ }
    })
    if ($processState.state -eq 'STOPPED' -and @($owned | Where-Object ownership -eq 'VERIFIED_RUNNING').Count -eq 0) {
        return [ordered]@{ profile = $Profile; state = 'STOPPED'; dataPreserved = $true; providersRetained = @($stores | Where-Object ready).Count -gt 0; processes = $owned; stores = $stores; volumes = $volumes }
    }
    $health = @()
    foreach ($definition in @(
        @{name='evidence';port=$ports.evidence},@{name='financial-profile';port=$ports.financialProfile},
        @{name='financial-rules';port=$ports.financialRules},@{name='audit';port=$ports.audit},
        @{name='reporting';port=$ports.reporting},@{name='customer-web';port=$ports.customerWeb})) {
        $uri = if ($definition.name -eq 'customer-web') { "http://127.0.0.1:$($definition.port)/reports" } else { "http://127.0.0.1:$($definition.port)/health/ready" }
        $ready = $false
        try { Wait-Http $uri 2; $ready = $true } catch { }
        $health += [ordered]@{ name=$definition.name; ready=$ready; uri=$uri }
    }
    $sourceProbe = [ordered]@{ ready=$false; code='SCENARIO_NOT_AVAILABLE' }
    if (Test-Path -LiteralPath $scenarioPath) {
        try {
            $scenario = Read-Json $scenarioPath
            $customer = @($scenario.customers | Where-Object customerId -eq 'd11-customer-a')
            if ($customer.Count -ne 1) { throw 'Scenario customer selection is invalid.' }
            $probe = New-Request 'GetFinancialProfile' ([ordered]@{ financialProfileId=$customer[0].financialProfileId; customerId=$customer[0].customerId }) $customer[0].customerId $null 'reporting' 'd11-reporting-workload'
            [void](Invoke-Contract "http://127.0.0.1:$($ports.financialProfile)/contracts/cid-030/v1" $probe)
            $sourceProbe = [ordered]@{ ready=$true; code='OWNER_CONTRACT_READY' }
        }
        catch { $sourceProbe = [ordered]@{ ready=$false; code='OWNER_CONTRACT_UNCERTAIN'; detail=$_.Exception.Message } }
    }
    $identityReady = $true
    try { Assert-PreparedIdentity } catch { $identityReady = $false }
    $allProcesses = $owned.Count -gt 0 -and @($owned | Where-Object ownership -ne 'VERIFIED_RUNNING').Count -eq 0
    $resolvedState = Resolve-D11ObservedRuntimeState -AllProcessesOwned:$allProcesses `
        -StoresReady:(@($stores | Where-Object { -not $_.ready }).Count -eq 0) `
        -VolumesReady:(@($volumes | Where-Object { -not $_.exists }).Count -eq 0) `
        -HealthReady:(@($health | Where-Object { -not $_.ready }).Count -eq 0) `
        -OwnerContractReady:$sourceProbe.ready -PreparedIdentityCurrent:$identityReady
    return [ordered]@{ profile=$Profile; state=$resolvedState; processes=$owned; stores=$stores; volumes=$volumes; health=$health; ownerContract=$sourceProbe; preparedIdentityCurrent=$identityReady; reportsUrl="http://127.0.0.1:$($ports.customerWeb)/reports"; syntheticData=(Test-Path -LiteralPath $scenarioPath) }
}

function Invoke-Stop {
    if (-not (Test-Path -LiteralPath $processPath)) {
        Write-Result ([ordered]@{ profile = $Profile; state = 'STOPPED'; message = 'No owned application runtime was recorded.' })
        return
    }
    $processState = Read-Json $processPath
    $runtimeSecrets = Read-Json $secretPath
    $servicePorts = @{ evidence=$ports.evidence; 'financial-profile'=$ports.financialProfile; 'financial-rules'=$ports.financialRules; audit=$ports.audit; reporting=$ports.reporting }
    $results = @()
    $unresolved = $false
    foreach ($entry in @($processState.processes | Sort-Object { $_.name -eq 'customer-web' } -Descending)) {
        $ownership = Get-OwnedProcessState $entry
        if ($ownership -eq 'NOT_RUNNING') { $entry.state='STOPPED'; $results += [ordered]@{name=$entry.name;result='ALREADY_STOPPED'}; continue }
        if ($ownership -ne 'VERIFIED_RUNNING') {
            $unresolved = $true
            $entry.state='UNRESOLVED'
            $results += [ordered]@{name=$entry.name;result=$ownership}
            continue
        }
        $process = Get-Process -Id $entry.pid
        $gracefulRequested = $false
        if ($servicePorts.ContainsKey([string]$entry.name)) {
            try {
                $response = Invoke-WebRequest -Method Post -Uri "http://127.0.0.1:$($servicePorts[[string]$entry.name])/operations/local/control/stop" `
                    -Headers @{'X-Monergy-Local-Control'=$runtimeSecrets.localControllerToken} -TimeoutSec 3 -UseBasicParsing
                $gracefulRequested = $response.StatusCode -eq 202
            }
            catch { $gracefulRequested = $false }
        }
        elseif ($entry.name -eq 'customer-web') { $gracefulRequested = $process.CloseMainWindow() }
        if ($gracefulRequested) { try { [void]$process.WaitForExit(5000) } catch { } }
        if (-not $process.HasExited) {
            Stop-Process -Id $entry.pid
            try { [void]$process.WaitForExit(5000) } catch { }
        }
        $forced = $false
        if (-not $process.HasExited) { Stop-Process -Id $entry.pid -Force; $forced=$true }
        $entry.state='STOPPED'
        $results += [ordered]@{name=$entry.name;result=$(if($forced){'FORCED_FALLBACK'}else{'STOPPED'});gracefulRequested=$gracefulRequested}
        $process.Dispose()
    }
    $processState.state = Resolve-D11StopJournalState -HasUnresolvedIdentity:$unresolved
    $processState.updatedAt = [DateTimeOffset]::UtcNow
    Save-Json $processPath $processState
    Write-Result ([ordered]@{ profile=$Profile; state=$processState.state; dataPreserved=$true; providersPreserved=$true; processes=$results })
    if ($unresolved) { throw 'One or more process identities were unresolved; ownership metadata was retained and no mismatched PID was stopped.' }
}

Initialize-RestrictedDirectory $stateRoot
$lock = $null
try {
    $lock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
}
catch { throw 'Another persisted-reporting controller action is active.' }
try {
    switch ($Action) {
        'Prepare' { Invoke-Prepare }
        'Start' { Invoke-Start }
        'Seed' { Invoke-Seed }
        'Status' { Write-Result (Get-Status) }
        'Verify' { Invoke-Verify }
        'Stop' { Invoke-Stop }
    }
}
finally { if ($null -ne $lock) { $lock.Dispose() } }
