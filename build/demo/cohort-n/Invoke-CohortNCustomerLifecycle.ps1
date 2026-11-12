[CmdletBinding()]
param([string]$RepositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')))
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$base='http://127.0.0.1:4291';$privateRoot=Join-Path $RepositoryRoot '.artifacts/cohort-n/private'
$registration=Get-Content (Join-Path $privateRoot 'registration-private.json') -Raw|ConvertFrom-Json
$profile=Get-Content (Join-Path $RepositoryRoot '.artifacts/uat/p2n-admin-connectivity/profile.json') -Raw|ConvertFrom-Json
function Post([string]$path,[object]$body,[hashtable]$headers=@{}){Invoke-WebRequest ($base+$path) -Method Post -ContentType application/json -Headers $headers -Body ($body|ConvertTo-Json -Compress) -SkipHttpErrorCheck -TimeoutSec 30}
$identity=$profile.identities|Where-Object{$_.tenantId-eq'T001'-and$_.actorId-eq'A900'}
$admin=(Post '/identity-api/local/v1/tenant-sessions' @{tenantId='T001'} @{'X-Monergy-Reference-Authentication'=$identity.token}).Content|ConvertFrom-Json
$ha=@{'X-Monergy-Tenant'='T001';'X-Monergy-Session'=$admin.authenticationContextId}
$hc=@{'X-Monergy-Tenant'='T001';'X-Monergy-Session'=$registration.SessionId}
$operation=[Guid]::NewGuid().ToString('N');$suspend=@{operationId=$operation;expectedVersion=[long]$registration.Version;active=$false;reason='Reviewed Cohort N suspension'}
$first=Post "/identity-api/local/v1/administration/customers/$($registration.CustomerId)/status" $suspend $ha
if($first.StatusCode-ne 200){throw "Suspension failed with HTTP $($first.StatusCode)."};$receipt=$first.Content|ConvertFrom-Json
$replay=Post "/identity-api/local/v1/administration/customers/$($registration.CustomerId)/status" $suspend $ha
if($replay.StatusCode-ne 200-or$replay.Content-ne$first.Content){throw 'Customer status exact replay was not immutable.'}
$changed=$suspend.Clone();$changed.reason='Changed intent must conflict'
$conflict=Post "/identity-api/local/v1/administration/customers/$($registration.CustomerId)/status" $changed $ha
if($conflict.StatusCode-ne 409){throw 'Changed customer status replay was not rejected.'}
$profileDenied=Invoke-WebRequest "$base/identity-api/local/v1/me/customer-profile" -Headers $hc -SkipHttpErrorCheck -TimeoutSec 15
$signInDenied=Post '/identity-api/local/v1/customer-registration/sign-in' @{tenantId='T001';email=$registration.Email;password=$registration.Password} @{'X-Monergy-Client-Id'='cohort-n-suspended'}
if($profileDenied.StatusCode-notin@(401,403)-or$signInDenied.StatusCode-ne401){throw 'Suspended Customer retained workspace or sign-in authority.'}
$financialDenied=Invoke-WebRequest "$base/product-api/v1/financial-profile" -Headers $ha -SkipHttpErrorCheck -TimeoutSec 15
if($financialDenied.StatusCode-ne403){throw 'Administrator unexpectedly gained Customer financial authority.'}

$db=$profile.databases|Where-Object{$_.service-eq'customer-identity'-and$_.tenant-eq'T001'}
$sql="SELECT active::text||'|'||owner_actor_id FROM customer_identity.customers WHERE customer_id='$($registration.CustomerId.Replace("'","''"))';"
$ownerState=docker exec -e "PGPASSWORD=$($db.ownerPassword)" monergy-uat-7426abf4-postgres-1 psql -U "$($db.name)_owner" -d $db.name -Atc $sql
if($LASTEXITCODE-ne0-or($ownerState|Out-String).Trim()-ne"true|$($registration.ActorId)"){throw 'Suspension mutated verified Customer ownership.'}

$activate=@{operationId=[Guid]::NewGuid().ToString('N');expectedVersion=[long]$receipt.after.version;active=$true;reason='Reviewed Cohort N reactivation'}
$reactivated=Post "/identity-api/local/v1/administration/customers/$($registration.CustomerId)/status" $activate $ha
if($reactivated.StatusCode-ne200){throw 'Customer reactivation failed.'}
$signedIn=Post '/identity-api/local/v1/customer-registration/sign-in' @{tenantId='T001';email=$registration.Email;password=$registration.Password} @{'X-Monergy-Client-Id'='cohort-n-reactivated';'X-Monergy-Client-Label'='Cohort N installed Chrome';'X-Monergy-Client-Type'='browser'}
if($signedIn.StatusCode-ne200){throw 'Reactivated Customer could not sign in.'};$current=$signedIn.Content|ConvertFrom-Json
$registration.SessionId=$current.authenticationContextId;$registration.Version=[long](($reactivated.Content|ConvertFrom-Json).after.version)
[IO.File]::WriteAllText((Join-Path $privateRoot 'registration-private.json'),($registration|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
$evidence=[ordered]@{Suspension=200;ExactReplay=200;ChangedReplay=[int]$conflict.StatusCode;SuspendedProfile=[int]$profileDenied.StatusCode;SuspendedSignIn=[int]$signInDenied.StatusCode;OwnershipPreserved=$true;AdministratorFinancialAuthority=[int]$financialDenied.StatusCode;Reactivation=[int]$reactivated.StatusCode;RestoredSignIn=[int]$signedIn.StatusCode;Version=$registration.Version}
[IO.File]::WriteAllText((Join-Path $RepositoryRoot '.artifacts/cohort-n/customer-lifecycle-evidence.json'),($evidence|ConvertTo-Json),[Text.UTF8Encoding]::new($false));$evidence|ConvertTo-Json
