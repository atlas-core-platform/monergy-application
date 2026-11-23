#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^assistant01b(?:-[a-z0-9-]+)?$')][string]$Profile,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{32}$')][string]$ExpectedNewInstance,
    [Parameter(Mandatory)][switch]$ConfirmNewProfileAfterGateC
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'assistant-01b.owner-common.ps1')
if (-not $ConfirmNewProfileAfterGateC) { throw 'Gate C and a newly created isolated profile are required.' }
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$profileFile=Join-Path $repository ".artifacts/uat/$Profile/profile.json"
if (-not (Test-Path -LiteralPath $profileFile -PathType Leaf)) { throw 'The explicitly authorized new profile must already exist.' }
$state=Read-AssistantOwnerProtectedJson $profileFile
if ($state.instanceId -cne $ExpectedNewInstance -or $state.assistant.enabled -ne $true) { throw 'New Assistant profile identity mismatch.' }
$privateRoot=Join-Path $repository '.artifacts/assistant-01b-private-fixtures'
$folder=Join-Path $privateRoot "browser-$Profile"
if (Test-Path -LiteralPath $folder) { throw 'Browser fixture already exists. Preserve and reuse its passwords; never overwrite it.' }
foreach ($path in @($privateRoot,(Split-Path $profileFile))) {
    if (Test-Path -LiteralPath $path) {
        $entry=Get-Item -LiteralPath $path -Force
        if (-not $entry.PSIsContainer -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked fixture/profile directories are forbidden.' }
    }
}
New-Item -ItemType Directory -Path $folder -Force | Out-Null
if ($IsWindows) {
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent().User
    $system=[Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $acl=[Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true,$false)
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity,'FullControl','ContainerInherit, ObjectInherit','None','Allow'))
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($system,'ReadAndExecute','ContainerInherit, ObjectInherit','None','Allow'))
    Set-Acl -LiteralPath $folder -AclObject $acl
} else { & chmod 700 $folder; if ($LASTEXITCODE -ne 0) { throw 'Private directory protection failed.' } }
$paths=@{ A900='/access';A100='/';A200='/';A300='/advisor';A400='/dsa';A500='/lender';A600='/financial-professional';A700='/property-valuer';A800='/legal-advisor';A850='/operations-support' }
$people=@()
foreach ($tenant in @('T001','T002')) {
    $actors=if ($tenant -eq 'T001') { @('A900','A100','A200','A300','A400','A500','A600','A700','A800','A850') } else { @('A900','A100') }
    foreach ($actor in $actors) {
        if (@($state.identities | Where-Object { $_.tenantId -eq $tenant -and $_.actorId -eq $actor }).Count -ne 1) { throw 'Required provisioned persona is unavailable.' }
        $password='Local!'+[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))+'7z'
        $people+=@{actor=$actor;tenant=$tenant;email=($actor.ToLowerInvariant()+'@example.test');password=$password;path=$paths[$actor]}
    }
}
$people+=@{actor='A950';tenant=$null;email='a950.platform@example.test';password=('Local!'+[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))+'7z');path='/platform'}
$destination=Join-Path $folder 'fixture.json'
@{schemaVersion=1;profile=$Profile;instanceId=$state.instanceId;port=$state.port;profilePath=$profileFile;personas=$people;createdAt=[DateTimeOffset]::UtcNow.ToString('O')} |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $destination -Encoding utf8NoBOM
if ($IsWindows) {
    $acl=[Security.AccessControl.FileSecurity]::new();$acl.SetAccessRuleProtection($true,$false)
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity,'FullControl','Allow'))
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($system,'Read','Allow'))
    Set-Acl -LiteralPath $destination -AclObject $acl
} else { & chmod 600 $destination; if ($LASTEXITCODE -ne 0) { throw 'Private file protection failed.' } }
Write-Output 'Protected new-profile browser fixture prepared. No activation, password change or session creation has run.'
Write-Output "Fixture path: $destination"
