[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$FixtureImage)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Assistant.Local.ps1')
$testName = 'assistant01b-test-permissions-' + [Guid]::NewGuid().ToString('N').Substring(0,10)
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path $repository ".local/$testName"))
$created = $false
$checks = 0
try {
    $declaredVolumes = (& docker image inspect $FixtureImage --format '{{json .Config.Volumes}}') | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect the disposable fixture image.' }
    $ephemeralVolumes = @()
    if ($null -ne $declaredVolumes) {
        foreach ($volume in $declaredVolumes.PSObject.Properties.Name) {
            if ($volume -notmatch '^/[A-Za-z0-9/_-]+$' -or $volume -eq '/') { throw 'Unsupported fixture image volume.' }
            $ephemeralVolumes += @('--tmpfs', "${volume}:rw,noexec,nosuid,size=1m")
        }
    }
    # Real Linux mode/ownership fixture; no named volume or retained profile is used.
    $reader = Resolve-AssistantRuntimeIdentity $false '23456' '23457'
    & docker run -d --name $testName --label monergy.fixture=assistant01b-permissions --user "$($reader.uid):$($reader.gid)" --read-only --cap-drop ALL --security-opt no-new-privileges @ephemeralVolumes --tmpfs /fixture:rw,noexec,nosuid,size=1m,mode=700,uid=23456,gid=23457 --entrypoint /bin/sh $FixtureImage -c 'umask 077; printf permission-fixture > /fixture/config; exec sleep 300' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not create disposable permission fixture.' }
    $created = $true
    & docker exec --user '23456:23457' $testName /bin/sh -c 'test -r /fixture/config && test "$(stat -c %a /fixture/config)" = 600 && test "$(stat -c %u /fixture/config)" = 23456' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Mode-0600 configuration unreadable by its matched non-root Runtime identity.' }; $checks++
    & docker exec --user '1000:1000' $testName /bin/sh -c 'test ! -r /fixture/config' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Unrelated fixed UID can read the protected configuration.' }; $checks++

    if ($env:OS -eq 'Windows_NT') {
        if (Test-Path -LiteralPath $fixtureRoot) { throw 'Fixture path collision; refusing reuse.' }
        New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
        $fixtureFile = Join-Path $fixtureRoot 'assistant-runtime.json'
        [IO.File]::WriteAllText($fixtureFile, '{"fixture":"readability-only"}')
        $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
        $system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
        $acl = [Security.AccessControl.FileSecurity]::new()
        $acl.SetAccessRuleProtection($true,$false)
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','Allow'))
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($system,'Read','Allow'))
        Set-Acl -LiteralPath $fixtureFile -AclObject $acl
        $rules = @((Get-Acl -LiteralPath $fixtureFile).GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]))
        if ($rules.Count -ne 2 -or @($rules | Where-Object { $_.IdentityReference.Value -notin @($sid.Value,$system.Value) -or $_.IsInherited }).Count) { throw 'Fixture ACL contains unrelated access.' }; $checks++
        $reader = Resolve-AssistantRuntimeIdentity $true '' ''
        & docker run --rm --name ($testName+'-windows') --label monergy.fixture=assistant01b-permissions --user "$($reader.uid):$($reader.gid)" --read-only --cap-drop ALL --security-opt no-new-privileges @ephemeralVolumes --mount "type=bind,source=$fixtureFile,target=/run/assistant-fixture,readonly" --entrypoint /bin/sh $FixtureImage -c 'test -r /run/assistant-fixture && test ! -w /run/assistant-fixture' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Docker Desktop cannot read the private read-only configuration as non-root.' }; $checks++
    }
    Write-Output "Physical Assistant configuration readability: $checks passed; 0 failed. No retained resource used."
} finally {
    if ($created) {
        & docker rm -f $testName | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Warning "Disposable permission container still exists: $testName" }
    }
    $allowedRoot = [IO.Path]::GetFullPath((Join-Path $repository '.local')) + [IO.Path]::DirectorySeparatorChar
    if (-not $fixtureRoot.StartsWith($allowedRoot,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe disposable fixture path.' }
    if (Test-Path -LiteralPath $fixtureRoot) { Remove-Item -LiteralPath $fixtureRoot -Recurse -Force }
}
