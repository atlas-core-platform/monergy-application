[CmdletBinding()]
param([string]$RepositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')))
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$base='http://127.0.0.1:4301'
$profile=Get-Content (Join-Path $RepositoryRoot '.artifacts/uat/p2o-customer-completion/profile.json') -Raw|ConvertFrom-Json
$privateRoot=Join-Path $RepositoryRoot '.artifacts/cohort-o/private'
$privatePath=Join-Path $privateRoot 'login-private.json'
$mailbox=Join-Path $privateRoot 'mailbox/messages.jsonl'

function Post([string]$path,[object]$body,[hashtable]$headers=@{}) {
  Invoke-WebRequest ($base+$path) -Method Post -ContentType application/json -Headers $headers `
    -Body ($body|ConvertTo-Json -Depth 8 -Compress) -SkipHttpErrorCheck -TimeoutSec 30
}
function Body($response){if([string]::IsNullOrWhiteSpace($response.Content)){return $null};$response.Content|ConvertFrom-Json}
function New-Password { ([Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))+'aA1!') }
function Code([string]$recipient,[DateTimeOffset]$after) {
  for($attempt=0;$attempt-lt 30;$attempt++){
    if(Test-Path $mailbox){
      $message=Get-Content $mailbox|ForEach-Object{$_|ConvertFrom-Json}|Where-Object{$_.Recipient-eq$recipient-and[DateTimeOffset]$_.ReceivedAt-ge$after}|Select-Object -Last 1
      if($null-ne$message-and$message.Message-match'verification code is (?<code>[0-9]{6})'){return $Matches.code}
    }
    Start-Sleep -Seconds 1
  }
  throw 'Protected LOCAL SMTP did not deliver the persona challenge.'
}
function SignIn([string]$email,[string]$password,[string]$tenant='T001') {
  Post '/identity-api/local/v1/persona-credentials/sign-in' @{tenantId=$tenant;email=$email;password=$password} @{
    'X-Monergy-Client-Id'='cohort-o-installed-chrome';'X-Monergy-Client-Label'='Cohort O installed Chrome';'X-Monergy-Client-Type'='browser'}
}

$identity=$profile.identities|Where-Object{$_.tenantId-eq'T001'-and$_.actorId-eq'A100'}
$session=Body (Post '/identity-api/local/v1/tenant-sessions' @{tenantId='T001'} @{'X-Monergy-Reference-Authentication'=$identity.token})
$headers=@{'X-Monergy-Tenant'='T001';'X-Monergy-Session'=$session.authenticationContextId}
$self=Invoke-RestMethod ($base+'/identity-api/local/v1/me/customer-profile') -Headers $headers -TimeoutSec 15
$email=$self.email
$activated=$false
$restoredFromLockout=$false
$password=$null
if(Test-Path $privatePath){
  $existing=Get-Content $privatePath -Raw|ConvertFrom-Json
  $email=$existing.Email;$password=$existing.Password
  $probe=SignIn $existing.Email $existing.Password
  if($probe.StatusCode-eq 200){$activated=$true}
  elseif($probe.StatusCode-eq 401){
    # A credential lockout intentionally returns the same bounded response as
    # an incorrect password. Recover the already-activated retained identity;
    # do not incorrectly fall back to the activation-only mailbox path.
    $replacement=New-Password;$requested=[DateTimeOffset]::UtcNow
    $challenge=Body (Post '/identity-api/local/v1/persona-credentials/recovery/request' @{tenantId='T001';email=$email})
    $code=Code $email $requested
    $recovery=Post '/identity-api/local/v1/persona-credentials/recovery/confirm' @{
      tenantId='T001';challengeId=$challenge.challengeId;code=$code;newPassword=$replacement}
    if($recovery.StatusCode-eq 200){$password=$replacement;$activated=$true;$restoredFromLockout=$true}
  }
}
if(-not$activated){
  $password=New-Password;$requested=[DateTimeOffset]::UtcNow
  $challenge=Body (Post '/identity-api/local/v1/persona-credentials/activation/request' @{tenantId='T001';email=$email})
  $code=Code $email $requested
  $confirmation=Post '/identity-api/local/v1/persona-credentials/activation/confirm' @{
    tenantId='T001';challengeId=$challenge.challengeId;code=$code;password=$password}
  if($confirmation.StatusCode-eq 200){$activated=$true}
}

if($restoredFromLockout){
  $final=SignIn $email $password
  if($final.StatusCode-ne 200){throw 'Recovered locked customer password could not sign in.'}
  $finalSession=(Body $final).authenticationContextId
  [IO.File]::WriteAllText($privatePath,(@{Email=$email;Password=$password;SessionId=$finalSession}|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
  [pscustomobject]@{Activation='ALREADY_ACTIVE';Recovery='LOCKOUT_RESTORED';ValidSignIn=200;CredentialInPrivateStore=$true}|ConvertTo-Json
  return
}

# Exercise the supported recovery path and session-revocation boundary. The final
# credential remains protected and is used only by the installed-browser suite.
$before=SignIn $email $password
if($before.StatusCode-ne 200){throw 'Customer password activation did not produce a valid sign-in.'}
$beforeSession=(Body $before).authenticationContextId
$finalPassword=New-Password;$requested=[DateTimeOffset]::UtcNow
$challenge=Body (Post '/identity-api/local/v1/persona-credentials/recovery/request' @{tenantId='T001';email=$email})
$code=Code $email $requested
$recovery=Post '/identity-api/local/v1/persona-credentials/recovery/confirm' @{
  tenantId='T001';challengeId=$challenge.challengeId;code=$code;newPassword=$finalPassword}
if($recovery.StatusCode-ne 200){throw 'Customer password recovery failed.'}
if((SignIn $email $password).StatusCode-ne 401){throw 'The superseded password remained usable.'}
$final=SignIn $email $finalPassword
if($final.StatusCode-ne 200){throw 'Recovered customer password could not sign in.'}
$finalSession=(Body $final).authenticationContextId
$revoked=Invoke-WebRequest ($base+'/identity-api/local/v1/me/customer-profile') -Headers @{
  'X-Monergy-Tenant'='T001';'X-Monergy-Session'=$beforeSession} -SkipHttpErrorCheck -TimeoutSec 15
if($revoked.StatusCode-ne 401){throw 'Recovery did not revoke the previously issued session.'}
$wrong=SignIn $email ($finalPassword+'x')
$foreign=SignIn $email $finalPassword 'T002'
if($wrong.StatusCode-ne 401-or$foreign.StatusCode-ne 401){throw 'Password or tenant boundary failed closed.'}
[IO.File]::WriteAllText($privatePath,(@{Email=$email;Password=$finalPassword;SessionId=$finalSession}|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
[pscustomobject]@{Activation='COMPLETED';Recovery='COMPLETED';ValidSignIn=200;InvalidPassword=401;ForeignTenant=401;PreviousSession=401;CredentialInPrivateStore=$true}|ConvertTo-Json
