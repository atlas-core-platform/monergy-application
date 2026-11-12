[CmdletBinding()]
param(
    [string]$RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')),
    [string]$Profile = 'p2n-admin-connectivity',
    [switch]$AttachOnly
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$profileRoot=Join-Path $RepositoryRoot ".artifacts/uat/$Profile"
$privateRoot=Join-Path $RepositoryRoot '.artifacts/cohort-n/private'
$uat=Join-Path $RepositoryRoot 'build/uat/Invoke-MonergyUat.ps1'
$common=@{
    Profile=$Profile;EnableOnboarding=$true;EnableEmailAccess=$true;EnableCustomerRegistration=$true;
    AccountAgreementFile=(Join-Path $privateRoot 'account-agreements.json');EmailConfigurationFile=(Join-Path $privateRoot 'email.json');
    GoogleDriveConfigurationFile=(Join-Path $privateRoot 'google-drive.json');OneDriveConfigurationFile=(Join-Path $privateRoot 'onedrive.json');
    EvidencePersistenceConfigurationFile=(Join-Path $privateRoot 'evidence-persistence.json');FinancialPersistenceConfigurationFile=(Join-Path $privateRoot 'financial-persistence.json');
    JobManagementPersistenceConfigurationFile=(Join-Path $privateRoot 'job-management-persistence.json');FinancialRulesPersistenceConfigurationFile=(Join-Path $privateRoot 'financial-rules-persistence.json');
    ClassificationConfigurationFile=(Join-Path $privateRoot 'classification.json');RequireRetainedEvidenceAndFinancialProfile=$true;
    RequireRetainedDocumentProcessing=$true;RequireRetainedFinancialRules=$true;RequireCustomerSetup=$true
}
if (-not $AttachOnly) {
    & $uat -Action Start @common
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$state=Get-Content (Join-Path $profileRoot 'profile.json') -Raw -Encoding utf8|ConvertFrom-Json
$project="monergy-uat-$($state.instanceId.Substring(0,8))"
$image="monergy-local-uat:$project"
$composeEnv=Join-Path $profileRoot 'compose.env'
$binaryRoot=Join-Path $RepositoryRoot '.artifacts/cohort-n/smtp-fixture'
$mailboxRoot=Join-Path $privateRoot 'mailbox'
[IO.File]::AppendAllText($composeEnv,
    "COHORT_N_PRIVATE_ROOT='$($privateRoot.Replace('\','/'))'`nCOHORT_N_SMTP_BINARY_ROOT='$($binaryRoot.Replace('\','/'))'`nCOHORT_N_MAILBOX_ROOT='$($mailboxRoot.Replace('\','/'))'`n",
    [Text.UTF8Encoding]::new($false))
$container=docker create $image
if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect the exact Cohort N runtime image trust store.' }
try { docker cp "${container}:/etc/ssl/certs/ca-certificates.crt" (Join-Path $privateRoot 'ca-bundle.pem') | Out-Null }
finally { docker rm $container | Out-Null }
if ($LASTEXITCODE -ne 0) { throw 'Unable to obtain the Cohort N runtime trust store.' }
[IO.File]::AppendAllText((Join-Path $privateRoot 'ca-bundle.pem'),[IO.File]::ReadAllText((Join-Path $privateRoot 'smtp-root.pem')),[Text.UTF8Encoding]::new($false))

$compose=@('compose','--env-file',(Join-Path $profileRoot 'compose.env'),'-f',(Join-Path $RepositoryRoot 'build/uat/compose.yml'),'-f',(Join-Path $PSScriptRoot 'compose.smtp.override.yml'),'--project-name',$project,'--profile','document-security')
docker @compose up -d --no-deps --force-recreate customer-identity smtp-fixture
if ($LASTEXITCODE -ne 0) { throw 'Unable to attach the protected Cohort N SMTP fixture.' }
for($attempt=0;$attempt -lt 30;$attempt++){
    $logs=docker @compose logs --no-color --tail 5 smtp-fixture 2>$null
    if(($logs|Out-String).Contains('protected STARTTLS SMTP fixture is ready')){break}
    Start-Sleep -Seconds 1
}
if($attempt -ge 30){throw 'The protected Cohort N SMTP fixture did not become ready.'}
& $uat -Action Verify -Profile $Profile -RequireRetainedEvidenceAndFinancialProfile -RequireRetainedDocumentProcessing -RequireRetainedFinancialRules
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
[pscustomobject]@{Profile=$Profile;Port=[int]$state.port;Project=$project;Readiness='VERIFIED';Smtp='AUTHENTICATED_STARTTLS_LOCAL'}|ConvertTo-Json
