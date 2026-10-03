[CmdletBinding()]
param([string]$RepositoryRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Net.Http
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot) }
$controller = Join-Path $RepositoryRoot 'build/local/Invoke-MonergyLocal.ps1'
$stateRoot = Join-Path $RepositoryRoot '.artifacts/d11/persisted-reporting'
$platform = if ($env:OS -eq 'Windows_NT') { 'windows' } else { 'linux' }
$evidenceRoot = Join-Path $RepositoryRoot ".artifacts/d11/remediation-r2/$platform"
New-Item -ItemType Directory -Force -Path $evidenceRoot | Out-Null
Import-Module (Join-Path $RepositoryRoot 'build/local/Monergy.LocalProcess.psm1') -Force

function Invoke-Controller([string]$Action) {
    $output = & $controller -Action $Action -Profile persisted-reporting -RepositoryRoot $RepositoryRoot -Json
    return (($output | Select-Object -Last 1) | ConvertFrom-Json)
}

function Security([string]$CustomerId,[string]$WorkloadId='customer-web',[string]$WorkloadIdentity='d11-customer-web') {
    return [ordered]@{
        actor=[ordered]@{actorId="d11-synthetic-actor-$CustomerId";actorType='SYNTHETIC_HUMAN';authenticatedAt='1970-01-01T00:00:00Z';authenticationContextId="d11-authentication-$CustomerId"}
        workload=[ordered]@{workloadId=$WorkloadId;workloadIdentityId=$WorkloadIdentity}
        access=[ordered]@{purpose='D11_PERSISTED_REPORTING';consentReferenceId="d11-consent-$CustomerId";authorizationContextId="d11-$WorkloadId-$CustomerId-authorization";customerId=$CustomerId}
    }
}

function Contract([string]$Name,[object]$Payload,[string]$CustomerId,[string]$Key,
    [string]$WorkloadId='customer-web',[string]$WorkloadIdentity='d11-customer-web') {
    $id=[Guid]::NewGuid().ToString('N')
    return [ordered]@{contractName=$Name;contractVersion='1.0.0';requestId="d11-r1-$id";correlationId="d11-r1-$id";causationId=$null;security=Security $CustomerId $WorkloadId $WorkloadIdentity;idempotencyKey=$Key;payload=$Payload}
}

function Post([string]$Uri,[object]$Body) {
    $client=[Net.Http.HttpClient]::new()
    $client.Timeout=[TimeSpan]::FromSeconds(15)
    $content=[Net.Http.StringContent]::new(($Body|ConvertTo-Json -Depth 15 -Compress),[Text.Encoding]::UTF8,'application/json')
    try {
        $response=$client.PostAsync($Uri,$content).GetAwaiter().GetResult()
        $responseBody=$response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if(-not $response.IsSuccessStatusCode) {
            throw "HTTP $([int]$response.StatusCode): $responseBody"
        }
        $result=$responseBody|ConvertFrom-Json
    }
    catch {
        throw "Contract request to '$Uri' failed: $($_.Exception.Message)"
    }
    finally {$content.Dispose();$client.Dispose()}
    if($result.outcome -ne 'Success'){throw "Contract failed with $($result.error.code)."}
    return $result.data
}

function Sha([string]$Value) {
    $algorithm=[Security.Cryptography.SHA256]::Create()
    try{return ([BitConverter]::ToString($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value)))).Replace('-','')}
    finally{$algorithm.Dispose()}
}

function Wait-Dispatch([string]$Expected,[int]$Seconds=35) {
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    do {
        $status=Invoke-RestMethod -Uri 'http://127.0.0.1:5189/operations/local/reporting/status' -TimeoutSec 3
        if($status.state -eq $Expected){return $status}
        Start-Sleep -Milliseconds 250
    } while([DateTimeOffset]::UtcNow -lt $deadline)
    throw "Audit dispatch did not reach '$Expected'; last state was '$($status.state)'."
}

