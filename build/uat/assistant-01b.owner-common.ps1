#requires -Version 7.0
# Source-only helpers. Dot-sourcing performs no Docker, database, or filesystem mutation.
Set-StrictMode -Version Latest

function Assert-AssistantOwnerPath([string]$Path) {
    $itemPath = [IO.Path]::GetFullPath($Path)
    while ($itemPath) {
        if (Test-Path -LiteralPath $itemPath) {
            if ((Get-Item -LiteralPath $itemPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'Linked owner-evidence paths are forbidden.'
            }
        }
        $parent = [IO.Path]::GetDirectoryName($itemPath)
        if ($parent -eq $itemPath) { break }
        $itemPath = $parent
    }
}

function Read-AssistantOwnerProtectedJson([string]$Path) {
    try {
        Assert-AssistantOwnerPath $Path
        $entry = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
        if ($entry.PSIsContainer -or $entry.Length -gt 1048576) { throw 'invalid protected file' }
        if ($IsWindows) {
            $allowed = @([Security.Principal.WindowsIdentity]::GetCurrent().User.Value,'S-1-5-18')
            $acl = Get-Acl -LiteralPath $Path -ErrorAction Stop
            foreach ($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
                if ($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Value -notin $allowed) { throw 'unprotected file' }
            }
        } else {
            $mode = [IO.File]::GetUnixFileMode($Path)
            $untrusted = [IO.UnixFileMode]::GroupRead -bor [IO.UnixFileMode]::GroupWrite -bor [IO.UnixFileMode]::GroupExecute -bor [IO.UnixFileMode]::OtherRead -bor [IO.UnixFileMode]::OtherWrite -bor [IO.UnixFileMode]::OtherExecute
            if (($mode -band $untrusted) -ne 0) { throw 'unprotected file' }
        }
        return Get-Content -LiteralPath $Path -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    } catch {
        # ConvertFrom-Json can include source excerpts. Never let protected
        # configuration or fixture content enter direct helper diagnostics.
        throw 'Protected owner configuration or fixture is unavailable or invalid; raw details are suppressed.'
    }
}

function Invoke-AssistantOwnerProcess([string[]]$Arguments, [string]$InputText = '') {
    $start = [Diagnostics.ProcessStartInfo]::new('docker')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw 'start' }
        $output = $process.StandardOutput.ReadToEndAsync()
        $errorOutput = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.Write($InputText)
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(40000)) { $process.Kill($true); throw 'timeout' }
        $text = $output.GetAwaiter().GetResult()
        $null = $errorOutput.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0 -or $text.Length -gt 16777216) { throw 'execution' }
        return $text.Trim()
    } catch { throw 'Owner inspection command failed; raw process diagnostics are suppressed.' }
    finally { $process.Dispose() }
}

function Invoke-AssistantOwnerSql($Context, [string]$Database, [string]$Role, [string]$Password, [string]$Sql) {
    if ($Database -notmatch '^[a-z0-9_]{1,63}$' -or $Role -notmatch '^[a-z0-9_]{1,63}$' -or $Password -notmatch '^[a-fA-F0-9]{64}$') {
        throw 'Invalid protected database binding.'
    }
    # The secret travels over stdin only; neither Docker nor psql argv contains it.
    $shell = 'IFS= read -r PGPASSWORD; export PGPASSWORD; exec psql -X -qAt -v ON_ERROR_STOP=1 --host=127.0.0.1 --username="$1" --dbname="$2" --no-password'
    $boundedSql = "SET statement_timeout='15s'; SET lock_timeout='5s';`n" + $Sql
    Invoke-AssistantOwnerProcess @('exec','-i','--user','postgres',$Context.Container,'sh','-c',$shell,'sh',$Role,$Database) ($Password + "`n" + $boundedSql + "`n")
}

