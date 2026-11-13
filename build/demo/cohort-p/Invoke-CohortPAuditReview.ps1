[CmdletBinding()]
param([string]$RepositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')))

$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$profile=Get-Content (Join-Path $RepositoryRoot '.artifacts/uat/p2p-durable-documents/profile.json') -Raw|ConvertFrom-Json
$artifact=Get-Content (Join-Path $RepositoryRoot '.artifacts/cohort-p/classification-physical.json') -Raw|ConvertFrom-Json
$version=[string]$artifact.unclassified.'unsupported-layout'.documentVersionId
$identity=$profile.identities|Where-Object tenantId -eq 'T001'|Where-Object actorId -eq 'A900'|Select-Object -First 1
$base="http://127.0.0.1:$($profile.port)"
$session=Invoke-RestMethod "$base/identity-api/local/v1/tenant-sessions" -Method Post -ContentType 'application/json' `
  -Headers @{'X-Monergy-Reference-Authentication'=$identity.token} -Body (@{tenantId='T001'}|ConvertTo-Json -Compress)
$headers=@{'X-Monergy-Tenant'='T001';'X-Monergy-Session'=$session.authenticationContextId}
try{
  $before=[int](docker exec monergy-uat-081b4d91-postgres-1 psql -U postgres -d uat_audit_t001 -Atc "select count(*) from audit.product_activity;")
  $response=Invoke-WebRequest "$base/classification-api/local/v1/administration/classification-reviews/reference-customer/$version" `
    -Method Post -Headers $headers -ContentType 'application/json' -Body (@{expectedRevision=1;documentClass='OTHER';reason='OTHER_DOCUMENT'}|ConvertTo-Json -Compress) `
    -SkipHttpErrorCheck -TimeoutSec 15
  if([int]$response.StatusCode-ne200){throw 'Fresh physical classification review did not succeed.'}
  $after=[int](docker exec monergy-uat-081b4d91-postgres-1 psql -U postgres -d uat_audit_t001 -Atc "select count(*) from audit.product_activity;")
  if($after-$before-ne2){throw "Classification review produced $($after-$before) audit rows instead of two."}
  $rows=@(docker exec monergy-uat-081b4d91-postgres-1 psql -U postgres -d uat_audit_t001 -Atc "select concat_ws('|',coalesce(record->>'requestId',''),coalesce(record->>'phase',''),coalesce(record->>'outcome',''),coalesce(record->>'actorId',''),coalesce(record->>'tenantId',''),coalesce(record->>'action',''),coalesce(record->>'reason',''),coalesce(record->>'customerId',''),coalesce(record->>'resourceId','')) from audit.product_activity order by recorded_at desc limit 2;")
  $parts=@($rows|ForEach-Object{,($_-split'\|')})
  if($parts.Count-ne2-or$parts[0][0]-ne$parts[1][0]-or@($parts|Where-Object{$_[3]-ne'A900'-or$_[4]-ne'T001'-or$_[5]-ne'API_ACCESS'}).Count-ne0){
    throw 'Classification review Audit identity is incomplete.'
  }
  $phases=@($parts|ForEach-Object{$_[1]}|Sort-Object)
  $outcomes=@($parts|ForEach-Object{$_[2]}|Sort-Object)
  $reasons=@($parts|ForEach-Object{$_[6]})
  if(($phases-join',')-ne'REQUESTED,RESULT'-or$outcomes-notcontains'HTTP_SUCCEEDED'-or$reasons-notcontains'POST'-or$reasons-notcontains'HTTP_200'){
    throw 'Classification review Audit request/result phases are incomplete.'
  }
  $sensitive=[int](docker exec monergy-uat-081b4d91-postgres-1 psql -U postgres -d uat_audit_t001 -Atc "select count(*) from audit.product_activity where record ?| array['password','token','authorization','headers','body','content','ocr','pan','aadhaar','reviewBody'];")
  if($sensitive-ne0){throw 'Audit records contain a prohibited sensitive payload key.'}
  $result=[ordered]@{reviewStatus=200;auditRows=2;requestResultPair=$true;actor='A900';tenant='T001';action='API_ACCESS';method='POST';resourceReference='OMITTED_BY_BOUNDED_CONTRACT';sensitiveKeys=0;documentBodyRetained=$false}
  [IO.File]::WriteAllText((Join-Path $RepositoryRoot '.artifacts/cohort-p/audit-review.json'),($result|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
  $result|ConvertTo-Json
}
finally{
  Invoke-RestMethod "$base/identity-api/local/v1/tenant-sessions/revoke" -Method Post -ContentType 'application/json' `
    -Body (@{tenantId='T001';authenticationContextId=$session.authenticationContextId}|ConvertTo-Json -Compress)|Out-Null
}
