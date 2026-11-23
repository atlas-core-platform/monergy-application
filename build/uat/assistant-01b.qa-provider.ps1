#requires -Version 7.0
<#
Gate D only: prepare/start an isolated copy of the accepted Cohort O deterministic
Q&A provider. This is NOT the Ask Monergy Runtime or Production model approval.
Preparation compiles the unchanged fixture and creates NEW private TLS/key files;
it must run only after source/integration gates and the new profile are ready.
It never provisions a tenant, changes permissions, configures a provider, or
generates a financial fact. Those actions use the normal governed product APIs.
Raw fixture receipts contain synthetic Q&A question hashes. Keep them private;
only Count output belongs in public evidence. Ask Monergy must not call this fixture.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Prepare','Start','Count')][string]$Action,
    [Parameter(Mandatory)][ValidatePattern('^assistant01b(?:-[a-z0-9-]+)?$')][string]$Profile,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{32}$')][string]$ExpectedNewInstance,
    [Parameter(Mandatory)][switch]$ConfirmGatesABC
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'assistant-01b.owner-common.ps1')
# The QA fixture is an independent ordinary business dependency. It must remain
# usable while Ask Monergy is disabled, without re-enabling that optional module.
$context=Get-AssistantOwnerContext $Profile $ExpectedNewInstance ([bool]$ConfirmGatesABC) -AllowAssistantDisabled
$repository=$context.Repository
$project='monergy-uat-'+$ExpectedNewInstance.Substring(0,8)
$profileRoot=Join-Path $repository ".artifacts/uat/$Profile"
$privateRoot=Join-Path $repository '.artifacts/assistant-01b-private-fixtures'
$folder=Join-Path $privateRoot "qa-$Profile"
Assert-AssistantOwnerPath $folder
$alias='assistant01b-qa-'+$ExpectedNewInstance
$endpoint="https://${alias}:8443/v1/chat/completions"
$fixtureSource=Join-Path $repository 'build/demo/cohort-o/provider-fixture/Program.cs'
$fixtureProject=Join-Path $repository 'build/demo/cohort-o/provider-fixture/CohortOProviderFixture.csproj'
$metadataPath=Join-Path $folder 'fixture.json'
$envPath=Join-Path $folder 'compose.env'

function Get-QaRuntimeIdentity {
    if ($IsWindows) { return [pscustomobject]@{uid='1000';gid='1000'} }
    $uid=(& id -u);if ($LASTEXITCODE -ne 0) { throw 'Cannot determine QA fixture owner UID.' }
    $gid=(& id -g);if ($LASTEXITCODE -ne 0) { throw 'Cannot determine QA fixture owner GID.' }
    $uid=$uid.Trim();$gid=$gid.Trim()
    foreach ($value in @($uid,$gid)) {
        if ($value -notmatch '^[1-9][0-9]{0,9}$' -or [uint64]$value -gt 2147483647) { throw 'QA fixture requires the original non-root operator identity.' }
    }
    return [pscustomobject]@{uid=$uid;gid=$gid}
}

function Get-RunningService([string]$Service) {
    $ids=@((Invoke-AssistantOwnerProcess @('ps','-q','--filter',"label=com.docker.compose.project=$project",'--filter',"label=com.docker.compose.service=$Service")) -split '\r?\n' | Where-Object { $_ })
    if ($ids.Count -ne 1 -or $ids[0] -notmatch '^[a-f0-9]{12,64}$') { throw 'Exactly one running isolated service is required.' }
    $info=Invoke-AssistantOwnerProcess @('inspect','--format','{{.Id}}|{{index .Config.Labels "com.docker.compose.project"}}|{{index .Config.Labels "com.docker.compose.service"}}|{{.Config.Image}}|{{.State.Running}}',$ids[0])
    $parts=$info.Split('|')
    if ($parts.Length -ne 5 -or $parts[1] -cne $project -or $parts[2] -cne $Service -or $parts[3] -cne "monergy-local-uat:$project" -or $parts[4] -cne 'true') { throw 'Isolated Q&A service identity mismatch.' }
    return $parts[0]
}

