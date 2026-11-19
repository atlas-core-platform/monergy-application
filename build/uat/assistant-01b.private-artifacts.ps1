#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Prepare','Verify')][string]$Mode,
    [Parameter(Mandatory)][string]$FixtureFile,
    [string]$OutputDirectory
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$privateRoot=[IO.Path]::GetFullPath((Join-Path $repository '.artifacts/assistant-01b-private-fixtures'))
$fixture=[IO.Path]::GetFullPath($FixtureFile)
if (-not $fixture.StartsWith($privateRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Artifact fixture is outside the protected fixture root.' }
function Assert-RegularPath([string]$Path) {
    $current=Get-Item -LiteralPath $Path -Force
    while ($null -ne $current) {
        if ($current.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked fixture or artifact ancestry is forbidden.' }
        $current=if($current -is [IO.DirectoryInfo]){$current.Parent}else{$current.Directory}
    }
}
function Assert-Private([string]$Path,[switch]$Children) {
    Assert-RegularPath $Path
    if ($IsWindows) {
        $identity=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        $acl=Get-Acl -LiteralPath $Path
        $operatorWrite=$false
        $privateInheritance=$false
        foreach ($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
            if ($rule.AccessControlType -ne 'Allow') { continue }
            if ($rule.IdentityReference.Value -notin @($identity,'S-1-5-18')) { throw 'Raw browser artifacts require operator/System-only permissions.' }
            if ($rule.IdentityReference.Value -eq $identity -and ($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::Write) -eq [Security.AccessControl.FileSystemRights]::Write) { $operatorWrite=$true }
            if ($rule.IdentityReference.Value -eq $identity -and
                ($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::Write) -eq [Security.AccessControl.FileSystemRights]::Write -and
                ($rule.InheritanceFlags -band [Security.AccessControl.InheritanceFlags]::ContainerInherit) -ne 0 -and
                ($rule.InheritanceFlags -band [Security.AccessControl.InheritanceFlags]::ObjectInherit) -ne 0 -and
                $rule.PropagationFlags -eq [Security.AccessControl.PropagationFlags]::None) { $privateInheritance=$true }
        }
        if (-not $operatorWrite) { throw 'Private artifact path is not writable by this operator.' }
        if ($Children -and (-not $acl.AreAccessRulesProtected -or -not $privateInheritance)) { throw 'Raw artifact parent must provide protected inheritable operator permissions.' }
    } else {
        $mode=[IO.File]::GetUnixFileMode($Path)
        $untrusted=[IO.UnixFileMode]::GroupRead -bor [IO.UnixFileMode]::GroupWrite -bor [IO.UnixFileMode]::GroupExecute -bor [IO.UnixFileMode]::OtherRead -bor [IO.UnixFileMode]::OtherWrite -bor [IO.UnixFileMode]::OtherExecute
        if (($mode -band $untrusted) -ne 0 -or ($mode -band [IO.UnixFileMode]::UserWrite) -eq 0) { throw 'Raw browser artifacts require private owner-writable permissions.' }
    }
}
function Protect-NewDirectory([string]$Path) {
    if ($IsWindows) {
        $identity=[Security.Principal.WindowsIdentity]::GetCurrent().User
        $system=[Security.Principal.SecurityIdentifier]::new('S-1-5-18')
        $acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetAccessRuleProtection($true,$false)
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity,'FullControl','ContainerInherit, ObjectInherit','None','Allow'))
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($system,'ReadAndExecute','ContainerInherit, ObjectInherit','None','Allow'))
        Set-Acl -LiteralPath $Path -AclObject $acl
    } else { & chmod 700 $Path; if($LASTEXITCODE -ne 0){throw 'Raw artifact directory protection failed.'} }
}
if (-not (Test-Path -LiteralPath $fixture -PathType Leaf)) { throw 'Protected fixture file is required before browser execution.' }
$fixtureParent=Split-Path $fixture
Assert-Private $fixture
Assert-Private $fixtureParent -Children
$rawParent=Join-Path $fixtureParent 'playwright-raw'
if ($Mode -eq 'Prepare') {
    if ($OutputDirectory) { throw 'Raw run paths are generated, not caller-selected.' }
    if (-not (Test-Path -LiteralPath $rawParent)) { New-Item -ItemType Directory -Path $rawParent | Out-Null;Protect-NewDirectory $rawParent }
    Assert-Private $rawParent -Children
    $OutputDirectory=Join-Path $rawParent ([Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
    Protect-NewDirectory $OutputDirectory
} elseif (-not $OutputDirectory) { throw 'A prepared private raw artifact directory is required.' }
$output=[IO.Path]::GetFullPath($OutputDirectory)
if ((Split-Path $output) -cne $rawParent -or (Split-Path $output -Leaf) -notmatch '^[0-9a-f]{32}$') { throw 'Raw artifacts must stay in the matching fixture private run directory.' }
Assert-Private $rawParent -Children
Assert-Private $output
# Playwright may remove/recreate outputDir. Its parent must remain private and
# provide private inheritance; no fixture credential lives below outputDir.
Write-Output $output
