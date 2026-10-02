Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-ControlledBaseEnvironment {
    $names = if ($env:OS -eq 'Windows_NT') {
        @('SystemRoot','WINDIR','ComSpec','PATHEXT','TEMP','TMP','USERPROFILE','LOCALAPPDATA','APPDATA',
          'ProgramData','ProgramFiles','ProgramFiles(x86)','PATH','DOTNET_ROOT')
    }
    else {
        @('PATH','HOME','TMPDIR','LANG','LC_ALL','DOTNET_ROOT')
    }
    $result = @{}
    foreach ($name in $names) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if (-not [string]::IsNullOrWhiteSpace($value)) { $result[$name] = $value }
    }
    return $result
}

function ConvertTo-ProcessArgument([string]$Value) {
    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') { return $Value }
    $builder = [Text.StringBuilder]::new()
    [void]$builder.Append('"')
    $slashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') { $slashes++; continue }
        if ($character -eq '"') {
            [void]$builder.Append(('\' * (($slashes * 2) + 1)))
            [void]$builder.Append('"')
            $slashes = 0
            continue
        }
        if ($slashes -gt 0) { [void]$builder.Append(('\' * $slashes)); $slashes = 0 }
        [void]$builder.Append($character)
    }
    if ($slashes -gt 0) { [void]$builder.Append(('\' * ($slashes * 2))) }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function New-ControlledProcessStartInfo {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$Arguments = @(),
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [hashtable]$Environment = @{},
        [switch]$CaptureOutput
    )
    $info = [Diagnostics.ProcessStartInfo]::new()
    $isCommandScript = $env:OS -eq 'Windows_NT' -and [IO.Path]::GetExtension($FilePath) -in @('.cmd','.bat')
    $info.FileName = if ($isCommandScript) { [Environment]::GetEnvironmentVariable('ComSpec') } else { $FilePath }
    $info.WorkingDirectory = $WorkingDirectory
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $renderedParts = @()
    foreach ($argument in $Arguments) { $renderedParts += ConvertTo-ProcessArgument ([string]$argument) }
    $renderedArguments = $renderedParts -join ' '
    $info.Arguments = if ($isCommandScript) {
        "/d /s /c `"`"$FilePath`" $renderedArguments`""
    }
    else { $renderedArguments }
    $info.Environment.Clear()
    $controlled = Get-ControlledBaseEnvironment
    foreach ($entry in $controlled.GetEnumerator()) { $info.Environment[$entry.Key] = [string]$entry.Value }
    foreach ($entry in $Environment.GetEnumerator()) { $info.Environment[$entry.Key] = [string]$entry.Value }
    if ($CaptureOutput) {
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
    }
    return $info
}

function Invoke-ControlledProcess {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$Arguments = @(),
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [hashtable]$Environment = @{},
        [switch]$AllowFailure
    )
    $info = New-ControlledProcessStartInfo $FilePath $Arguments $WorkingDirectory $Environment -CaptureOutput
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    try {
        if (-not $process.Start()) { throw "Unable to start controlled process '$FilePath'." }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $result = [pscustomobject]@{ ExitCode = $process.ExitCode; StandardOutput = $stdout; StandardError = $stderr }
        if (-not $AllowFailure -and $result.ExitCode -ne 0) {
            throw "Controlled process '$FilePath' failed with exit code $($result.ExitCode). $stderr"
        }
        return $result
    }
    finally { $process.Dispose() }
}

function Start-ControlledProcess {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$Arguments = @(),
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [hashtable]$Environment = @{}
    )
    $info = New-ControlledProcessStartInfo $FilePath $Arguments $WorkingDirectory $Environment
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    if (-not $process.Start()) { throw "Unable to start controlled process '$FilePath'." }
    return $process
}

function Get-StringSha256([string]$Value) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value)))).Replace('-','')
    }
    finally { $algorithm.Dispose() }
}