function Write-NewPrivateFile([string]$Path,[byte[]]$Bytes) {
    Assert-AssistantOwnerPath $Path
    $stream=[IO.FileStream]::new($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $stream.Write($Bytes,0,$Bytes.Length);$stream.Flush($true) } finally { $stream.Dispose() }
    if (-not $IsWindows) { [IO.File]::SetUnixFileMode($Path,[IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite) }
}

function Assert-PrivateFile([string]$Path) {
    Assert-AssistantOwnerPath $Path
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'Restore the matching protected Q&A fixture; no secret will be regenerated.' }
    if ($IsWindows) {
        $operator=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        foreach ($rule in (Get-Acl -LiteralPath $Path).GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
            if ($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Value -notin @($operator,'S-1-5-18')) { throw 'Q&A fixture material requires operator/System-only ACLs.' }
        }
    } else {
        $mode=[IO.File]::GetUnixFileMode($Path)
        $untrusted=[IO.UnixFileMode]::GroupRead -bor [IO.UnixFileMode]::GroupWrite -bor [IO.UnixFileMode]::GroupExecute -bor [IO.UnixFileMode]::OtherRead -bor [IO.UnixFileMode]::OtherWrite -bor [IO.UnixFileMode]::OtherExecute
        if (($mode -band $untrusted) -ne 0) { throw 'Q&A fixture material requires private owner-only permissions.' }
    }
}

