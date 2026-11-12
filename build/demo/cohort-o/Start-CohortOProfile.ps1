[CmdletBinding()]
param(
  [string]$RepositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')),
  [string]$Profile='p2o-customer-completion',
  [switch]$AttachOnly
)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$template=Join-Path $RepositoryRoot 'build/demo/cohort-n/Start-CohortNProfile.ps1';$source=Get-Content -LiteralPath $template -Raw -Encoding utf8
$replace=[ordered]@{'p2n-admin-connectivity'='p2o-customer-completion';'.artifacts/cohort-n'='.artifacts/cohort-o';'cohort-n'='cohort-o';'Cohort N'='Cohort O';'COHORT_N'='COHORT_O';'compose.smtp.override.yml'='compose.smtp.override.yml'}
foreach($entry in $replace.GetEnumerator()){$source=$source.Replace([string]$entry.Key,[string]$entry.Value)}
$scriptRoot=$PSScriptRoot.Replace("'","''");$source=$source.Replace('$PSScriptRoot',"'$scriptRoot'")
$old='GoogleDriveConfigurationFile=(Join-Path $privateRoot ''google-drive.json'');OneDriveConfigurationFile=(Join-Path $privateRoot ''onedrive.json'');'
$new=$old+"`n    AiModelConfigurationFile=(Join-Path `$privateRoot 'ai-model.json');KycConfigurationFile=(Join-Path `$privateRoot 'kyc.json');`n    EnablePersonaLogin=`$true;EnableCustomerPan=`$true;"
$source=$source.Replace($old,$new)
$source=$source.Replace("[IO.File]::ReadAllText((Join-Path `$privateRoot 'smtp-root.pem'))",'([IO.File]::ReadAllText((Join-Path $privateRoot ''smtp-root.pem''))+"`n"+[IO.File]::ReadAllText((Join-Path $privateRoot ''provider-root.pem'')))')
$source=$source.Replace('customer-identity smtp-fixture','customer-identity integration-gateway smtp-fixture qa-provider-fixture')
$providerEnvironment=@'
$providerBinaryRoot=Join-Path $RepositoryRoot '.artifacts/cohort-o/provider-fixture'
$providerReceiptRoot=Join-Path $RepositoryRoot '.artifacts/cohort-o/provider-receipts'
[IO.File]::AppendAllText($composeEnv,
    "COHORT_O_PROVIDER_BINARY_ROOT='$($providerBinaryRoot.Replace('\','/'))'`nCOHORT_O_PROVIDER_RECEIPT_ROOT='$($providerReceiptRoot.Replace('\','/'))'`n",
    [Text.UTF8Encoding]::new($false))
'@
$source=$source.Replace('$container=docker create $image',$providerEnvironment+"`n"+'$container=docker create $image')
if(-not $source.Contains('AiModelConfigurationFile')-or-not $source.Contains('qa-provider-fixture')){throw 'Cohort O start transformation failed.'}
$bootstrap=[scriptblock]::Create($source);& $bootstrap -RepositoryRoot $RepositoryRoot -Profile $Profile -AttachOnly:$AttachOnly
if($LASTEXITCODE-ne 0){exit $LASTEXITCODE}
$profileRoot=Join-Path $RepositoryRoot ".artifacts/uat/$Profile";$state=Get-Content (Join-Path $profileRoot 'profile.json') -Raw|ConvertFrom-Json;$project="monergy-uat-$($state.instanceId.Substring(0,8))";$compose=@('compose','--env-file',(Join-Path $profileRoot 'compose.env'),'-f',(Join-Path $RepositoryRoot 'build/uat/compose.yml'),'-f',(Join-Path $PSScriptRoot 'compose.smtp.override.yml'),'--project-name',$project,'--profile','document-security')
$ready=$false;for($i=0;$i-lt 30;$i++){$log=docker @compose logs --no-color --tail 10 qa-provider-fixture 2>$null;if(($log|Out-String).Contains('Now listening on: https://[::]:8443')){$ready=$true;break};Start-Sleep -Seconds 1};if(-not $ready){throw 'Protected Cohort O provider fixture did not become ready.'}
[pscustomobject]@{Profile=$Profile;Port=[int]$state.port;Project=$project;Provider='PROTECTED_HTTPS_READY'}|ConvertTo-Json
