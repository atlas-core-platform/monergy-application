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
& $scriptPath -Action Prepare -Profile onboarding -EnableOnboarding -Port 4183
$onboardPath=Join-Path $testRoot '.artifacts/uat/onboarding/profile.json'
$onboard=Get-Content $onboardPath -Raw | ConvertFrom-Json
foreach ($name in @('googleDriveProtection','oneDriveProtection','financialQaProtection','aiModelInternal','jobAuditEvent')) {$onboard.tokens.PSObject.Properties.Remove($name)}
$onboard | ConvertTo-Json -Depth 30 | Set-Content $onboardPath
& $scriptPath -Action Prepare -Profile onboarding -EnableOnboarding
$config=Get-Content (Join-Path (Split-Path $onboardPath) 'integration-gateway.json') -Raw | ConvertFrom-Json
if ([Convert]::FromBase64String($config.Monergy.GoogleDrive.EncryptionKey).Length -ne 32) { throw 'Bad conversion' }
'PASS: syntax; fresh/default/legacy/onboarding configuration; all non-destructive actions with mocked Docker/readiness; credential preservation; repeat-run stability; encryption conversion.'

} finally { Remove-Item -LiteralPath $testRoot -Recurse -Force }
