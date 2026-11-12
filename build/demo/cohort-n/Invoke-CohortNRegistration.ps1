[CmdletBinding()]
param(
    [string]$RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')),
    [string]$Profile = 'p2n-admin-connectivity'
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$base='http://127.0.0.1:4291'
$profileRoot=Join-Path $RepositoryRoot ".artifacts/uat/$Profile"
$privateRoot=Join-Path $RepositoryRoot '.artifacts/cohort-n/private'
$profileState=Get-Content (Join-Path $profileRoot 'profile.json') -Raw -Encoding utf8|ConvertFrom-Json
$suffix=[Guid]::NewGuid().ToString('N')
$email="cohort-n-$suffix@example.test"
$password=([Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))+'aA1!')

function JsonRequest([string]$path,[object]$body,[hashtable]$headers=@{}){
    Invoke-RestMethod ($base+$path) -Method Post -ContentType application/json -Headers $headers -Body ($body|ConvertTo-Json -Depth 12 -Compress) -TimeoutSec 30
}
function Receive-Code([string]$recipient,[DateTimeOffset]$after){
    $mailbox=Join-Path $privateRoot 'mailbox/messages.jsonl'
    for($attempt=0;$attempt -lt 30;$attempt++){
        if(Test-Path -LiteralPath $mailbox){
            $record=Get-Content -LiteralPath $mailbox -Encoding utf8|ForEach-Object{$_|ConvertFrom-Json}|Where-Object{$_.Recipient -eq $recipient -and [DateTimeOffset]$_.ReceivedAt -ge $after}|Select-Object -Last 1
            if($null-ne$record -and $record.Message -match 'verification code is (?<code>[0-9]{6})'){return $Matches.code}
        }
        Start-Sleep -Seconds 1
    }
    throw 'Protected LOCAL SMTP did not deliver the registration challenge.'
}
function ReferenceSession([string]$actor){
    $identity=$profileState.identities|Where-Object{$_.tenantId -eq 'T001' -and $_.actorId -eq $actor}
    JsonRequest '/identity-api/local/v1/tenant-sessions' @{tenantId='T001'} @{'X-Monergy-Reference-Authentication'=$identity.token}
}

$publication=Invoke-RestMethod "$base/identity-api/local/v1/customer-registration/agreements?tenantId=T001" -TimeoutSec 15
if(@($publication.documents).Count-ne 3 -or @($publication.documents.kind|Sort-Object) -join ',' -ne 'CONSENT,PRIVACY,TERMS'){throw 'Registration agreements are incomplete.'}
$requestedAt=[DateTimeOffset]::UtcNow
$challenge=JsonRequest '/identity-api/local/v1/customer-registration/request' @{tenantId='T001';email=$email}
$code=Receive-Code $email $requestedAt
$completion=JsonRequest '/identity-api/local/v1/customer-registration/confirm' @{
    tenantId='T001';challengeId=$challenge.challengeId;code=$code;displayName='Cohort N Registered Customer';password=$password;
    agreementRevision=$publication.revision;acceptTerms=$true;acknowledgePrivacy=$true;consentToProcessing=$true
}
$session=JsonRequest '/identity-api/local/v1/customer-registration/sign-in' @{tenantId='T001';email=$email;password=$password} @{
    'X-Monergy-Client-Id'='cohort-n-browser';'X-Monergy-Client-Label'='Cohort N installed Chrome';'X-Monergy-Client-Type'='browser'
}
$customerHeaders=@{'X-Monergy-Tenant'='T001';'X-Monergy-Session'=$session.authenticationContextId}
$profileResponse=Invoke-WebRequest "$base/identity-api/local/v1/me/customer-profile" -Headers $customerHeaders -SkipHttpErrorCheck -TimeoutSec 15
if($profileResponse.StatusCode-ne 200){throw 'Registered Customer could not read the Customer owner profile.'}
$admin=ReferenceSession 'A900'
$adminHeaders=@{'X-Monergy-Tenant'='T001';'X-Monergy-Session'=$admin.authenticationContextId}
$directory=Invoke-RestMethod "$base/identity-api/local/v1/administration/customers?search=$suffix" -Headers $adminHeaders -TimeoutSec 15
$row=@($directory.items|Where-Object{$_.customerId -eq $completion.customerId})
if($row.Count-ne 1 -or $row[0].active-ne $true){throw 'Registered Customer did not appear active in Customer Management.'}
$impersonation=Invoke-WebRequest "$base/identity-api/local/v1/me/customer-profile" -Headers $adminHeaders -SkipHttpErrorCheck -TimeoutSec 15
if($impersonation.StatusCode-ne 403){throw 'Administrator unexpectedly obtained Customer self-profile authority.'}

$private=[ordered]@{Email=$email;Password=$password;CustomerId=$completion.customerId;ActorId=$completion.actorId;SessionId=$session.authenticationContextId;Version=[long]$row[0].version;AdminSessionId=$admin.authenticationContextId}
[IO.File]::WriteAllText((Join-Path $privateRoot 'registration-private.json'),($private|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
$evidence=[ordered]@{Transport='AUTHENTICATED_STARTTLS_LOCAL';AgreementKinds=3;Registration='COMPLETED';Enrollment='COMPLETED';SignIn='COMPLETED';CustomerManagementVisible=$true;AdministratorImpersonationStatus=[int]$impersonation.StatusCode;CustomerId=$completion.customerId;ActorId=$completion.actorId;CredentialVersion=1}
[IO.File]::WriteAllText((Join-Path $RepositoryRoot '.artifacts/cohort-n/registration-evidence.json'),($evidence|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
$evidence|ConvertTo-Json -Depth 6
