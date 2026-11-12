[CmdletBinding()]
param([string]$RepositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')))
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$privateRoot=Join-Path $RepositoryRoot '.artifacts/cohort-n/private';$binaryRoot=Join-Path $RepositoryRoot '.artifacts/cohort-n/provider-harness'
$registration=Get-Content (Join-Path $privateRoot 'registration-private.json') -Raw|ConvertFrom-Json
$inputPath=Join-Path $privateRoot 'provider-harness-input.json'
[IO.File]::WriteAllText($inputPath,(@{TenantId='T001';SessionId=$registration.SessionId}|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
dotnet publish (Join-Path $PSScriptRoot 'provider-harness/CohortNProviderHarness.csproj') -c Release -o $binaryRoot --nologo
if($LASTEXITCODE-ne0){throw 'Provider physical harness failed to compile.'}
$config=Join-Path $RepositoryRoot '.artifacts/uat/p2n-admin-connectivity/integration-gateway.json'
$container='monergy-uat-7426abf4-integration-gateway-1';$workspace='monergy-uat-7426abf4-workspace-1';$image='monergy-local-uat:monergy-uat-7426abf4'
docker stop $container|Out-Null;if($LASTEXITCODE-ne0){throw 'Unable to pause Integration Gateway for exclusive retained-store acceptance.'}
try{
    $output=docker run --rm --network "container:$workspace" --user 1654:1654 --read-only --tmpfs /tmp:rw,noexec,nosuid,size=32m `
      --mount "type=volume,source=monergy-uat-7426abf4_drive-data,target=/var/lib/monergy-drive" `
      --mount "type=bind,source=$($binaryRoot.Replace('\','/')),target=/harness,readonly" `
      --mount "type=bind,source=$($config.Replace('\','/')),target=/run/cohort-n/integration-gateway.json,readonly" `
      --mount "type=bind,source=$($inputPath.Replace('\','/')),target=/run/cohort-n/input.json,readonly" `
      --entrypoint dotnet $image /harness/CohortNProviderHarness.dll /run/cohort-n/integration-gateway.json /run/cohort-n/input.json
    if($LASTEXITCODE-ne0){throw 'Physical provider contract harness failed.'}
}
finally{docker start $container|Out-Null;if($LASTEXITCODE-ne0){throw 'Integration Gateway did not restart after provider acceptance.'}}
$result=($output|Select-Object -Last 1)|ConvertFrom-Json
if($result.GoogleDrive.Replay-ne'EXACT'-or$result.OneDrive.Replay-ne'EXACT'){throw 'Provider replay evidence is incomplete.'}
[IO.File]::WriteAllText((Join-Path $RepositoryRoot '.artifacts/cohort-n/provider-physical-evidence.json'),($result|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
$result|ConvertTo-Json -Depth 8
