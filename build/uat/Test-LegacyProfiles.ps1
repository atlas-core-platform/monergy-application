$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$testRoot=Join-Path ([IO.Path]::GetTempPath()) ('monergy-uat-test-'+[Guid]::NewGuid().ToString('N'))
$scriptPath=Join-Path $testRoot 'build/uat/Invoke-MonergyUat.ps1'
New-Item -ItemType Directory -Path (Split-Path $scriptPath) -Force | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'Invoke-MonergyUat.ps1') $scriptPath
try {
$parseErrors=$null; $parseTokens=$null
$null=[System.Management.Automation.Language.Parser]::ParseFile($scriptPath,[ref]$parseTokens,[ref]$parseErrors)
if ($parseErrors.Count) { throw 'Parser errors' }
function docker { $global:LASTEXITCODE=0; if ($args[0] -eq 'info') { 'linux' } }
function Invoke-WebRequest { [pscustomobject]@{StatusCode=200} }
$global:fixtureMembers=@{ T001=@('A900'); T002=@('A900') }
$global:memberWrites=@()
function Invoke-RestMethod {
 param([string]$Uri,[string]$Method='Get',[string]$ContentType,[hashtable]$Headers,[string]$Body,[int]$TimeoutSec)
 if ($Uri.EndsWith('/identity-api/local/v1/tenant-sessions/revoke')) { return $true }
 if ($Uri.EndsWith('/identity-api/local/v1/tenant-sessions')) {
  $tenant=($Body | ConvertFrom-Json).tenantId
  return [pscustomobject]@{tenantId=$tenant;actorId='A900';authenticationContextId=('session-'+$tenant);subjectVersion=1;expiresAt='2099-01-01T00:00:00Z'}
 }
 if ($Uri -like '*/access-api/v1/administration/members?limit=100') {
  $tenant=[string]$Headers['X-Monergy-Tenant']
  $items=@($global:fixtureMembers[$tenant] | ForEach-Object { [pscustomobject]@{actorId=$_} })
  return [pscustomobject]@{policyVersion=1+@($global:memberWrites).Count;items=$items;nextCursor=$null}
 }
 if ($Uri.EndsWith('/access-api/v1/administration/members') -and $Method -eq 'Post') {
  $tenant=[string]$Headers['X-Monergy-Tenant']; $actor=($Body | ConvertFrom-Json).actorId
  $global:fixtureMembers[$tenant]=@($global:fixtureMembers[$tenant])+$actor
  $global:memberWrites+=($tenant+'/'+$actor)
  return [pscustomobject]@{actorId=$actor}
 }
 throw "Unexpected mocked request: $Method $Uri"
}
& $scriptPath -Action Prepare
$path=Join-Path $testRoot '.artifacts/uat/default/profile.json'
$original=Get-Content $path -Raw | ConvertFrom-Json
$original.PSObject.Properties.Remove('onboarding')
foreach ($name in @('jobAuditEvent','financialAuditEvent','productAuditEvent')) { $original.tokens.PSObject.Properties.Remove($name) }
$original | ConvertTo-Json -Depth 30 | Set-Content $path
$before=Get-Content $path -Raw | ConvertFrom-Json
& $scriptPath -Action Prepare
$after=Get-Content $path -Raw | ConvertFrom-Json
foreach ($name in @('instanceId','postgresPassword','port','identities','databases')) {
 if (($before.$name | ConvertTo-Json -Depth 30 -Compress) -ne ($after.$name | ConvertTo-Json -Depth 30 -Compress)) { throw "Changed retained $name" }
}
foreach ($prop in $before.tokens.PSObject.Properties) { if ($after.tokens.($prop.Name) -ne $prop.Value) { throw 'Rotated token' } }
foreach ($name in @('jobAuditEvent','financialAuditEvent','productAuditEvent')) { if ($after.tokens.$name.Length -ne 64) { throw 'Missing migrated token' } }
$bytes=Get-Content $path -Raw
Set-Content (Join-Path (Split-Path $path) 'members-initialized') $after.instanceId
foreach ($action in @('Prepare','Start','Status','Verify','Stop')) {
 & $scriptPath -Action $action
 if ((Get-Content $path -Raw) -ne $bytes) { throw "Profile changed during $action" }
}
foreach ($expected in @('T001/A100','T001/A300','T002/A100','T002/A300')) {
 if ($expected -notin $global:memberWrites) { throw "Retained profile did not repair $expected" }
}
$memberWriteCount=@($global:memberWrites).Count
if (-not (Test-Path (Join-Path (Split-Path $path) 'reference-members-v2-initialized'))) { throw 'Revised membership marker missing' }
& $scriptPath -Action Start
if (@($global:memberWrites).Count -ne $memberWriteCount) { throw 'Revised membership replay replaced operator state' }
if ((Get-Content $path -Raw) -ne $bytes) { throw 'Profile changed during revised membership replay' }
& $scriptPath -Action Prepare -Profile onboarding -EnableOnboarding -Port 4183
$onboardPath=Join-Path $testRoot '.artifacts/uat/onboarding/profile.json'
$onboard=Get-Content $onboardPath -Raw | ConvertFrom-Json
foreach ($name in @('googleDriveProtection','oneDriveProtection','financialQaProtection','aiModelInternal','jobAuditEvent')) {$onboard.tokens.PSObject.Properties.Remove($name)}
$onboard | ConvertTo-Json -Depth 30 | Set-Content $onboardPath
& $scriptPath -Action Prepare -Profile onboarding -EnableOnboarding
$config=Get-Content (Join-Path (Split-Path $onboardPath) 'integration-gateway.json') -Raw | ConvertFrom-Json
if ([Convert]::FromBase64String($config.Monergy.GoogleDrive.EncryptionKey).Length -ne 32) { throw 'Bad conversion' }
$kycPath=Join-Path $testRoot 'kyc-fixture.json'
@{ T001=@{ Notice='Test-only published review notice; no legal approval claimed.'; Reviewers=@(@{ActorId='A300';Name='Test reviewer'}) } } | ConvertTo-Json -Depth 10 | Set-Content $kycPath
& $scriptPath -Action Prepare -Profile onboarding -EnableOnboarding -KycConfigurationFile $kycPath
$identity=Get-Content (Join-Path (Split-Path $onboardPath) 'customer-identity.json') -Raw | ConvertFrom-Json
if ($identity.Monergy.BasicKyc.Tenants.T001.Reviewers[0].ActorId -ne 'A300' -or $identity.Monergy.BasicKyc.EvidenceBaseUrl -ne 'http://127.0.0.1:5111/') { throw 'KYC configuration was not preserved.' }
$durablePath=Join-Path $testRoot 'evidence-fixture.json'
@{ RuntimeConnections=@{T001='Host=127.0.0.1;Database=fixture_evidence;Username=fixture'}; S3=@{ Endpoint='http://127.0.0.1:9000';Bucket='fixture';AccessKey='fixture';SecretKey='fixture'} } | ConvertTo-Json -Depth 10 | Set-Content $durablePath
& $scriptPath -Action Prepare -Profile onboarding -EnableOnboarding -EvidencePersistenceConfigurationFile $durablePath
$evidence=Get-Content (Join-Path (Split-Path $onboardPath) 'evidence.json') -Raw | ConvertFrom-Json
if ($evidence.Monergy.ReferenceAdapters -ne 'false' -or $evidence.Monergy.Persistence.Provider -ne 'POSTGRESQL_S3' -or $evidence.Monergy.Persistence.Evidence.RuntimeConnections.T001 -notlike '*fixture_evidence*') { throw 'Durable Evidence configuration missing.' }
& $scriptPath -Action Prepare -Profile onboarding -EnableOnboarding
$again=Get-Content (Join-Path (Split-Path $onboardPath) 'evidence.json') -Raw | ConvertFrom-Json
if ($again.Monergy.Persistence.Evidence.S3.Bucket -ne 'fixture' -or $again.Monergy.ReferenceAdapters -ne 'false') { throw 'Durable Evidence silently fell back on repeat start.' }
'PASS: durable Evidence opt-in and retained configuration; no credentials output.'
$classificationPath=Join-Path $testRoot 'classification-fixture.json'
@{ RuntimeConnections=@{T001='Host=127.0.0.1;Database=fixture_classification;Username=fixture'}; Reviewers=@{T001=@('A900')} } | ConvertTo-Json -Depth 10 | Set-Content $classificationPath
& $scriptPath -Action Prepare -Profile onboarding -EnableOnboarding -ClassificationConfigurationFile $classificationPath
$classification=Get-Content (Join-Path (Split-Path $onboardPath) 'document-intelligence.json') -Raw | ConvertFrom-Json
if ($classification.Monergy.Classification.Reviewers.T001[0] -ne 'A900' -or $classification.Monergy.Classification.RuntimeConnections.T001 -notlike '*fixture_classification*') { throw 'Classification configuration missing.' }
& $scriptPath -Action Prepare -Profile onboarding -EnableOnboarding
$classificationAgain=Get-Content (Join-Path (Split-Path $onboardPath) 'document-intelligence.json') -Raw | ConvertFrom-Json
if ($classificationAgain.Monergy.Classification.Reviewers.T001[0] -ne 'A900') { throw 'Classification silently fell back on repeat start.' }
'PASS: classification persistence and explicit reviewer configuration retained.'
# A retained PostgreSQL volume cannot preserve in-memory Evidence/Financial Profile.
$missingDurabilityRejected = $false
try { & $scriptPath -Action Verify -RequireRetainedEvidenceAndFinancialProfile }
catch { $missingDurabilityRejected = $_.Exception.Message -like '*requires both configured durable owner stores*' }
if (-not $missingDurabilityRejected) { throw 'Temporary default UAT incorrectly passed retained-business-history preflight.' }
$financialPath=Join-Path $testRoot 'financial-fixture.json'
@{ RuntimeConnection='Host=127.0.0.1;Database=fixture_financial;Username=fixture_financial_runtime;Password=fixture-only' } | ConvertTo-Json -Depth 10 | Set-Content $financialPath
$conversionBlocked=$false
try { & $scriptPath -Action Prepare -Profile onboarding -EnableOnboarding -FinancialPersistenceConfigurationFile $financialPath }
catch { $conversionBlocked=$_.Exception.Message -like '*Cannot convert a retained in-memory Financial Profile*' }
if (-not $conversionBlocked) { throw 'Existing temporary profile was permitted to convert silently.' }
$retainedFinancialConfig=Get-Content (Join-Path (Split-Path $onboardPath) 'financial-profile.json') -Raw | ConvertFrom-Json
if ($retainedFinancialConfig.Monergy.ReferenceAdapters -ne 'true') { throw 'Rejected storage conversion mutated retained Financial Profile configuration.' }
& $scriptPath -Action Prepare -Profile retained -Port 4191 -EnableOnboarding -EvidencePersistenceConfigurationFile $durablePath -FinancialPersistenceConfigurationFile $financialPath -RequireRetainedEvidenceAndFinancialProfile
$retainedDir=Join-Path $testRoot '.artifacts/uat/retained'
$financial=Get-Content (Join-Path $retainedDir 'financial-profile.json') -Raw | ConvertFrom-Json
if ($financial.Monergy.ReferenceAdapters -ne 'false' -or $financial.Monergy.Persistence.Provider -ne 'POSTGRESQL_S3' -or $financial.Monergy.Persistence.FinancialProfile.RuntimeConnection -notlike '*fixture_financial*') { throw 'Physical Financial Profile opt-in missing.' }
& $scriptPath -Action Prepare -Profile retained -EnableOnboarding -RequireRetainedEvidenceAndFinancialProfile
$financeAgain=Get-Content (Join-Path $retainedDir 'financial-profile.json') -Raw | ConvertFrom-Json
$evidenceAgain=Get-Content (Join-Path $retainedDir 'evidence.json') -Raw | ConvertFrom-Json
if ($financeAgain.Monergy.ReferenceAdapters -ne 'false' -or $evidenceAgain.Monergy.ReferenceAdapters -ne 'false') { throw 'Durable profile silently fell back to reference adapters.' }
'PASS: temporary-profile continuity gate denies, conversion blocked, dual durable opt-in persists on repeated Prepare.'

'PASS: Basic KYC onboarding configuration; syntax; fresh/default/legacy/onboarding configuration; retained membership repair; all non-destructive actions with mocked Docker/readiness; credential preservation; repeat-run stability; encryption conversion.'

} finally { Remove-Item -LiteralPath $testRoot -Recurse -Force }
