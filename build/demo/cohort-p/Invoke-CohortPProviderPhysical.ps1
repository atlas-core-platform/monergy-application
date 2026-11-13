[CmdletBinding()]
param([string]$RepositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')))

$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest

$profilePath=Join-Path $RepositoryRoot '.artifacts/uat/p2p-durable-documents/profile.json'
$profile=Get-Content -LiteralPath $profilePath -Raw -Encoding utf8|ConvertFrom-Json
$identity=$profile.identities|Where-Object tenantId -eq 'T001'|Where-Object actorId -eq 'A100'|Select-Object -First 1
if($null-eq$identity){throw 'Cohort P T001/A100 reference identity is unavailable.'}
$base="http://127.0.0.1:$($profile.port)"
$session=Invoke-RestMethod -Method Post -Uri "$base/identity-api/local/v1/tenant-sessions" `
  -Headers @{'X-Monergy-Reference-Authentication'=$identity.token} `
  -ContentType 'application/json' -Body (@{tenantId='T001'}|ConvertTo-Json -Compress)

$privateRoot=Join-Path $RepositoryRoot '.artifacts/cohort-p/private'
$binaryRoot=Join-Path $RepositoryRoot '.artifacts/cohort-p/provider-harness'
$inputPath=Join-Path $privateRoot 'provider-harness-input.json'
[IO.Directory]::CreateDirectory($privateRoot)|Out-Null
[IO.File]::WriteAllText($inputPath,(@{TenantId='T001';SessionId=$session.authenticationContextId}|ConvertTo-Json),[Text.UTF8Encoding]::new($false))

dotnet publish (Join-Path $RepositoryRoot 'build/demo/cohort-n/provider-harness/CohortNProviderHarness.csproj') -c Release -o $binaryRoot --nologo
if($LASTEXITCODE-ne0){throw 'Provider physical harness failed to compile.'}

$shortId=$profile.instanceId.Substring(0,8)
$project="monergy-uat-$shortId"
$config=Join-Path $RepositoryRoot '.artifacts/uat/p2p-durable-documents/integration-gateway.json'
$container="$project-integration-gateway-1"
$workspace="$project-workspace-1"
$image="monergy-local-uat:$project"

docker stop $container|Out-Null
if($LASTEXITCODE-ne0){throw 'Unable to pause the Cohort P Integration Gateway for exclusive retained-store acceptance.'}
try{
    $output=docker run --rm --network "container:$workspace" --user 1654:1654 --read-only --tmpfs /tmp:rw,noexec,nosuid,size=32m `
      --mount "type=volume,source=$($project)_drive-data,target=/var/lib/monergy-drive" `
      --mount "type=bind,source=$($binaryRoot.Replace('\','/')),target=/harness,readonly" `
      --mount "type=bind,source=$($config.Replace('\','/')),target=/run/cohort-p/integration-gateway.json,readonly" `
      --mount "type=bind,source=$($inputPath.Replace('\','/')),target=/run/cohort-p/input.json,readonly" `
      --entrypoint dotnet $image /harness/CohortNProviderHarness.dll /run/cohort-p/integration-gateway.json /run/cohort-p/input.json
    if($LASTEXITCODE-ne0){throw 'Physical provider contract harness failed.'}
}
finally{
    docker start $container|Out-Null
    if($LASTEXITCODE-ne0){throw 'Integration Gateway did not restart after provider acceptance.'}
    try{
      Invoke-RestMethod -Method Post -Uri "$base/identity-api/local/v1/tenant-sessions/revoke" -ContentType 'application/json' `
        -Body (@{tenantId='T001';authenticationContextId=$session.authenticationContextId}|ConvertTo-Json -Compress)|Out-Null
    }catch{}
}

$result=($output|Select-Object -Last 1)|ConvertFrom-Json
if($result.GoogleDrive.Replay-ne'EXACT'-or$result.OneDrive.Replay-ne'EXACT'){throw 'Provider replay evidence is incomplete.'}
[IO.File]::WriteAllText((Join-Path $RepositoryRoot '.artifacts/cohort-p/provider-physical-evidence.json'),($result|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
$result|ConvertTo-Json -Depth 8
