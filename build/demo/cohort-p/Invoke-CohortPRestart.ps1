[CmdletBinding()]
param([string]$RepositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')))

$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$profile=Get-Content (Join-Path $RepositoryRoot '.artifacts/uat/p2p-durable-documents/profile.json') -Raw|ConvertFrom-Json
$project="monergy-uat-$($profile.instanceId.Substring(0,8))"
$base="http://127.0.0.1:$($profile.port)"
$external=@('monergy-p2p-20261113-postgres-1','monergy-p2p-20261113-seaweedfs-1')
$artifacts=Join-Path $RepositoryRoot '.artifacts/cohort-p'
$reviewVersion=(Get-Content (Join-Path $artifacts 'classification-physical.json') -Raw|ConvertFrom-Json).review.documentVersionId

function Session([string]$actor){
  $identity=$profile.identities|Where-Object tenantId -eq 'T001'|Where-Object actorId -eq $actor|Select-Object -First 1
  Invoke-RestMethod "$base/identity-api/local/v1/tenant-sessions" -Method Post -ContentType 'application/json' `
    -Headers @{'X-Monergy-Reference-Authentication'=$identity.token} -Body (@{tenantId='T001'}|ConvertTo-Json -Compress)
}
function Headers($session){@{'X-Monergy-Tenant'='T001';'X-Monergy-Session'=$session.authenticationContextId}}
function Revoke($session){Invoke-RestMethod "$base/identity-api/local/v1/tenant-sessions/revoke" -Method Post -ContentType 'application/json' -Body (@{tenantId='T001';authenticationContextId=$session.authenticationContextId}|ConvertTo-Json -Compress)|Out-Null}
function Wait-Container([string]$name){
  for($attempt=0;$attempt-lt120;$attempt++){
    $state=docker inspect $name --format '{{json .State}}'|ConvertFrom-Json
    $health=$state.PSObject.Properties['Health']
    if($state.Running-and($null-eq$health-or$health.Value.Status-eq'healthy')){return}
    Start-Sleep -Milliseconds 500
  }
  throw "$name did not recover."
}
function Ready {
  for($attempt=0;$attempt-lt180;$attempt++){
    try{$response=Invoke-WebRequest "$base/health/ready" -SkipHttpErrorCheck -TimeoutSec 5;if([int]$response.StatusCode-eq200){$body=$response.Content|ConvertFrom-Json;if($body.serviceCount-ne14){throw 'Unexpected readiness service count.'};return $body}}catch{}
    Start-Sleep -Milliseconds 500
  }
  throw 'Cohort P aggregate readiness did not recover.'
}
function Hash([string]$value){$bytes=[Text.Encoding]::UTF8.GetBytes($value);[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()}
function ClassificationHash { $value=docker exec monergy-p2p-20261113-postgres-1 psql -U p2p_admin -d monergy_document_intelligence -Atc "select record::text from public.document_classifications where record->>'DocumentVersionId'='$reviewVersion';"; if($LASTEXITCODE-ne0-or[string]::IsNullOrWhiteSpace($value)){throw 'Classification review row is unavailable.'};Hash ($value|Out-String) }
function Counts {
  [ordered]@{
    evidenceT001=[int](docker exec monergy-p2p-20261113-postgres-1 psql -U p2p_admin -d monergy_evidence -Atc 'select count(*) from evidence.document_versions;')
    evidenceT002=[int](docker exec monergy-p2p-20261113-postgres-1 psql -U p2p_admin -d monergy_evidence_t002 -Atc 'select count(*) from evidence.document_versions;')
    classificationT001=[int](docker exec monergy-p2p-20261113-postgres-1 psql -U p2p_admin -d monergy_document_intelligence -Atc 'select count(*) from public.document_classifications;')
    classificationT002=[int](docker exec monergy-p2p-20261113-postgres-1 psql -U p2p_admin -d monergy_document_intelligence_t002 -Atc 'select count(*) from public.document_classifications;')
  }
}
function Readback([string]$name){
  dotnet run --project (Join-Path $RepositoryRoot 'build/demo/cohort-p/readback-harness/CohortPReadbackHarness.csproj') -c Release -- `
    (Join-Path $artifacts 'private/evidence-persistence.json') (Join-Path $artifacts 'storage-exact-version.json') `
    (Join-Path $artifacts 'provider-physical-evidence.json') (Join-Path $artifacts 'classification-physical.json') (Join-Path $artifacts $name)|Out-Null
  if($LASTEXITCODE-ne0){throw 'Cohort P durable readback failed.'}
  Get-Content (Join-Path $artifacts $name) -Raw
}
function ContainerIds([string[]]$names){$map=[ordered]@{};foreach($name in $names){$map[$name]=docker inspect $name --format '{{.Id}}'};$map}

$customer=Session 'A100';$admin=Session 'A900'
try{
  $readyBefore=Ready
  $topologyBefore=Invoke-RestMethod "$base/uat-api/v1/topology" -Headers (Headers $admin) -TimeoutSec 15
  if(@($topologyBefore.services).Count-ne14-or@($topologyBefore.services|Where-Object{$_.available}).Count-ne14){throw 'Pre-restart topology is not 14/14.'}
  $appContainers=@(docker ps --filter "label=com.docker.compose.project=$project" --format '{{.Names}}')
  $allContainers=@($appContainers+$external|Sort-Object -Unique)
  $idsBefore=ContainerIds $allContainers
  $volumesBefore=@(docker volume ls --format '{{.Name}}'|Where-Object{$_-like"$project*"-or$_-like'monergy-p2p-20261113*'}|Sort-Object)
  $before=[ordered]@{readback=(Readback 'pre-restart-readback.json');classificationHash=(ClassificationHash);counts=(Counts)}

  $services=@($appContainers|Where-Object{$_-notmatch'-(postgres|workspace)-1$'})
  $workspace=@($appContainers|Where-Object{$_-match'-workspace-1$'})
  $postgres=@($appContainers|Where-Object{$_-match'-postgres-1$'})
  docker stop $services|Out-Null;if($LASTEXITCODE-ne0){throw 'Could not stop Cohort P service containers.'}
  docker stop $workspace|Out-Null;if($LASTEXITCODE-ne0){throw 'Could not stop Cohort P workspace container.'}
  docker stop $postgres|Out-Null;if($LASTEXITCODE-ne0){throw 'Could not stop Cohort P profile PostgreSQL.'}
  docker stop $external|Out-Null;if($LASTEXITCODE-ne0){throw 'Could not stop Cohort P owner infrastructure.'}

  docker start $external|Out-Null;if($LASTEXITCODE-ne0){throw 'Could not restart Cohort P owner infrastructure.'}
  foreach($name in $external){Wait-Container $name}
  docker start $postgres|Out-Null;if($LASTEXITCODE-ne0){throw 'Could not restart Cohort P profile PostgreSQL.'};Wait-Container ($postgres[0])
  docker start $workspace|Out-Null;if($LASTEXITCODE-ne0){throw 'Could not restart Cohort P workspace.'};Wait-Container ($workspace[0])
  docker start $services|Out-Null;if($LASTEXITCODE-ne0){throw 'Could not restart Cohort P services.'}
  $readyAfter=Ready

  $after=[ordered]@{readback=(Readback 'post-restart-readback.json');classificationHash=(ClassificationHash);counts=(Counts)}
  $idsAfter=ContainerIds $allContainers
  $volumesAfter=@(docker volume ls --format '{{.Name}}'|Where-Object{$_-like"$project*"-or$_-like'monergy-p2p-20261113*'}|Sort-Object)
  if(($before.readback -ne $after.readback) -or $before.classificationHash -ne $after.classificationHash -or ($before.counts|ConvertTo-Json -Compress) -ne ($after.counts|ConvertTo-Json -Compress)){
    throw 'Durable Evidence or classification state drifted across restart.'
  }
  if(($idsBefore|ConvertTo-Json -Compress) -ne ($idsAfter|ConvertTo-Json -Compress) -or ($volumesBefore-join'|') -ne ($volumesAfter-join'|')){throw 'Container or volume identity changed across restart.'}
  $customerContext=Invoke-WebRequest "$base/product-api/v1/context" -Headers (Headers $customer) -SkipHttpErrorCheck -TimeoutSec 15
  $topologyAfter=Invoke-RestMethod "$base/uat-api/v1/topology" -Headers (Headers $admin) -TimeoutSec 15
  if([int]$customerContext.StatusCode-ne200-or@($topologyAfter.services).Count-ne14-or@($topologyAfter.services|Where-Object{$_.available}).Count-ne14){throw 'Sessions or topology did not survive restart.'}
  $result=[ordered]@{sameContainers=$true;containerCount=$allContainers.Count;sameVolumes=$true;volumeCount=$volumesAfter.Count;readinessBefore=$readyBefore.serviceCount;readinessAfter=$readyAfter.serviceCount;topologyBefore=14;topologyAfter=14;customerSession=200;counts=$after.counts;classificationHashExact=$true;durableReadbackExact=$true;mutationDrift=0}
  [IO.File]::WriteAllText((Join-Path $artifacts 'restart-evidence.json'),($result|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
  $result|ConvertTo-Json -Depth 8
}
finally{try{Revoke $customer}catch{};try{Revoke $admin}catch{}}
