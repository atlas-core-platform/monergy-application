#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^assistant01b(?:-[a-z0-9-]+)?$')][string]$Profile,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]+$')][string]$Label,
    [Parameter(Mandatory)][switch]$ConfirmGatesABC
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'assistant-01b.owner-common.ps1')
if (-not $ConfirmGatesABC) { throw 'Physical inspection requires the authorized Gate D profile.' }
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$folder=Join-Path $repository ".artifacts/uat/$Profile"
$state=Read-AssistantOwnerProtectedJson (Join-Path $folder 'profile.json')
if ($state.instanceId -notmatch '^[a-f0-9]{32}$') { throw 'Invalid isolated profile identity.' }
$project="monergy-uat-$($state.instanceId.Substring(0,8))"
$ids=@(& docker ps -aq --filter "label=com.docker.compose.project=$project")
if ($LASTEXITCODE -ne 0 -or $ids.Count -eq 0) { throw 'Isolated profile containers are unavailable.' }
# Inspect output stays in memory. Environment and command arrays never enter evidence.
$raw=& docker inspect @ids
if ($LASTEXITCODE -ne 0) { throw 'Container inspection failed.' }
try { $containers=@($raw | ConvertFrom-Json) }
catch { throw 'Container metadata is invalid; raw inspection content is suppressed.' }
$safe=@($containers | ForEach-Object {
    @{id=$_.Id;name=$_.Name.TrimStart('/');service=$_.Config.Labels.'com.docker.compose.service';state=$_.State.Status;
      health=$(if($null -ne $_.State.PSObject.Properties['Health']){$_.State.Health.Status}else{'not-configured'});
      user=$_.Config.User;readOnly=$_.HostConfig.ReadonlyRootfs;capDrop=@($_.HostConfig.CapDrop);
      securityOpt=@($_.HostConfig.SecurityOpt);networkMode=$_.HostConfig.NetworkMode;
      publishedPorts=@($_.NetworkSettings.Ports.PSObject.Properties | Where-Object {$null -ne $_.Value} | ForEach-Object {$_.Name});
      volumes=@($_.Mounts | Where-Object {$_.Type -eq 'volume'} | ForEach-Object {@{name=$_.Name;destination=$_.Destination}})}
})
$runtime=@($containers | Where-Object {$_.Config.Labels.'com.docker.compose.service' -eq 'knowledge-runtime'})
$runtimeReady=$null
if ($runtime.Count -eq 1 -and $runtime[0].State.Running) {
    $health=& docker exec $runtime[0].Id node -e "fetch('http://127.0.0.1:5122/health/ready',{signal:AbortSignal.timeout(3000)}).then(async r=>{const b=await r.json();console.log(JSON.stringify({status:r.status,ready:b.status==='ready',catalogueSha256:b.catalogueSha256}));}).catch(()=>process.exit(1))"
    if ($LASTEXITCODE -eq 0) { $runtimeReady=$health | ConvertFrom-Json }
}
$allVolumes=@(& docker volume ls --format '{{.Name}}')
if ($LASTEXITCODE -ne 0) { throw 'Retained volume inventory failed.' }
$allContainers=@(& docker ps -a --format '{{.ID}} {{.Names}} {{.Status}}')
if ($LASTEXITCODE -ne 0) { throw 'Retained container inventory failed.' }
$output=Join-Path $repository '.artifacts/assistant-01b-testing/browser'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$target=Join-Path $output "snapshot-$Label.json"
if (Test-Path -LiteralPath $target) { throw 'Snapshot exists; select another label to preserve prior evidence.' }
@{schemaVersion=1;observedAt=[DateTimeOffset]::UtcNow.ToString('O');profile=$Profile;instanceId=$state.instanceId;
  containers=$safe;knowledgeRuntime=$runtimeReady;allVolumeNames=($allVolumes | Sort-Object);allContainers=$allContainers} |
  ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $target -Encoding utf8NoBOM
Write-Output "Sanitized read-only snapshot saved: $target"