function Get-CanonicalFileSetIdentity {
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$RelativePaths
    )
    $root = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $paths = [string[]]@($RelativePaths | ForEach-Object { ([string]$_).Replace('\', '/') } | Select-Object -Unique)
    [Array]::Sort($paths, [StringComparer]::Ordinal)
    $records = @()
    foreach ($relative in $paths) {
        if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or
            $relative.Split('/') -contains '..') {
            throw "Canonical identity path '$relative' is not repository-relative."
        }
        $fullPath = [IO.Path]::GetFullPath((Join-Path $root $relative))
        if (-not $fullPath.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
            -not [IO.File]::Exists($fullPath)) {
            throw "Canonical identity source '$relative' is unavailable."
        }
        $stream = [IO.File]::Open($fullPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            $bytes = $stream.Length
            $algorithm = [Security.Cryptography.SHA256]::Create()
            try {
                $sha256 = ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
            }
            finally { $algorithm.Dispose() }
        }
        finally { $stream.Dispose() }
        $records += [ordered]@{
            path = $relative
            bytes = $bytes
            sha256 = $sha256
        }
    }
    $material = if ($records.Count -eq 0) { '' }
        else { (@($records | ForEach-Object { "$($_.path)`t$($_.bytes)`t$($_.sha256)" }) -join "`n") + "`n" }
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        $digest = ([BitConverter]::ToString($algorithm.ComputeHash(
            [Text.UTF8Encoding]::new($false).GetBytes($material)))).Replace('-', '').ToLowerInvariant()
    }
    finally { $algorithm.Dispose() }
    return [ordered]@{
        algorithm = 'canonical-file-set-v1'
        definition = 'repository-relative / paths; ordinal case-sensitive ordering; decimal byte length; lowercase raw-file SHA-256; UTF-8 without BOM; LF records including final LF'
        fileCount = $records.Count
        digest = $digest
        files = @($records)
    }
}

function Get-CanonicalDirtySourceIdentity {
    param([Parameter(Mandatory)][string]$RepositoryRoot)
    $git = (Get-Command git -ErrorAction Stop).Source
    $head = (Invoke-ControlledProcess $git @('-C',$RepositoryRoot,'rev-parse','HEAD') $RepositoryRoot).StandardOutput.Trim()
    $changed = (Invoke-ControlledProcess $git @('-C',$RepositoryRoot,'-c','core.quotepath=false','diff','HEAD','--name-only','--diff-filter=ACMRTUXB','-z','--') $RepositoryRoot).StandardOutput
    $untracked = (Invoke-ControlledProcess $git @('-C',$RepositoryRoot,'-c','core.quotepath=false','ls-files','--others','--exclude-standard','-z','--') $RepositoryRoot).StandardOutput
    $paths = [string[]]@(($changed + $untracked) -split [char]0 | Where-Object { -not [string]::IsNullOrEmpty($_) })
    $identity = Get-CanonicalFileSetIdentity -RepositoryRoot $RepositoryRoot -RelativePaths $paths
    return [ordered]@{
        head = $head
        dirtyFileCount = $identity.fileCount
        dirtyDigest = $identity.digest
        algorithm = $identity.algorithm
        definition = $identity.definition
        files = $identity.files
    }
}

function Resolve-D11RetainedState {
    param(
        [Parameter(Mandatory)][bool]$SecretsPresent,
        [Parameter(Mandatory)][hashtable]$Volumes
    )
    $postgres = $Volumes.ContainsKey('postgres') -and [bool]$Volumes.postgres
    $seaweed = $Volumes.ContainsKey('seaweed') -and [bool]$Volumes.seaweed
    if (-not $SecretsPresent -and -not $postgres -and -not $seaweed) { return 'CLEAN_FIRST_RUN' }
    if ($SecretsPresent -and $postgres -and $seaweed) { return 'COMPLETE_RETAINED_STATE' }
    return 'BLOCKED_RECOVERY_REQUIRED'
}