if ($Action -eq 'Prepare') {
    if (-not $context.State.assistant.enabled) { throw 'Initial QA preparation requires the newly enabled Assistant test profile; disabled runs reuse the same fixture.' }
    if (Test-Path -LiteralPath $folder) { throw 'Q&A fixture already exists. Preserve its credentials, certificates and receipts; do not overwrite it.' }
    $runtimeIdentity=Get-QaRuntimeIdentity
    $null=Get-RunningService 'workspace'
    $integration=Get-RunningService 'integration-gateway'
    Protect-AssistantOwnerDirectory $folder
    # The accepted chiseled runtime has no shell or cat. Copy only its public CA
    # bundle from the verified container into this newly protected fixture folder.
    $systemRoots=Join-Path $folder 'system-roots.pem'
    $null=Invoke-AssistantOwnerProcess @('cp',($integration+':/etc/ssl/certs/ca-certificates.crt'),$systemRoots)
    Assert-AssistantOwnerPath $systemRoots
    if (-not (Test-Path -LiteralPath $systemRoots -PathType Leaf) -or (Get-Item -LiteralPath $systemRoots).Length -gt 2097152) { throw 'Container public CA bundle is unavailable or outside its size bound.' }
    if (-not $IsWindows) { [IO.File]::SetUnixFileMode($systemRoots,[IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite) }
    $publicBundle=[IO.File]::ReadAllText($systemRoots)
    if (-not $publicBundle.Contains('-----BEGIN CERTIFICATE-----') -or $publicBundle.Contains('PRIVATE KEY')) { throw 'A valid public container trust bundle is required.' }
    foreach ($child in @('binary','receipts')) { Protect-AssistantOwnerDirectory (Join-Path $folder $child) }
    Write-AssistantOwnerJson (Join-Path $folder 'authorization.json') @{ApiKey=[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).ToLowerInvariant()}
    $rootKey=$null;$root=$null;$serverKey=$null;$issued=$null;$server=$null
    try {
        $rootKey=[Security.Cryptography.RSA]::Create(3072)
        $request=[Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=Monergy Assistant 01B LOCAL QA Root',$rootKey,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true,$false,0,$true))
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new([Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyCertSign,$true))
        $root=$request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5),[DateTimeOffset]::UtcNow.AddDays(30))
        $serverKey=[Security.Cryptography.RSA]::Create(3072)
        $request=[Security.Cryptography.X509Certificates.CertificateRequest]::new("CN=$alias",$serverKey,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $san=[Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new();$san.AddDnsName($alias);$request.CertificateExtensions.Add($san.Build())
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false,$false,0,$true))
        $eku=[Security.Cryptography.OidCollection]::new();$null=$eku.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1'))
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($eku,$true))
        $issued=$request.Create($root,[DateTimeOffset]::UtcNow.AddMinutes(-5),[DateTimeOffset]::UtcNow.AddDays(14),[Security.Cryptography.RandomNumberGenerator]::GetBytes(16))
        $server=[Security.Cryptography.X509Certificates.RSACertificateExtensions]::CopyWithPrivateKey($issued,$serverKey)
        Write-NewPrivateFile (Join-Path $folder 'server.pfx') $server.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pkcs12)
        Write-NewPrivateFile (Join-Path $folder 'ca-bundle.pem') ([Text.Encoding]::UTF8.GetBytes($publicBundle+"`n"+$root.ExportCertificatePem()+"`n"))
    } finally { foreach ($disposable in @($server,$issued,$serverKey,$root,$rootKey)) { if ($null -ne $disposable) { $disposable.Dispose() } } }
    # This is the unchanged accepted fixture project, never a new provider adapter.
    & dotnet publish $fixtureProject -c Release -o (Join-Path $folder 'binary') --nologo *> (Join-Path $folder 'publish.log')
    if ($LASTEXITCODE -ne 0) { throw 'Existing deterministic fixture compilation failed; see protected publish.log.' }
    $library=Join-Path $folder 'binary/CohortOProviderFixture.dll'
    if (-not (Test-Path -LiteralPath $library -PathType Leaf)) { throw 'Expected existing fixture library was not produced.' }
    if (-not $IsWindows) { Get-ChildItem -LiteralPath $folder -Recurse -File | ForEach-Object { [IO.File]::SetUnixFileMode($_.FullName,[IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite) } }
    $portableFolder=$folder.Replace('\','/')
    if ($portableFolder.Contains("'") -or $portableFolder.Contains("`n") -or $portableFolder.Contains('$')) { throw 'Unsupported private fixture path for Compose.' }
    Write-NewPrivateFile $envPath ([Text.Encoding]::UTF8.GetBytes("ASSISTANT_QA_PRIVATE_ROOT='$portableFolder'`nASSISTANT_QA_ALIAS=$alias`nASSISTANT_QA_UID=$($runtimeIdentity.uid)`nASSISTANT_QA_GID=$($runtimeIdentity.gid)`n"))
    Write-AssistantOwnerJson $metadataPath @{
        schemaVersion=1;profile=$Profile;instanceId=$ExpectedNewInstance;providerAlias=$alias;endpoint=$endpoint;model='cohort-o-local-contract-v1';
        providerLabel='Assistant 01B protected LOCAL deterministic Q&A contract';
        notice='LOCAL test only. A deterministic protected fixture receives the bounded question and canonical claims. This is not Production model approval or financial advice.';
        authorizationFile=(Join-Path $folder 'authorization.json');
        runtimeUid=$runtimeIdentity.uid;runtimeGid=$runtimeIdentity.gid;
        sourceSha256=(Get-FileHash -LiteralPath $fixtureSource -Algorithm SHA256).Hash;
        projectSha256=(Get-FileHash -LiteralPath $fixtureProject -Algorithm SHA256).Hash;
        librarySha256=(Get-FileHash -LiteralPath $library -Algorithm SHA256).Hash;
        preparedAt=[DateTimeOffset]::UtcNow.ToString('O')
    }
    Write-Output 'Protected LOCAL deterministic Q&A fixture prepared. Provider setup, permissions and financial data remain unchanged.'
    Write-Output "Private metadata: $metadataPath"
    return
}

foreach ($name in @('fixture.json','compose.env','authorization.json','server.pfx','ca-bundle.pem')) { Assert-PrivateFile (Join-Path $folder $name) }
$metadata=Read-AssistantOwnerProtectedJson $metadataPath
$runtimeIdentity=Get-QaRuntimeIdentity
if ($metadata.schemaVersion -ne 1 -or $metadata.profile -cne $Profile -or $metadata.instanceId -cne $ExpectedNewInstance -or $metadata.providerAlias -cne $alias -or $metadata.endpoint -cne $endpoint -or
    $metadata.runtimeUid -cne $runtimeIdentity.uid -or $metadata.runtimeGid -cne $runtimeIdentity.gid -or
    $metadata.authorizationFile -cne (Join-Path $folder 'authorization.json') -or
    $metadata.sourceSha256 -cne (Get-FileHash -LiteralPath $fixtureSource -Algorithm SHA256).Hash -or
    $metadata.projectSha256 -cne (Get-FileHash -LiteralPath $fixtureProject -Algorithm SHA256).Hash -or
    $metadata.librarySha256 -cne (Get-FileHash -LiteralPath (Join-Path $folder 'binary/CohortOProviderFixture.dll') -Algorithm SHA256).Hash) { throw 'Q&A fixture identity or accepted provider source changed.' }

if ($Action -eq 'Start') {
    $null=Get-RunningService 'workspace'
    $null=Get-RunningService 'integration-gateway'
    # Do not let an inherited shell value silently select another profile's
    # secret mounts, host port, or a root runtime identity over the protected file.
    foreach ($line in [IO.File]::ReadLines((Join-Path $profileRoot 'compose.env'))) {
        if ($line -match '^(UAT_[A-Z0-9_]+)=') {
            if ($null -ne [Environment]::GetEnvironmentVariable($Matches[1])) { throw 'Run Q&A setup without shell UAT overrides; the exact new profile environment is authoritative.' }
        }
    }
    $arguments=@('compose','--env-file',(Join-Path $profileRoot 'compose.env'),'--env-file',$envPath,'--project-name',$project,
        '-f',(Join-Path $PSScriptRoot 'compose.yml'))
    if ($context.State.assistant.enabled) {
        $arguments+=@('-f',(Join-Path $PSScriptRoot 'compose.assistant.yml'))
        if ($null -ne $context.State.assistant.PSObject.Properties['publishedResolutionsSha256']) { $arguments+=@('-f',(Join-Path $PSScriptRoot 'compose.assistant-resolutions.yml')) }
    }
    $arguments+=@('-f',(Join-Path $PSScriptRoot 'assistant-01b.qa-provider.yml'),'--profile','document-security','up','-d','--no-build','--no-deps','assistant-qa-provider','integration-gateway')
    # Shell environment otherwise takes precedence over --env-file. Bind only the
    # fixture variables and restore them even when Compose fails. Dedicated QA
    # UID/GID remain available when the launcher omits disabled Assistant variables.
    $previousRoot=$env:ASSISTANT_QA_PRIVATE_ROOT
    $previousAlias=$env:ASSISTANT_QA_ALIAS
    $previousUid=$env:ASSISTANT_QA_UID
    $previousGid=$env:ASSISTANT_QA_GID
    try {
        $env:ASSISTANT_QA_PRIVATE_ROOT=$folder.Replace('\','/')
        $env:ASSISTANT_QA_ALIAS=$alias
        $env:ASSISTANT_QA_UID=$runtimeIdentity.uid
        $env:ASSISTANT_QA_GID=$runtimeIdentity.gid
        $null=Invoke-AssistantOwnerProcess $arguments
    } finally {
        $env:ASSISTANT_QA_PRIVATE_ROOT=$previousRoot
        $env:ASSISTANT_QA_ALIAS=$previousAlias
        $env:ASSISTANT_QA_UID=$previousUid
        $env:ASSISTANT_QA_GID=$previousGid
    }
    $null=Get-RunningService 'assistant-qa-provider'
    $null=Get-RunningService 'integration-gateway'
    Write-Output 'Isolated deterministic Q&A sidecar started; only this new profile Integration Gateway received its public CA overlay. Configure it through normal Platform Administration.'
    return
}

$null=Get-RunningService 'assistant-qa-provider'
$receiptPath=Join-Path $folder 'receipts/provider-calls.ndjson'
Assert-AssistantOwnerPath $receiptPath
$count=0
if (Test-Path -LiteralPath $receiptPath -PathType Leaf) {
    if ((Get-Item -LiteralPath $receiptPath).Length -gt 16777216) { throw 'Q&A canary receipt size exceeds this bounded run.' }
    foreach ($line in [IO.File]::ReadLines($receiptPath)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $receipt=$line | ConvertFrom-Json
        $count++
        if ($receipt.sequence -ne $count -or $receipt.authorization -cne 'VALIDATED_NOT_RETAINED' -or $receipt.model -cne 'cohort-o-local-contract-v1') { throw 'Q&A canary receipt integrity failed.' }
    }
}
[pscustomobject]@{profile=$Profile;instanceId=$ExpectedNewInstance;provider='LOCAL_DETERMINISTIC_CONTRACT';calls=$count} | ConvertTo-Json
