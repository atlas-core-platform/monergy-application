[CmdletBinding()]
param(
  [string]$RepositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')),
  [string]$Profile='p2o-customer-completion',
  [int]$Port=4301
)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
function Secret{$bytes=[byte[]]::new(32);[Security.Cryptography.RandomNumberGenerator]::Fill($bytes);[Convert]::ToHexString($bytes).ToLowerInvariant()}
function Save([string]$path,[object]$value){[IO.File]::WriteAllText($path,($value|ConvertTo-Json -Depth 20),[Text.UTF8Encoding]::new($false))}
$private=Join-Path $RepositoryRoot '.artifacts/cohort-o/private';$receipts=Join-Path $RepositoryRoot '.artifacts/cohort-o/provider-receipts';$providerBinary=Join-Path $RepositoryRoot '.artifacts/cohort-o/provider-fixture'
New-Item -ItemType Directory -Force -Path $private,$receipts,$providerBinary|Out-Null
$authPath=Join-Path $private 'provider-auth.json';if(-not(Test-Path $authPath)){Save $authPath @{ApiKey=(Secret)}};$auth=Get-Content $authPath -Raw|ConvertFrom-Json
$rootPath=Join-Path $private 'provider-root.pem';$serverPath=Join-Path $private 'provider-server.pfx'
if(-not(Test-Path $rootPath)-or-not(Test-Path $serverPath)){
  $rootKey=[Security.Cryptography.RSA]::Create(3072);$request=[Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=Monergy Cohort O LOCAL Provider Root',$rootKey,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
  $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true,$false,0,$true));$request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new([Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyCertSign,$true));$root=$request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1),[DateTimeOffset]::UtcNow.AddDays(30))
  $key=[Security.Cryptography.RSA]::Create(3072);$serverRequest=[Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=cohort-o-provider',$key,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1);$san=[Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new();$san.AddDnsName('cohort-o-provider');$serverRequest.CertificateExtensions.Add($san.Build());$serverRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false,$false,0,$true));$eku=[Security.Cryptography.OidCollection]::new();[void]$eku.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1'));$serverRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($eku,$true));$serial=[byte[]]::new(16);[Security.Cryptography.RandomNumberGenerator]::Fill($serial);$issued=$serverRequest.Create($root,[DateTimeOffset]::UtcNow.AddDays(-1),[DateTimeOffset]::UtcNow.AddDays(14),$serial);$server=[Security.Cryptography.X509Certificates.RSACertificateExtensions]::CopyWithPrivateKey($issued,$key)
  [IO.File]::WriteAllText($rootPath,$root.ExportCertificatePem(),[Text.UTF8Encoding]::new($false));[IO.File]::WriteAllBytes($serverPath,$server.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pkcs12));$server.Dispose();$issued.Dispose();$key.Dispose();$root.Dispose();$rootKey.Dispose()
}
Save (Join-Path $private 'ai-model.json') @{T001=@{Endpoint='https://cohort-o-provider:8443/v1/chat/completions';ApiKey=$auth.ApiKey;Model='cohort-o-local-contract-v1';ProviderLabel='Protected Cohort O LOCAL provider contract';Notice='LOCAL acceptance only. A deterministic protected provider fixture receives the bounded question and canonical claims. This is not Production provider approval or financial advice.'}}
Save (Join-Path $private 'kyc.json') @{T001=@{Notice='Protected LOCAL Cohort O manual evidence review. Manual acceptance is not government, Aadhaar, bank or provider verification.';Reviewers=@(@{ActorId='A300';Name='Cohort O LOCAL reviewer'})};T002=@{Notice='Protected LOCAL Cohort O manual evidence review. Manual acceptance is not government, Aadhaar, bank or provider verification.';Reviewers=@(@{ActorId='A300';Name='Cohort O LOCAL reviewer'})}}
dotnet publish (Join-Path $PSScriptRoot 'provider-fixture/CohortOProviderFixture.csproj') -c Release -o $providerBinary --nologo
if($LASTEXITCODE-ne 0){throw 'Protected Cohort O provider fixture failed to compile.'}
$template=Join-Path $RepositoryRoot 'build/demo/cohort-n/Prepare-CohortNProfile.ps1';$source=Get-Content -LiteralPath $template -Raw -Encoding utf8
$replace=[ordered]@{'p2n-admin-connectivity'='p2o-customer-completion';'4291'='4301';'.artifacts/cohort-n'='.artifacts/cohort-o';'cohort-n'='cohort-o';'Cohort N'='Cohort O';'COHORT_N'='COHORT_O'}
foreach($entry in $replace.GetEnumerator()){$source=$source.Replace([string]$entry.Key,[string]$entry.Value)}
$templateRoot=(Split-Path -Parent $template).Replace("'","''");$source=$source.Replace('$PSScriptRoot',"'$templateRoot'")
$old='GoogleDriveConfigurationFile=$googlePath;OneDriveConfigurationFile=$oneDrivePath;'
$new=$old+"`n    AiModelConfigurationFile=(Join-Path `$privateRoot 'ai-model.json');KycConfigurationFile=(Join-Path `$privateRoot 'kyc.json');`n    EnablePersonaLogin=`$true;"
$source=$source.Replace($old,$new);if(-not $source.Contains('AiModelConfigurationFile')){throw 'Cohort O prepare transformation failed.'}
$bootstrap=[scriptblock]::Create($source);& $bootstrap -RepositoryRoot $RepositoryRoot -Profile $Profile -Port $Port
if($LASTEXITCODE-ne 0){exit $LASTEXITCODE}
$envPath=Join-Path $RepositoryRoot ".artifacts/uat/$Profile/compose.env";$p=$private.Replace('\','/');$b=$providerBinary.Replace('\','/');$r=$receipts.Replace('\','/');[IO.File]::AppendAllText($envPath,"COHORT_O_PROVIDER_BINARY_ROOT='$b'`nCOHORT_O_PROVIDER_RECEIPT_ROOT='$r'`n",[Text.UTF8Encoding]::new($false))
[pscustomobject]@{Profile=$Profile;Port=$Port;Provider='PROTECTED_HTTPS_CHAT_COMPLETIONS_LOCAL';Kyc='MANUAL_LOCAL_NOTICE'}|ConvertTo-Json