function Get-AssistantOwnerContext([string]$Profile, [string]$ExpectedNewInstance, [bool]$Confirmed, [switch]$AllowAssistantDisabled) {
    if (-not $Confirmed -or $Profile -notmatch '^assistant01b(?:-[a-z0-9-]+)?$' -or $Profile.Length -gt 31 -or $ExpectedNewInstance -notmatch '^[0-9a-f]{32}$') {
        throw 'Explicit Gate D authorization and the exact new isolated profile identity are required.'
    }
    $repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
    $profileFile = Join-Path $repository ".artifacts/uat/$Profile/profile.json"
    $fixtureFile = Join-Path $repository ".artifacts/assistant-01b-private-fixtures/browser-$Profile/fixture.json"
    Assert-AssistantOwnerPath $profileFile
    Assert-AssistantOwnerPath $fixtureFile
    $state = Read-AssistantOwnerProtectedJson $profileFile
    $fixture = Read-AssistantOwnerProtectedJson $fixtureFile
    # Only the ordinary Q&A continuity fixture opts into this during module-disable verification.
    # Owner fault/snapshot callers retain the enabled-only default; all store/instance checks still run.
    if ($state.instanceId -cne $ExpectedNewInstance -or $state.assistant.enabled -isnot [bool] -or
        (-not $state.assistant.enabled -and -not $AllowAssistantDisabled) -or
        $fixture.instanceId -cne $ExpectedNewInstance -or $fixture.profile -cne $Profile -or
        [IO.Path]::GetFullPath($fixture.profilePath) -cne $profileFile) { throw 'The protected new-profile fixture and profile binding differ.' }
    if ($state.postgresPassword -notmatch '^[a-fA-F0-9]{64}$') { throw 'Protected cluster credential is unavailable.' }
    $modules = @($state.assistant.databases)
    if ($modules.Count -ne 3) { throw 'Exactly three isolated Assistant databases are required.' }
    foreach ($service in @('ai-feedback','am-platform','audit-platform')) {
        $matching = @($modules | Where-Object service -CEQ $service)
        if ($matching.Count -ne 1) { throw 'Owner database binding is ambiguous.' }
        $module = $matching[0]
        $expectedName = 'uat_' + $Profile.Replace('-','_') + '_' + $service.Replace('-','_')
        if ($module.name -cne $expectedName -or $module.ownerPassword -notmatch '^[a-fA-F0-9]{64}$' -or
            $module.runtimePassword -notmatch '^[a-fA-F0-9]{64}$' -or $module.runtimePassword -ceq $module.ownerPassword) {
            throw 'Owner database credentials or profile namespace are invalid.'
        }
    }
    $project = 'monergy-uat-' + $ExpectedNewInstance.Substring(0,8)
    $ids = @( (Invoke-AssistantOwnerProcess @('ps','-q','--filter',"label=com.docker.compose.project=$project",'--filter','label=com.docker.compose.service=postgres')) -split '\r?\n' | Where-Object { $_ })
    if ($ids.Count -ne 1 -or $ids[0] -notmatch '^[a-f0-9]{12,64}$') { throw 'Exactly one running isolated PostgreSQL container is required.' }
    $metadata = Invoke-AssistantOwnerProcess @('inspect','--format','{{json .Id}}|{{index .Config.Labels "com.docker.compose.project"}}|{{index .Config.Labels "com.docker.compose.service"}}|{{.State.Running}}',$ids[0])
    $parts = $metadata.Split('|')
    if ($parts.Length -ne 4 -or $parts[1] -cne $project -or $parts[2] -cne 'postgres' -or $parts[3] -cne 'true') { throw 'Container project binding failed.' }
    $context = [pscustomobject]@{ Repository=$repository; Profile=$Profile; Instance=$ExpectedNewInstance; Container=($parts[0] | ConvertFrom-Json); State=$state; Modules=$modules }
    $marker = Invoke-AssistantOwnerSql $context 'postgres' 'postgres' $state.postgresPassword 'SELECT instance_id FROM public.monergy_uat_instance WHERE singleton;'
    if ($marker -cne $ExpectedNewInstance) { throw 'Full PostgreSQL cluster instance marker mismatch.' }
    foreach ($module in $modules) {
        $owner = $module.name + '_owner'
        $marker = Invoke-AssistantOwnerSql $context $module.name $owner $module.ownerPassword 'SELECT instance_id||'':''||service FROM public.monergy_assistant_store WHERE singleton;'
        if ($marker -cne ($ExpectedNewInstance + ':' + $module.service)) { throw 'Full owner-store instance marker mismatch.' }
    }
    return $context
}

function Get-AssistantOwnerRoleState($Context, $Module) {
    $name = $Module.name
    $role = $name + '_runtime'
    $sql = @"
SELECT jsonb_build_object('database',d.datname,'databaseOid',d.oid::text,'owner',pg_get_userbyid(d.datdba),
 'databaseAclHash',encode(sha256(convert_to(coalesce(d.datacl::text,''),'UTF8')),'hex'),
 'role',r.rolname,'roleOid',r.oid::text,'login',r.rolcanlogin,'superuser',r.rolsuper,'inherit',r.rolinherit,
 'createDb',r.rolcreatedb,'createRole',r.rolcreaterole,'replication',r.rolreplication,'bypassRls',r.rolbypassrls,
 'memberships',(SELECT count(*) FROM pg_auth_members WHERE member=r.oid),
 'connect',has_database_privilege(r.oid,d.oid,'CONNECT'))
FROM pg_database d,pg_roles r WHERE d.datname='$name' AND r.rolname='$role';
"@
    $result = Invoke-AssistantOwnerSql $Context 'postgres' 'postgres' $Context.State.postgresPassword $sql | ConvertFrom-Json
    if ($null -eq $result -or $result.owner -cne ($name+'_owner') -or $result.role -cne $role -or
        $result.superuser -or $result.inherit -or $result.createDb -or $result.createRole -or $result.replication -or $result.bypassRls -or
        $result.memberships -ne 0 -or -not $result.connect) { throw 'Owner/runtime least-privilege binding failed.' }
    return $result
}

function Protect-AssistantOwnerDirectory([string]$Path) {
    Assert-AssistantOwnerPath $Path
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    if ($IsWindows) {
        $acl = [Security.AccessControl.DirectorySecurity]::new()
        $acl.SetAccessRuleProtection($true,$false)
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.WindowsIdentity]::GetCurrent().User,'FullControl','ContainerInherit, ObjectInherit','None','Allow'))
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-18'),'ReadAndExecute','ContainerInherit, ObjectInherit','None','Allow'))
        Set-Acl -LiteralPath $Path -AclObject $acl
    } else { [IO.File]::SetUnixFileMode($Path, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute) }
}

function Write-AssistantOwnerJson([string]$Path, $Value, [switch]$Replace) {
    Assert-AssistantOwnerPath $Path
    $target = [IO.Path]::GetFullPath($Path)
    $temporary = $target + '.pending-' + [Guid]::NewGuid().ToString('N')
    if ([IO.Path]::GetDirectoryName($temporary) -cne [IO.Path]::GetDirectoryName($target)) { throw 'Atomic evidence path escaped its directory.' }
    # Never truncate the only restoration baseline. Flush a sibling before an atomic same-directory rename.
    $stream = [IO.FileStream]::new($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes(($Value | ConvertTo-Json -Depth 30))
        $stream.Write($bytes,0,$bytes.Length)
        $stream.Flush($true)
    } finally { $stream.Dispose() }
    if (-not $IsWindows) { [IO.File]::SetUnixFileMode($temporary,[IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite) }
    [IO.File]::Move($temporary,$target,[bool]$Replace)
}
