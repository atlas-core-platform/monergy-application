[CmdletBinding()]
param(
    [string]$RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
)

$ErrorActionPreference = 'Stop'
$base = 'http://127.0.0.1:4291'
$private = Get-Content (Join-Path $RepositoryRoot '.artifacts/cohort-n/private/registration-private.json') -Raw | ConvertFrom-Json
$adminHeaders = @{ 'X-Monergy-Tenant'='T001'; 'X-Monergy-Session'=$private.AdminSessionId }
$customerHeaders = @{ 'X-Monergy-Tenant'='T001'; 'X-Monergy-Session'=$private.SessionId }
$prefix = 'monergy-uat-7426abf4-'
$cases = @(
    @{ owner='Customer & Identity'; container=$prefix+'customer-identity-1'; path='/identity-api/local/v1/administration/customers'; headers=$adminHeaders },
    @{ owner='Access Management'; container=$prefix+'access-management-1'; path='/access-api/v1/administration/members?limit=1'; headers=$adminHeaders },
    @{ owner='Job Management'; container=$prefix+'job-management-1'; path='/jobs-api/local/v1/administration/processing'; headers=$adminHeaders },
    @{ owner='Evidence'; container=$prefix+'evidence-1'; path='/evidence-api/local/v1/administration/documents'; headers=$adminHeaders },
    @{ owner='Document Intelligence'; container=$prefix+'document-intelligence-1'; path='/classification-api/local/v1/administration/document-classifications'; headers=$adminHeaders },
    @{ owner='Audit'; container=$prefix+'audit-1'; path='/audit-api/local/v1/administration/audit-log'; headers=$adminHeaders },
    @{ owner='Consent'; container=$prefix+'consent-1'; path=('/consent-api/local/v1/consents/customers/'+$private.CustomerId); headers=$customerHeaders },
    @{ owner='Integration Gateway'; container=$prefix+'integration-gateway-1'; path='/connections-api/local/v1/google-drive'; headers=$customerHeaders }
)

function Wait-Ready {
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        $response = Invoke-WebRequest "$base/health/ready" -SkipHttpErrorCheck -TimeoutSec 5
        if ($response.StatusCode -eq 200) { return }
        Start-Sleep -Milliseconds 500
    }
    throw 'Cohort N did not recover aggregate readiness.'
}

$results = [System.Collections.Generic.List[object]]::new()
foreach ($case in $cases) {
    docker stop $case.container | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not pause $($case.owner)." }
    try {
        Start-Sleep -Milliseconds 250
        $response = Invoke-WebRequest ($base+$case.path) -Headers $case.headers -SkipHttpErrorCheck -TimeoutSec 15
        $body = [string]$response.Content
        $contentType = [string]$response.Headers.'Content-Type'
        $unsafe = $body -match '(?i)(stack trace|System\.|Npgsql|Bearer\s|X-Monergy-Session|Password=|request headers|developer exception)'
        $falseSuccess = $response.StatusCode -ge 200 -and $response.StatusCode -lt 300
        $sanitized = -not $unsafe -and -not $falseSuccess -and $body.Length -le 4096 -and $contentType -match 'json'
        $results.Add([pscustomobject]@{
            Owner=$case.owner
            Status=[int]$response.StatusCode
            Json=($contentType -match 'json')
            Sanitized=$sanitized
            FalseSuccess=$falseSuccess
        })
    }
    finally {
        docker start $case.container | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not restart $($case.owner)." }
        Wait-Ready
        $session = Invoke-WebRequest "$base/identity-api/local/v1/me/customer-profile" -Headers $customerHeaders -SkipHttpErrorCheck -TimeoutSec 15
        if ($session.StatusCode -ne 200) { throw "Customer session was not retained after $($case.owner) recovery." }
    }
}

if (@($results | Where-Object { -not $_.Sanitized -or $_.FalseSuccess }).Count -ne 0) {
    throw 'One or more owner outage responses were not sanitized fail-closed responses.'
}
$evidence = [ordered]@{
    recordedAt=[DateTimeOffset]::UtcNow.ToString('O')
    cases=$results
    customerSessionRetained=$true
    aggregateReadinessRecovered=$true
}
$output = Join-Path $RepositoryRoot '.artifacts/cohort-n/outage-evidence.json'
[IO.File]::WriteAllText($output, ($evidence | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
$results | Format-Table -AutoSize