function Wait-DispatchFailure([int]$PriorFailures,[int]$Seconds=25) {
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    do {
        $status=Invoke-RestMethod -Uri 'http://127.0.0.1:5189/operations/local/reporting/status' -TimeoutSec 3
        if([int]$status.dispatchFailures -gt $PriorFailures){return $status}
        Start-Sleep -Milliseconds 100
    } while([DateTimeOffset]::UtcNow -lt $deadline)
    throw "No new Audit dispatch failure was observed; last failure count was $($status.dispatchFailures)."
}

function Get-AuditEvent([string]$EventId) {
    $secrets=Get-Content -LiteralPath (Join-Path $stateRoot 'secrets.json') -Raw | ConvertFrom-Json
    return Invoke-RestMethod -Uri "http://127.0.0.1:5193/operations/local/audit/events/$EventId" `
        -Headers @{'X-Monergy-Local-Transport'=$secrets.localTransportToken} -TimeoutSec 5
}

$startedAt=[DateTimeOffset]::UtcNow
$initial=Invoke-Controller Verify
$reportId=$initial.generatedReportId
$before=Post 'http://127.0.0.1:5189/contracts/cid-052/v1' (Contract 'GetReport' ([ordered]@{customerId='d11-customer-a';reportId=$reportId}) 'd11-customer-a' $null)
$beforeHash=Sha ([string]$before.export.content)

# Exercise a real owner-process update. Financial Profile owns the fact revision,
# Financial Rules owns the replacement calculation, and Reporting only consumes
# those owner contracts after the scenario selection is advanced explicitly.
$scenarioPath=Join-Path $stateRoot 'scenario-state.json'
$scenario=Get-Content -LiteralPath $scenarioPath -Raw | ConvertFrom-Json
$customerState=$scenario.customers | Where-Object customerId -eq 'd11-customer-a'
$fixture=Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/local/fixtures/persisted-reporting.synthetic.json') -Raw | ConvertFrom-Json
$fixtureCustomer=$fixture.customers | Where-Object customerId -eq 'd11-customer-a'
$updatedFacts=@()
for($index=0;$index -lt @($fixtureCustomer.facts).Count;$index++) {
    $fact=$fixtureCustomer.facts[$index]
    $updatedFacts += [ordered]@{
        sourceFactId=$fact.sourceFactId;customerId='d11-customer-a';factType=$fact.factType;label=$fact.label
        candidateValue=([decimal]$fact.value + $(if($index -eq 0){1}else{0}));currency=$fact.currency
        effectiveDate='2026-09-30';sourceLocation="fixture/$($fact.sourceFactId)";confidence=1.0
        evidenceId=$customerState.evidenceId;documentVersionId=$customerState.documentVersionId
        extractionVersion='d11.synthetic/1.0.0';validationVersion='d11.synthetic/1.0.0'
    }
}
$updateKey="d11-r1-owner-update-$([Guid]::NewGuid().ToString('N'))"
$normalized=Post 'http://127.0.0.1:5191/contracts/cid-031/v1' (Contract 'NormalizeSourceFacts' ([ordered]@{
    financialProfileId=$customerState.financialProfileId;customerId='d11-customer-a'
    processingId=$updateKey;facts=$updatedFacts;normalizationVersion='d11.synthetic/1.0.0'
    requestedAt=[DateTimeOffset]::UtcNow.ToString('O',[Globalization.CultureInfo]::InvariantCulture)
}) 'd11-customer-a' $updateKey 'local-fixture-seeder' 'd11-local-fixture-seeder')
$inputs=@()
foreach($fixtureFact in $fixtureCustomer.facts) {
    $normalizedFact=@($normalized.facts | Where-Object label -eq $fixtureFact.label)[0]
    $inputs += [ordered]@{role=$fixtureFact.role;financialFactId=$normalizedFact.financialFactId;expectedRevision=$normalizedFact.revision}
}
$calculation=Post 'http://127.0.0.1:5192/contracts/cid-037/v1' (Contract 'ExecuteCalculation' ([ordered]@{
    customerId='d11-customer-a';ruleId='engineering.sum';ruleVersion='1.0.0';inputs=$inputs
}) 'd11-customer-a' "$updateKey-calculation" 'local-fixture-seeder' 'd11-local-fixture-seeder')
$customerState.financialFactIds=@($normalized.facts.financialFactId)
$customerState.calculationResultId=$calculation.calculationResultId
[IO.File]::WriteAllText($scenarioPath,($scenario|ConvertTo-Json -Depth 15),[Text.UTF8Encoding]::new($false))
$updatedReport=Post 'http://127.0.0.1:5189/contracts/cid-051/v1' (Contract 'GenerateReport' ([ordered]@{customerId='d11-customer-a'}) 'd11-customer-a' "$updateKey-report")
$updatedCalculation=@($updatedReport.items | Where-Object {$_.source.sourceType -eq 'FinancialCalculation'})
$updatedHash=Sha ([string]$updatedReport.export.content)
if($updatedCalculation.Count -ne 1 -or $updatedCalculation[0].source.sourceId -ne $calculation.calculationResultId -or
   $updatedCalculation[0].source.sourceId -eq $initial.calculationResultId -or $updatedHash -eq $beforeHash) {
    throw 'The real owner update was not reflected by a newly initiated persisted report.'
}
[void](Wait-Dispatch 'DELIVERED' 25)
$updatedAudit=Get-AuditEvent "report-generated-$($updatedReport.reportId)"

[void](Invoke-Controller Stop)
[void](Invoke-Controller Start)
$after=Post 'http://127.0.0.1:5189/contracts/cid-052/v1' (Contract 'GetReport' ([ordered]@{customerId='d11-customer-a';reportId=$reportId}) 'd11-customer-a' $null)
$afterHash=Sha ([string]$after.export.content)
if($before.reportId -ne $after.reportId -or $before.generatedAt -ne $after.generatedAt -or
   $before.export.sha256 -ne $after.export.sha256 -or $beforeHash -ne $afterHash -or
   ($before.sourceFinancialReferences -join '|') -ne ($after.sourceFinancialReferences -join '|')) {
    throw 'Restart changed persisted report/export/source identity.'
}

$journal=Get-Content -LiteralPath (Join-Path $stateRoot 'processes.json') -Raw | ConvertFrom-Json
$auditProcess=$journal.processes | Where-Object name -eq 'audit'
$process=Get-Process -Id $auditProcess.pid -ErrorAction Stop
if($process.StartTime.ToUniversalTime().Ticks -ne [long]$auditProcess.startTimeTicks -or
   [IO.Path]::GetFullPath($process.Path) -ne [IO.Path]::GetFullPath($auditProcess.executable)) {
    throw 'Audit process ownership could not be verified for the outage test.'
}
Stop-Process -Id $auditProcess.pid
$outageKey="d11-r1-outage-$([Guid]::NewGuid().ToString('N'))"
$outageReport=Post 'http://127.0.0.1:5189/contracts/cid-051/v1' (Contract 'GenerateReport' ([ordered]@{customerId='d11-customer-a'}) 'd11-customer-a' $outageKey)
$degraded=Wait-Dispatch 'DEGRADED' 15
if($degraded.pendingEvents -lt 1){throw 'Audit outage did not retain pending Reporting intent.'}
[void](Invoke-Controller Stop)
[void](Invoke-Controller Start)
[void](Wait-Dispatch 'DELIVERED' 25)
$outageAudit=Get-AuditEvent "report-generated-$($outageReport.reportId)"

[IO.File]::WriteAllText((Join-Path $stateRoot 'inject-audit-lost-receipt.once'),'once',[Text.UTF8Encoding]::new($false))
$beforeLost=Invoke-RestMethod -Uri 'http://127.0.0.1:5189/operations/local/reporting/status' -TimeoutSec 3
$lostKey="d11-r1-lost-$([Guid]::NewGuid().ToString('N'))"
$lostReport=Post 'http://127.0.0.1:5189/contracts/cid-051/v1' (Contract 'GenerateReport' ([ordered]@{customerId='d11-customer-a'}) 'd11-customer-a' $lostKey)
[void](Wait-Dispatch 'DELIVERED' 25)
$afterLost=Invoke-RestMethod -Uri 'http://127.0.0.1:5189/operations/local/reporting/status' -TimeoutSec 3
if([int]$afterLost.dispatchFailures -le [int]$beforeLost.dispatchFailures -or
   [int]$afterLost.duplicateDeliveries -le [int]$beforeLost.duplicateDeliveries){
    throw 'Lost-receipt recovery did not record both a failed attempt and duplicate-safe redelivery.'
}
$lostAudit=Get-AuditEvent "report-generated-$($lostReport.reportId)"

[IO.File]::WriteAllText((Join-Path $stateRoot 'inject-audit-stall.once'),'once',[Text.UTF8Encoding]::new($false))
$beforeStall=Invoke-RestMethod -Uri 'http://127.0.0.1:5189/operations/local/reporting/status' -TimeoutSec 3
$stallKey="d11-r1-stall-$([Guid]::NewGuid().ToString('N'))"
$stallReport=Post 'http://127.0.0.1:5189/contracts/cid-051/v1' (Contract 'GenerateReport' ([ordered]@{customerId='d11-customer-a'}) 'd11-customer-a' $stallKey)
$stallFailure=Wait-DispatchFailure ([int]$beforeStall.dispatchFailures) 25
[void](Wait-Dispatch 'DELIVERED' 35)
$stallAudit=Get-AuditEvent "report-generated-$($stallReport.reportId)"

$prepared=Get-Content -LiteralPath (Join-Path $stateRoot 'prepared.json') -Raw | ConvertFrom-Json
$currentSource=Get-CanonicalDirtySourceIdentity -RepositoryRoot $RepositoryRoot
if($prepared.sourceIdentity.algorithm -ne 'canonical-file-set-v1' -or
   $prepared.sourceIdentity.dirtyDigest -cne $currentSource.dirtyDigest -or
   [int]$prepared.sourceIdentity.dirtyFileCount -ne [int]$currentSource.dirtyFileCount -or
   $prepared.sourceIdentity.head -cne $currentSource.head){
    throw 'Final D11 runtime evidence is not bound to the current canonical source identity. Run Prepare after the final source edit.'
}
$evidence=[ordered]@{
    schemaVersion='1.0.0';remediation='R2';classification='SYNTHETIC_LOCAL_CI_EPHEMERAL';platform=$platform
    startedAt=$startedAt;completedAt=[DateTimeOffset]::UtcNow;sourceIdentity=$prepared.sourceIdentity;buildIdentity=$prepared.buildIdentity
    ownerUpdate=[ordered]@{reportId=$updatedReport.reportId;previousCalculationResultId=$initial.calculationResultId;calculationResultId=$calculation.calculationResultId;financialFactIds=@($normalized.facts.financialFactId);exportSha256=$updatedHash;auditEvidenceId=$updatedAudit.auditEvidenceId;ownerMediated=$true}
    restart=[ordered]@{reportId=$reportId;generatedAt=$before.generatedAt;exportSha256=$beforeHash;sourceFinancialReferences=$before.sourceFinancialReferences;unchanged=$true}
    auditOutage=[ordered]@{reportId=$outageReport.reportId;pendingObserved=$degraded.pendingEvents;auditEvidenceId=$outageAudit.auditEvidenceId;recovered=$true}
    lostReceipt=[ordered]@{reportId=$lostReport.reportId;auditEvidenceId=$lostAudit.auditEvidenceId;sourceEventId=$lostAudit.sourceEventId;dispatchFailureDelta=([int]$afterLost.dispatchFailures-[int]$beforeLost.dispatchFailures);duplicateDelta=([int]$afterLost.duplicateDeliveries-[int]$beforeLost.duplicateDeliveries);duplicateSafe=$true}
    timeout=[ordered]@{reportId=$stallReport.reportId;dispatchFailuresBefore=$beforeStall.dispatchFailures;dispatchFailuresObserved=$stallFailure.dispatchFailures;auditEvidenceId=$stallAudit.auditEvidenceId;recovered=$true}
}
$path=Join-Path $evidenceRoot 'runtime-integration.json'
[IO.File]::WriteAllText($path,($evidence|ConvertTo-Json -Depth 15),[Text.UTF8Encoding]::new($false))
Write-Output "D11 runtime integration evidence PASS: $path"
