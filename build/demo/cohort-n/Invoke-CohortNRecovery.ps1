[CmdletBinding()]
param(
    [string]$RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$base = 'http://127.0.0.1:4291'
$privateRoot = Join-Path $RepositoryRoot '.artifacts/cohort-n/private'
$statePath = Join-Path $privateRoot 'registration-private.json'
$state = Get-Content $statePath -Raw -Encoding utf8 | ConvertFrom-Json
$newPassword = ([Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(24)) + 'aA1!')

function Post([string]$path, [object]$body, [hashtable]$headers = @{}) {
    Invoke-WebRequest ($base + $path) -Method Post -ContentType application/json -Headers $headers `
        -Body ($body | ConvertTo-Json -Depth 8 -Compress) -SkipHttpErrorCheck -TimeoutSec 30
}
function Receive-Code([string]$recipient, [DateTimeOffset]$after) {
    $mailbox = Join-Path $privateRoot 'mailbox/messages.jsonl'
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        if (Test-Path -LiteralPath $mailbox) {
            $record = Get-Content -LiteralPath $mailbox -Encoding utf8 |
                ForEach-Object { $_ | ConvertFrom-Json } |
                Where-Object { $_.Recipient -eq $recipient -and [DateTimeOffset]$_.ReceivedAt -ge $after } |
                Select-Object -Last 1
            if ($null -ne $record -and $record.Message -match 'verification code is (?<code>[0-9]{6})') {
                return $Matches.code
            }
        }
        Start-Sleep -Seconds 1
    }
    throw 'Protected LOCAL SMTP did not deliver the recovery challenge.'
}

$requestedAt = [DateTimeOffset]::UtcNow
$request = Post '/identity-api/local/v1/customer-registration/recovery/request' @{ tenantId='T001'; email=$state.Email }
if ($request.StatusCode -ne 200) { throw "Recovery request failed with HTTP $($request.StatusCode)." }
$challenge = $request.Content | ConvertFrom-Json
$code = Receive-Code $state.Email $requestedAt
$confirmation = Post '/identity-api/local/v1/customer-registration/recovery/confirm' @{
    tenantId='T001'; challengeId=$challenge.challengeId; code=$code; newPassword=$newPassword
}
if ($confirmation.StatusCode -ne 200) { throw "Recovery confirmation failed with HTTP $($confirmation.StatusCode)." }

$replay = Post '/identity-api/local/v1/customer-registration/recovery/confirm' @{
    tenantId='T001'; challengeId=$challenge.challengeId; code=$code; newPassword=$newPassword
}
$oldSession = Invoke-WebRequest "$base/identity-api/local/v1/me/customer-profile" -Headers @{
    'X-Monergy-Tenant'='T001'; 'X-Monergy-Session'=$state.SessionId
} -SkipHttpErrorCheck -TimeoutSec 15
$oldPassword = Post '/identity-api/local/v1/customer-registration/sign-in' @{
    tenantId='T001'; email=$state.Email; password=$state.Password
}
$newSignIn = Post '/identity-api/local/v1/customer-registration/sign-in' @{
    tenantId='T001'; email=$state.Email; password=$newPassword
} @{
    'X-Monergy-Client-Id'='cohort-n-recovery'; 'X-Monergy-Client-Label'='Cohort N installed Chrome'; 'X-Monergy-Client-Type'='browser'
}
if ($replay.StatusCode -ne 401 -or $oldSession.StatusCode -ne 401 -or $oldPassword.StatusCode -ne 401 -or $newSignIn.StatusCode -ne 200) {
    throw "Recovery boundary failed: replay=$($replay.StatusCode) oldSession=$($oldSession.StatusCode) oldPassword=$($oldPassword.StatusCode) newSignIn=$($newSignIn.StatusCode)."
}
$session = $newSignIn.Content | ConvertFrom-Json
$state.Password = $newPassword
$state.SessionId = $session.authenticationContextId
[IO.File]::WriteAllText($statePath, ($state | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))

$evidence = [ordered]@{
    Transport = 'AUTHENTICATED_STARTTLS_LOCAL'
    RecoveryRequest = [int]$request.StatusCode
    RecoveryConfirmation = [int]$confirmation.StatusCode
    Replay = [int]$replay.StatusCode
    PriorSession = [int]$oldSession.StatusCode
    PriorPassword = [int]$oldPassword.StatusCode
    NewPasswordSignIn = [int]$newSignIn.StatusCode
    CustomerId = $state.CustomerId
    ActorId = $state.ActorId
}
$output = Join-Path $RepositoryRoot '.artifacts/cohort-n/recovery-evidence.json'
[IO.File]::WriteAllText($output, ($evidence | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
$evidence
