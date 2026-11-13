[CmdletBinding()]
param([string]$RepositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')))

$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$profileRoot=Join-Path $RepositoryRoot '.artifacts/uat/p2p-durable-documents'
$profile=Get-Content (Join-Path $profileRoot 'profile.json') -Raw -Encoding utf8|ConvertFrom-Json
$configurationPath=Join-Path $profileRoot 'document-intelligence.json'
$original=[IO.File]::ReadAllBytes($configurationPath)
$project="monergy-uat-$($profile.instanceId.Substring(0,8))"
$compose=@('compose','--env-file',(Join-Path $profileRoot 'compose.env'),'-f',(Join-Path $RepositoryRoot 'build/uat/compose.yml'),'--project-name',$project,'--profile','document-security')
$base="http://127.0.0.1:$($profile.port)"
$artifact=Get-Content (Join-Path $RepositoryRoot '.artifacts/cohort-p/classification-physical.json') -Raw|ConvertFrom-Json
$version=[string]$artifact.review.documentVersionId

function Wait-Ready {
  for($attempt=0;$attempt-lt120;$attempt++){
    try{$r=Invoke-WebRequest "$base/health/ready" -SkipHttpErrorCheck -TimeoutSec 5;if([int]$r.StatusCode-eq200){return}}catch{}
    Start-Sleep -Milliseconds 500
  }
  throw 'Cohort P readiness did not recover after reviewer configuration change.'
}
function Session([string]$tenant,[string]$actor){
  $identity=$profile.identities|Where-Object tenantId -eq $tenant|Where-Object actorId -eq $actor|Select-Object -First 1
  Invoke-RestMethod "$base/identity-api/local/v1/tenant-sessions" -Method Post -ContentType 'application/json' `
    -Headers @{'X-Monergy-Reference-Authentication'=$identity.token} -Body (@{tenantId=$tenant}|ConvertTo-Json -Compress)
}
function Headers([string]$tenant,$session){@{'X-Monergy-Tenant'=$tenant;'X-Monergy-Session'=$session.authenticationContextId}}
function Revoke([string]$tenant,$session){
  Invoke-RestMethod "$base/identity-api/local/v1/tenant-sessions/revoke" -Method Post -ContentType 'application/json' `
    -Body (@{tenantId=$tenant;authenticationContextId=$session.authenticationContextId}|ConvertTo-Json -Compress)|Out-Null
}
function Status([string]$path,[string]$method,$headers,$body=$null){
  $parameters=@{Uri="$base$path";Method=$method;Headers=$headers;SkipHttpErrorCheck=$true;TimeoutSec=15}
  if($null-ne$body){$parameters.ContentType='application/json';$parameters.Body=($body|ConvertTo-Json -Compress)}
  Invoke-WebRequest @parameters
}

$admin=Session 'T001' 'A900'
try{
  $config=Get-Content $configurationPath -Raw -Encoding utf8|ConvertFrom-Json
  $config.Monergy.Classification.Reviewers.T001=@()
  [IO.File]::WriteAllText($configurationPath,($config|ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
  docker @compose up -d --no-deps --force-recreate document-intelligence|Out-Null
  if($LASTEXITCODE-ne0){throw 'Unable to recreate Document Intelligence for unassigned-reviewer proof.'}
  Wait-Ready
  $read=Status '/classification-api/local/v1/administration/classification-reviews?status=all' 'GET' (Headers 'T001' $admin)
  $denied=Status "/classification-api/local/v1/administration/classification-reviews/reference-customer/$version" 'POST' (Headers 'T001' $admin) @{expectedRevision=2;documentClass='PAN';reason='SOURCE_REVIEWED'}
  if([int]$read.StatusCode-ne200-or[int]$denied.StatusCode-ne403-or($denied.Content|ConvertFrom-Json).error-ne'CLASSIFICATION_REVIEW_NOT_GRANTED'){
    throw 'Unassigned Tenant Administrator review boundary failed.'
  }
}
finally{
  [IO.File]::WriteAllBytes($configurationPath,$original)
  docker @compose up -d --no-deps --force-recreate document-intelligence|Out-Null
  if($LASTEXITCODE-ne0){throw 'Unable to restore assigned reviewer configuration.'}
  Wait-Ready
  Revoke 'T001' $admin
}

$t2=Session 'T002' 'A900'
try{
  $foreign=Status "/classification-api/local/v1/administration/classification-reviews/reference-customer/$version" 'GET' (Headers 'T002' $t2)
  if([int]$foreign.StatusCode-ne404){throw 'Cross-tenant classification lookup did not fail closed.'}
}finally{Revoke 'T002' $t2}

foreach($actor in @('A100','A300')){
  $session=Session 'T001' $actor
  try{$response=Status '/classification-api/local/v1/administration/classification-reviews?status=all' 'GET' (Headers 'T001' $session);if([int]$response.StatusCode-ne403){throw "$actor entered classification administration."}}
  finally{Revoke 'T001' $session}
}

$revoked=Session 'T001' 'A900'
$revokedHeaders=Headers 'T001' $revoked
Revoke 'T001' $revoked
$revokedResponse=Status '/classification-api/local/v1/administration/classification-reviews?status=all' 'GET' $revokedHeaders
if([int]$revokedResponse.StatusCode-ne401){throw 'Revoked administrator session was not rejected.'}

$result=[ordered]@{assignedReviewer=200;unassignedRead=200;unassignedReview=403;restoredReviewer=$true;foreignTenant=404;customer=403;advisor=403;revokedSession=401;configurationRestored=([Convert]::ToBase64String([IO.File]::ReadAllBytes($configurationPath))-eq[Convert]::ToBase64String($original))}
if(-not$result.configurationRestored){throw 'Reviewer configuration was not restored byte-for-byte.'}
[IO.File]::WriteAllText((Join-Path $RepositoryRoot '.artifacts/cohort-p/reviewer-boundaries.json'),($result|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
$result|ConvertTo-Json