function Test-D11RetainedServiceCredentials {
    param([Parameter(Mandatory)][object]$State)

    if ($null -eq $State.services) { return $false }
    $expected = [ordered]@{
        evidence = @{ database='monergy_evidence'; owner='monergy_evidence_owner'; runtime='monergy_evidence_runtime' }
        financial_profile = @{ database='monergy_financial_profile'; owner='monergy_financial_profile_owner'; runtime='monergy_financial_profile_runtime' }
        financial_rules = @{ database='monergy_financial_rules'; owner='monergy_financial_rules_owner'; runtime='monergy_financial_rules_runtime' }
        reporting = @{ database='monergy_reporting'; owner='monergy_reporting_owner'; runtime='monergy_reporting_runtime' }
        audit = @{ database='monergy_audit'; owner='monergy_audit_owner'; runtime='monergy_audit_runtime' }
    }
    $actualNames = @($State.services.PSObject.Properties.Name)
    if ($actualNames.Count -ne $expected.Count -or
        @($actualNames | Where-Object { -not $expected.Contains($_) }).Count -gt 0) { return $false }
    foreach ($name in $expected.Keys) {
        $entry = $State.services.$name
        if ($null -eq $entry -or $entry.database -cne $expected[$name].database -or
            $entry.owner -cne $expected[$name].owner -or $entry.runtime -cne $expected[$name].runtime -or
            [string]::IsNullOrWhiteSpace([string]$entry.ownerPassword) -or
            [string]::IsNullOrWhiteSpace([string]$entry.runtimePassword)) { return $false }
    }
    return $true
}

function Resolve-D11StartJournalState {
    param([string[]]$OwnershipStates = @())
    if ($OwnershipStates.Count -gt 0 -and
        @($OwnershipStates | Where-Object { $_ -ne 'VERIFIED_RUNNING' }).Count -eq 0) {
        return 'ALREADY_RUNNING'
    }
    if (@($OwnershipStates | Where-Object { $_ -in @('VERIFIED_RUNNING','IDENTITY_MISMATCH','UNRESOLVED_LAUNCH') }).Count -gt 0) {
        return 'BLOCKED_RECOVERY_REQUIRED'
    }
    return 'SAFE_TO_START'
}

function Resolve-D11ObservedRuntimeState {
    param(
        [Parameter(Mandatory)][bool]$AllProcessesOwned,
        [Parameter(Mandatory)][bool]$StoresReady,
        [Parameter(Mandatory)][bool]$VolumesReady,
        [Parameter(Mandatory)][bool]$HealthReady,
        [Parameter(Mandatory)][bool]$OwnerContractReady,
        [Parameter(Mandatory)][bool]$PreparedIdentityCurrent
    )
    if ($AllProcessesOwned -and $StoresReady -and $VolumesReady -and $HealthReady -and
        $OwnerContractReady -and $PreparedIdentityCurrent) { return 'READY_FOR_PROFILE' }
    if ($AllProcessesOwned) { return 'DEGRADED' }
    return 'BLOCKED'
}

function Resolve-D11StopJournalState {
    param([Parameter(Mandatory)][bool]$HasUnresolvedIdentity)
    return $(if ($HasUnresolvedIdentity) { 'BLOCKED' } else { 'STOPPED' })
}

function Get-ControlledProcessOwnership([object]$Entry) {
    if ($null -eq $Entry.pid) { return 'UNRESOLVED_LAUNCH' }
    $process = Get-Process -Id $Entry.pid -ErrorAction SilentlyContinue
    if ($null -eq $process) { return 'NOT_RUNNING' }
    $sameStart = $process.StartTime.ToUniversalTime().Ticks -eq [long]$Entry.startTimeTicks
    $actualPath = try { $process.Path } catch { $null }
    $samePath = -not [string]::IsNullOrWhiteSpace($actualPath) -and
        [IO.Path]::GetFullPath($actualPath) -eq [IO.Path]::GetFullPath([string]$Entry.executable)
    return $(if ($sameStart -and $samePath) { 'VERIFIED_RUNNING' } else { 'IDENTITY_MISMATCH' })
}

Export-ModuleMember -Function Get-ControlledBaseEnvironment,New-ControlledProcessStartInfo,Invoke-ControlledProcess,Start-ControlledProcess,Get-StringSha256,Get-ControlledProcessOwnership,Get-CanonicalFileSetIdentity,Get-CanonicalDirtySourceIdentity,Resolve-D11RetainedState,Test-D11RetainedServiceCredentials,Resolve-D11StartJournalState,Resolve-D11ObservedRuntimeState,Resolve-D11StopJournalState
