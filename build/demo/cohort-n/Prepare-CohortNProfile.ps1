[CmdletBinding()]
param(
    [string]$RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')),
    [string]$Profile = 'p2n-admin-connectivity',
    [int]$Port = 4291
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function New-Secret {
    $bytes = [byte[]]::new(32)
    [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    [Convert]::ToHexString($bytes).ToLowerInvariant()
}
function Save-Json([string]$Path, [object]$Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
}
function Hash([string]$Value) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Value))).ToLowerInvariant()
}

$privateRoot = Join-Path $RepositoryRoot '.artifacts/cohort-n/private'
$mailboxRoot = Join-Path $privateRoot 'mailbox'
$binaryRoot = Join-Path $RepositoryRoot '.artifacts/cohort-n/smtp-fixture'
New-Item -ItemType Directory -Force -Path $privateRoot,$mailboxRoot,$binaryRoot | Out-Null

$authPath = Join-Path $privateRoot 'smtp-auth.json'
if (-not (Test-Path -LiteralPath $authPath)) {
    Save-Json $authPath @{ User='cohort-n-tester'; Password=(New-Secret) }
}
$auth = Get-Content -LiteralPath $authPath -Raw -Encoding utf8 | ConvertFrom-Json

$rootPath = Join-Path $privateRoot 'smtp-root.pem'
$serverPath = Join-Path $privateRoot 'smtp-server.pfx'
if (-not (Test-Path -LiteralPath $rootPath) -or -not (Test-Path -LiteralPath $serverPath)) {
    $rootKey = [Security.Cryptography.RSA]::Create(3072)
    $rootRequest = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
        'CN=Monergy Cohort N LOCAL SMTP Root', $rootKey,
        [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $rootRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true,$false,0,$true))
    $rootRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
        [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyCertSign -bor [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::CrlSign,$true))
    $root = $rootRequest.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1),[DateTimeOffset]::UtcNow.AddDays(30))
    $serverKey = [Security.Cryptography.RSA]::Create(3072)
    $serverRequest = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
        'CN=127.0.0.1', $serverKey, [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $serverRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false,$false,0,$true))
    $serverRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
        [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature,$true))
    $eku = [Security.Cryptography.OidCollection]::new(); [void]$eku.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1'))
    $serverRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($eku,$true))
    $san = [Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new(); $san.AddIpAddress([Net.IPAddress]::Loopback); $san.AddDnsName('localhost')
    $serverRequest.CertificateExtensions.Add($san.Build())
    $serial=[byte[]]::new(16); [Security.Cryptography.RandomNumberGenerator]::Fill($serial)
    $issued = $serverRequest.Create($root,[DateTimeOffset]::UtcNow.AddDays(-1),[DateTimeOffset]::UtcNow.AddDays(14),$serial)
    $server = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::CopyWithPrivateKey($issued,$serverKey)
    [IO.File]::WriteAllText($rootPath,$root.ExportCertificatePem(),[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllBytes($serverPath,$server.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pkcs12))
    $server.Dispose();$issued.Dispose();$serverKey.Dispose();$root.Dispose();$rootKey.Dispose()
}

$agreementPath = Join-Path $privateRoot 'account-agreements.json'
$documents = @(
    @{Kind='TERMS';Version='cohort-n-local-1';Title='LOCAL test terms';Body='Protected LOCAL Cohort N terms publication. No Production legal claim.'},
    @{Kind='PRIVACY';Version='cohort-n-local-1';Title='LOCAL test privacy notice';Body='Protected LOCAL Cohort N privacy publication. No Production legal claim.'},
    @{Kind='CONSENT';Version='cohort-n-local-1';Title='LOCAL test consent';Body='Protected LOCAL Cohort N consent publication. No Production legal claim.'}
)
foreach ($document in $documents) { $document.Sha256 = Hash $document.Body }
Save-Json $agreementPath @{Publications=@{T001=@{TenantId='T001';PublishedAt='2026-11-12T00:00:00.0000000+00:00';Documents=$documents}}}

$emailPath = Join-Path $privateRoot 'email.json'
Save-Json $emailPath @{SmtpHost='127.0.0.1';SmtpPort=2525;SmtpUser=$auth.User;SmtpPassword=$auth.Password;From='cohort-n@example.test'}
$googlePath = Join-Path $privateRoot 'google-drive.json'
$oneDrivePath = Join-Path $privateRoot 'onedrive.json'
if (-not (Test-Path -LiteralPath $googlePath)) { Save-Json $googlePath @{T001=@{ClientId='local-google-contract';ClientSecret=(New-Secret);RedirectUri="http://localhost:$Port/connections-api/local/v1/google-drive/callback"}} }
if (-not (Test-Path -LiteralPath $oneDrivePath)) { Save-Json $oneDrivePath @{T001=@{ClientId='local-microsoft-contract';ClientSecret=(New-Secret);RedirectUri="http://localhost:$Port/connections-api/local/v1/onedrive/callback"}} }

dotnet publish (Join-Path $PSScriptRoot 'smtp-fixture/CohortNSmtpFixture.csproj') -c Release -o $binaryRoot --nologo
if ($LASTEXITCODE -ne 0) { throw 'The protected SMTP fixture failed to compile.' }

$uat = Join-Path $RepositoryRoot 'build/uat/Invoke-MonergyUat.ps1'
$arguments = @{
    Action='Prepare';Profile=$Profile;Port=$Port;EnableOnboarding=$true;EnableEmailAccess=$true;
    EnableCustomerRegistration=$true;AccountAgreementFile=$agreementPath;EmailConfigurationFile=$emailPath;
    GoogleDriveConfigurationFile=$googlePath;OneDriveConfigurationFile=$oneDrivePath;
    EvidencePersistenceConfigurationFile=(Join-Path $privateRoot 'evidence-persistence.json');
    FinancialPersistenceConfigurationFile=(Join-Path $privateRoot 'financial-persistence.json');
    JobManagementPersistenceConfigurationFile=(Join-Path $privateRoot 'job-management-persistence.json');
    FinancialRulesPersistenceConfigurationFile=(Join-Path $privateRoot 'financial-rules-persistence.json');
    ClassificationConfigurationFile=(Join-Path $privateRoot 'classification.json');
    RequireRetainedEvidenceAndFinancialProfile=$true;RequireRetainedDocumentProcessing=$true;
    RequireRetainedFinancialRules=$true;RequireCustomerSetup=$true
}
& $uat @arguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$profileRoot = Join-Path $RepositoryRoot ".artifacts/uat/$Profile"
$composeEnv = Join-Path $profileRoot 'compose.env'
$privateUnix = $privateRoot.Replace('\','/')
$binaryUnix = $binaryRoot.Replace('\','/')
$mailboxUnix = $mailboxRoot.Replace('\','/')
[IO.File]::AppendAllText($composeEnv,"COHORT_N_PRIVATE_ROOT='$privateUnix'`nCOHORT_N_SMTP_BINARY_ROOT='$binaryUnix'`nCOHORT_N_MAILBOX_ROOT='$mailboxUnix'`n",[Text.UTF8Encoding]::new($false))

if ($env:OS -eq 'Windows_NT') {
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent().Name
    & icacls (Join-Path $RepositoryRoot '.artifacts/cohort-n') /inheritance:r /grant:r "${identity}:(OI)(CI)F" 'SYSTEM:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Unable to protect Cohort N transport state.' }
}
[pscustomobject]@{Profile=$Profile;Port=$Port;ProtectedTransport='STARTTLS_AUTH';ProviderMode='DETERMINISTIC_LOCAL_CONTRACT';Publications=3} | ConvertTo-Json
