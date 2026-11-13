[CmdletBinding()]
param([string]$RepositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')))
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$base='http://127.0.0.1:4311'
$profile=Get-Content (Join-Path $RepositoryRoot '.artifacts/uat/p2p-durable-documents/profile.json') -Raw -Encoding utf8|ConvertFrom-Json
$mailbox=Join-Path $RepositoryRoot '.artifacts/cohort-p/private/mailbox/messages.jsonl'

function Receive-Code([string]$recipient,[DateTimeOffset]$after){
    for($attempt=0;$attempt-lt 30;$attempt++){
        if(Test-Path -LiteralPath $mailbox){
            $record=Get-Content -LiteralPath $mailbox -Encoding utf8|ForEach-Object{$_|ConvertFrom-Json}|Where-Object{$_.Recipient-eq$recipient-and[DateTimeOffset]$_.ReceivedAt-ge$after}|Select-Object -Last 1
            if($null-ne$record-and$record.Message-match'verification code is (?<code>[0-9]{6})'){return $Matches.code}
        }
        Start-Sleep -Seconds 1
    }
    throw "Protected LOCAL SMTP did not deliver the setup challenge for $recipient."
}
function Headers([string]$tenant,[string]$session){@{'X-Monergy-Tenant'=$tenant;'X-Monergy-Session'=$session}}
function Revoke([string]$tenant,[string]$session){
    Invoke-RestMethod "$base/identity-api/local/v1/tenant-sessions/revoke" -Method Post -ContentType application/json -Body (@{tenantId=$tenant;authenticationContextId=$session}|ConvertTo-Json -Compress)|Out-Null
}
function Complete([string]$tenant,[string]$actor){
    $identity=$profile.identities|Where-Object{$_.tenantId-eq$tenant-and$_.actorId-eq$actor}
    if($null-eq$identity){throw "Missing reference identity $tenant/$actor."}
    $reference=Invoke-RestMethod "$base/identity-api/local/v1/tenant-sessions" -Method Post -ContentType application/json -Headers @{'X-Monergy-Reference-Authentication'=$identity.token} -Body (@{tenantId=$tenant}|ConvertTo-Json -Compress)
    try{
        $context=Invoke-RestMethod "$base/product-api/v1/context" -Headers (Headers $tenant $reference.authenticationContextId)
        if($context.setup.ready){return [pscustomobject]@{Tenant=$tenant;Actor=$actor;Ready=$true;Reused=$true;Capabilities=@($context.capabilities).Count}}
    }finally{Revoke $tenant $reference.authenticationContextId}

    $email="$($actor.ToLowerInvariant())@example.test"
    $requested=[DateTimeOffset]::UtcNow
    $challenge=Invoke-RestMethod "$base/identity-api/local/v1/customer-email-access/request" -Method Post -ContentType application/json -Body (@{tenantId=$tenant;email=$email}|ConvertTo-Json -Compress)
    $code=Receive-Code $email $requested
    $session=Invoke-RestMethod "$base/identity-api/local/v1/customer-email-access/confirm" -Method Post -ContentType application/json -Body (@{tenantId=$tenant;challengeId=$challenge.challengeId;code=$code}|ConvertTo-Json -Compress)
    try{
        $headers=Headers $tenant $session.authenticationContextId
        $customer=Invoke-RestMethod "$base/identity-api/local/v1/me/customer-profile" -Headers $headers
        if($customer.version-eq 0){
            Invoke-RestMethod "$base/identity-api/local/v1/me/customer-profile" -Method Put -ContentType application/json -Headers $headers -Body (@{operationId=[guid]::NewGuid().ToString('N');expectedVersion=0;displayName="Cohort P $actor";mobile=$null}|ConvertTo-Json -Compress)|Out-Null
        }
        $agreements=Invoke-RestMethod "$base/identity-api/local/v1/me/account-agreements" -Headers $headers
        if($null-eq$agreements.acceptance){
            Invoke-RestMethod "$base/identity-api/local/v1/me/account-agreements" -Method Post -ContentType application/json -Headers $headers -Body (@{operationId=[guid]::NewGuid().ToString('N');revision=$agreements.publication.revision;acceptTerms=$true;acknowledgePrivacy=$true;consentToProcessing=$true}|ConvertTo-Json -Compress)|Out-Null
        }
        $context=Invoke-RestMethod "$base/product-api/v1/context" -Headers $headers
        if(-not$context.setup.ready-or@($context.capabilities).Count-eq 0){throw "Customer setup did not establish a ready product context for $tenant/$actor."}
        [pscustomobject]@{Tenant=$tenant;Actor=$actor;Ready=$true;Reused=$false;Capabilities=@($context.capabilities).Count}
    }finally{Revoke $tenant $session.authenticationContextId}
}

$results=@(
    Complete 'T001' 'A100'
    Complete 'T001' 'A200'
    Complete 'T002' 'A100'
)
$results|ConvertTo-Json -Depth 4
