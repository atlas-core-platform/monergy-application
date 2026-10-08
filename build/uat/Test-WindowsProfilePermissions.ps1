[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:OS -ne 'Windows_NT') { throw 'This verification requires Windows file ACLs.' }
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$launcher = Join-Path $PSScriptRoot 'Invoke-MonergyUat.ps1'
$profile = 'acl-' + [Guid]::NewGuid().ToString('N').Substring(0,12)
$folder = Join-Path $repository ".artifacts/uat/$profile"
$identity = [Security.Principal.WindowsIdentity]::GetCurrent().User
$allowedSids = @($identity.Value, 'S-1-5-18')

function Snapshot {
    $result = @{}
    foreach ($file in Get-ChildItem -LiteralPath $folder -Force -File) {
        $result[$file.Name] = @{
            Hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            WriteTime = $file.LastWriteTimeUtc.Ticks
            Owner = (Get-Acl -LiteralPath $file.FullName).Owner
        }
    }
    return $result
}
function Assert-Unchanged($before) {
    $after = Snapshot
    if ($before.Count -ne $after.Count) { throw 'Permission repair changed the profile file set.' }
    foreach ($name in $before.Keys) {
        if (-not $after.ContainsKey($name)) { throw 'Permission repair removed a profile file.' }
        foreach ($field in @('Hash','WriteTime','Owner')) {
            if ($before[$name][$field] -ne $after[$name][$field]) { throw "Permission repair changed $field for $name." }
        }
    }
}
function Assert-PrivateReadable {
    $items = @((Get-Item -LiteralPath $folder -Force)) + @(Get-ChildItem -LiteralPath $folder -Force)
    foreach ($item in $items) {
        $acl = Get-Acl -LiteralPath $item.FullName
        if (-not $acl.AreAccessRulesProtected) { throw 'Profile permissions still inherit unrelated parent access.' }
        $rules = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
        if ($rules.Count -ne 2) { throw 'Expected only the current operator and local SYSTEM permissions.' }
        foreach ($rule in $rules) {
            if ($rule.IdentityReference.Value -notin $allowedSids -or $rule.AccessControlType -ne 'Allow' -or $rule.IsInherited) { throw 'Unexpected profile ACL entry.' }
        }
        $operator = @($rules | Where-Object { $_.IdentityReference.Value -eq $identity.Value })
        if ($operator.Count -ne 1 -or $operator[0].FileSystemRights -ne 'FullControl') { throw 'Operator lacks explicit full access.' }
        if (-not $item.PSIsContainer) {
            $stream = [IO.File]::OpenRead($item.FullName)
            $stream.Dispose()
        }
    }
    # Native Compose must actually read compose.env under the same Windows token.
    & docker compose --env-file (Join-Path $folder 'compose.env') -f (Join-Path $PSScriptRoot 'compose.yml') --project-name "monergy-$profile" config --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Native Docker Compose could not read the prepared profile.' }
}
function Deny-Read($path) {
    $acl = Get-Acl -LiteralPath $path
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity, 'ReadData', 'Deny'))
    Set-Acl -LiteralPath $path -AclObject $acl
}

try {
    & $launcher -Action Prepare -Profile $profile
    Assert-PrivateReadable
    $before = Snapshot
    & $launcher -Action RepairPermissions -Profile $profile
    Assert-Unchanged $before
    Assert-PrivateReadable

    # Reproduce actual denied reads, including an unreadable master credential file.
    foreach ($name in @('profile.json','compose.env')) {
        $path = Join-Path $folder $name
        Deny-Read $path
        $denied = $false
        $stream = $null
        try { $stream = [IO.File]::OpenRead($path) }
        catch [UnauthorizedAccessException] { $denied = $true }
        finally { if ($null -ne $stream) { $stream.Dispose() } }
        if (-not $denied) { throw 'The denied-read regression fixture did not deny access.' }
    }
    Deny-Read $folder
    & $launcher -Action RepairPermissions -Profile $profile
    Assert-Unchanged $before
    Assert-PrivateReadable

    # Preparing an existing broken profile also repairs access before reading its keys.
    Deny-Read (Join-Path $folder 'profile.json')
    Deny-Read (Join-Path $folder 'compose.env')
    & $launcher -Action Prepare -Profile $profile
    $afterPrepare = Snapshot
    if ($before['profile.json'].Hash -ne $afterPrepare['profile.json'].Hash) { throw 'Preparing an existing profile rotated retained credentials.' }
    Assert-PrivateReadable
    Write-Host "Windows UAT ACL verification passed: PowerShell $($PSVersionTable.PSVersion), explicit private access, denied-read recovery, unchanged credentials/content/owners, native Compose config."
} finally {
    # Only this test's freshly generated profile is removed; no Docker engine or volume is used.
    if (Test-Path -LiteralPath $folder) {
        & $launcher -Action RepairPermissions -Profile $profile
        Remove-Item -LiteralPath $folder -Recurse -Force
    }
}
