[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$fixture='assistant-command-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
$folder=[IO.Path]::GetFullPath((Join-Path $repository ".artifacts/uat/$fixture"))
$allowedRoot=[IO.Path]::GetFullPath((Join-Path $repository '.artifacts/uat'))+[IO.Path]::DirectorySeparatorChar
if (-not $folder.StartsWith($allowedRoot,[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $folder)) { throw 'Unsafe command fixture path.' }
$global:AssistantCommandCapture=$null
function docker {
    $global:AssistantCommandCapture=@{ arguments=@($args); input=($input | Out-String) }
    $global:LASTEXITCODE=0
}
$command=Join-Path $PSScriptRoot 'Invoke-MonergyAssistantReviewer.ps1'
$operation=[Guid]::NewGuid()
$event=[Guid]::NewGuid()
$count=0
function Assert-Command([string]$mode,$expected) {
    if ($global:AssistantCommandCapture.arguments[-1] -ne $mode) { throw 'Wrong protected owner CLI mode.' }
    if ($global:AssistantCommandCapture.arguments -contains 'assistant-operator-control') { throw 'Operator secret was forwarded as a command argument.' }
    if ($null -eq $expected) {
        if (-not [string]::IsNullOrWhiteSpace($global:AssistantCommandCapture.input)) { throw 'Status must not send command content.' }
    } else {
        $actual=$global:AssistantCommandCapture.input | ConvertFrom-Json
        if (@($actual.PSObject.Properties).Count -ne $expected.Count) { throw 'Unexpected command fields.' }
        foreach($key in $expected.Keys) { if ($actual.$key -ne $expected[$key]) { throw "Wrong protected command field: $key" } }
    }
    $script:count++
}
try {
    New-Item -ItemType Directory -Path $folder | Out-Null
    # Deliberately no credentials, databases or live profile; only CLI serialization is tested here.
    [IO.File]::WriteAllText((Join-Path $folder 'profile.json'),'{"assistant":{"enabled":true},"instanceId":"01234567890123456789012345678901"}')
    & $command -Profile $fixture -Status
    Assert-Command '--platform-reviewer-status' $null
    & $command -Profile $fixture -Discover
    Assert-Command '--platform-reviewer-discover' @{after=$null;limit=20;state=$null}
    & $command -Profile $fixture -Discover -DeliveryState Paused -Limit 5 -After $event.ToString()
    Assert-Command '--platform-reviewer-discover' @{after=$event.ToString();limit=5;state='Paused'}
    & $command -Profile $fixture -Operation -OperationId $operation
    Assert-Command '--platform-reviewer-operation' @{operationId=$operation.ToString()}
    & $command -Profile $fixture -EventId $event -OperationId $operation -ExpectedRecoveryCount 2
    Assert-Command '--platform-reviewer-resume' @{eventId=$event.ToString();operationId=$operation.ToString();expectedRecoveryCount=2}
    foreach($action in @('Grant','Revoke')) {
        & $command -Profile $fixture -Action $action -PrincipalId 'synthetic-principal' -OperationId $operation -ExpectedPolicyRevision 2 -ReasonCode SECURITY_RESPONSE
        Assert-Command '--platform-reviewer-command' @{contractVersion='1.0.0';operationId=$operation.ToString();principalId='synthetic-principal';capability='platform.assistant.feedback.review';action=$action;expectedPolicyRevision=2;reasonCode='SECURITY_RESPONSE'}
    }
    $rejected=$false
    try { & $command -Profile $fixture -EventId $event -OperationId $operation -ExpectedRecoveryCount 3 } catch { $rejected=$true }
    if(-not $rejected){throw 'Recovery bound was not enforced.'}; $count++
    Write-Output "Protected reviewer command checks: $count passed; 0 failed. No Docker invocation or grant performed."
} finally {
    $global:AssistantCommandCapture=$null
    if (Test-Path -LiteralPath $folder) { Remove-Item -LiteralPath $folder -Recurse -Force }
}
