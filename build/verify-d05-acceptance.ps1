[CmdletBinding()]
param([string]$RepositoryRoot, [switch]$SelfTest)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $RepositoryRoot) { $RepositoryRoot = Split-Path -Parent $PSScriptRoot }

function Test-Sequence($Actual, $Expected) { ($Actual -join '|') -ceq ($Expected -join '|') }
$expectedPaths = @('PROJECT-STRUCTURE.md','README.md','services/financial-rules/README.md','repository.manifest.json','services/catalog.json','build/governance/d05-acceptance.json','build/verify-d05-acceptance.ps1','build/verify-financial-rules.ps1','build/verify-bootstrap.ps1','build/verify-toolchain.ps1','build/verify-vs02.ps1','build/release/New-CandidateManifest.ps1','build/release/verify-candidate-manifest.ps1')
$scope = Get-Content (Join-Path $RepositoryRoot 'build/governance/d05-scope-lock.json') -Raw -Encoding utf8 | ConvertFrom-Json
$d04 = Get-Content (Join-Path $RepositoryRoot 'build/governance/d04-acceptance.json') -Raw -Encoding utf8 | ConvertFrom-Json
$prior = @(@($d04.contracts.acceptedD03) + @($d04.contracts.d04Applicable) | Sort-Object -Unique)
$newIds = @('CID-037','CID-038','CID-039','CID-040','CID-041')
$union = @($prior + $newIds | Sort-Object -Unique)
$featureIds = @('M2-WS05-E01-F01','M2-WS05-E01-F02','M2-WS05-E01-F03','M2-WS05-E02-F01','M2-WS05-E02-F02','M2-WS05-E02-F03')
$record = Get-Content (Join-Path $RepositoryRoot 'build/governance/d05-acceptance.json') -Raw -Encoding utf8 | ConvertFrom-Json

function Get-AcceptanceChecks($Value) {
    $checks = [ordered]@{}
    $checks['Application accepted at SIMULATOR; merge not authorized'] = $Value.deliverable -ceq 'MWP-03-D05' -and $Value.applicationLifecycle -ceq 'ACCEPTED_COMPLETE' -and $Value.evidenceLevel -ceq 'SIMULATOR' -and $Value.crossRepositoryLifecycle -ceq 'PENDING_ARCHITECTURE_CLOSURE' -and $Value.mergeAuthorization -ceq 'NOT_AUTHORIZED'
    $checks['Reviewed candidate and distinct ancestry/requirements identities'] = $Value.approvedCandidates.application -ceq '827a7e26a515196047666d05c04ce46b218d2274' -and $Value.approvedCandidates.architecture -ceq 'e4ab9a1bf62891c73b400eab9faa0fe78bc200f5' -and $Value.approvedCandidates.sourceTree -ceq '11cb8f43840d94aae700cdf630fd906dfd62b3e8' -and $Value.startingCommits.application -ceq '9aae295846f0dd1ca01f9d7f99f0233cd53c7626' -and $Value.startingCommits.architecture -ceq '866c05122a0823d38dcaf243d3164b5b2b0947d9' -and $Value.startingCommits.architectureRequirements -ceq '5e7fb1cc9a56cc7b0411640bbb63c13c02c83657' -and $Value.approvedCandidates.applicationPullRequest -eq 3 -and $Value.approvedCandidates.architecturePullRequest -eq 9
    $checks['Exactly six accepted Feature identities'] = (Test-Sequence $Value.features.id $featureIds) -and (Test-Sequence $Value.features.name $scope.features.name) -and @($Value.features | Where-Object status -cne 'IMPLEMENTATION_ACCEPTED_SIMULATOR').Count -eq 0
    $checks['Five new contracts; consumed/regression sets and exact union'] = (Test-Sequence $Value.contracts.newlyRealized.id $newIds) -and (Test-Sequence $Value.contracts.newlyRealized.name $scope.contracts.name) -and @($Value.contracts.newlyRealized | Where-Object { $_.version -cne '1.0.0' -or $_.status -cne 'BEHAVIORAL_COMPATIBILITY_EVIDENCE_ACCEPTED_SIMULATOR' }).Count -eq 0 -and (Test-Sequence $Value.contracts.consumedExisting @('CID-032','CID-033')) -and (Test-Sequence $Value.contracts.regressionExisting $scope.regressionContracts) -and (Test-Sequence $Value.contracts.priorD03D04Union $prior) -and (Test-Sequence $Value.contracts.programWideUnion $union) -and $Value.contracts.programWideUnionCount -eq $union.Count -and $union.Count -eq 22
    $runs = @($Value.reviewedCandidateWorkflows)
    $expectedDigests = @('25fb16d485f7edd34ce43539b89ca0597e0ad6a44ab56c6e888a164acf8479ab','f93cf69e1eb967adad2e1296ca3666d1dab2028d1fcaf501d22828818adc7370','5ed02c4e0c94e0a2ec0157aa90adf5897715140e326da86de876147bfdaaf5c8')
    $checks['Reviewed workflow and immutable artifact identities; failed run stays FAIL'] = (Test-Sequence $runs.id @(35471427802,35471224262,35471222663)) -and (Test-Sequence $runs.conclusion @('success','success','failure')) -and (Test-Sequence $runs.artifact.id @(10592983949,10592534433,10592738646)) -and (Test-Sequence $runs.artifact.sha256 $expectedDigests) -and (Test-Sequence $runs.checkedOutCommit @('827a7e26a515196047666d05c04ce46b218d2274','a6960ccbb5f9b0912b6b0c39caecab8208cb4074','827a7e26a515196047666d05c04ce46b218d2274')) -and $runs[2].retainedResult -ceq 'FAIL' -and @($runs | Where-Object reportedHeadSha -cne '827a7e26a515196047666d05c04ce46b218d2274').Count -eq 0
    $verification = $Value.reviewedCandidateVerification
    $checks['Historical candidate test/verifier evidence and local/hosted separation'] = $verification.dotnet.d03 -eq 27 -and $verification.dotnet.d04 -eq 38 -and $verification.dotnet.d05 -eq 66 -and $verification.dotnet.architecture -eq 8 -and $verification.dotnet.total -eq 139 -and $verification.dotnet.failed -eq 0 -and $verification.dotnet.skipped -eq 0 -and $verification.frontend -eq 7 -and $verification.browser -eq 2 -and $verification.applicationVerifiers.d05 -ceq '24/24' -and $verification.negativeSelfTests.d05 -ceq '12/12' -and $verification.localDocker -ceq 'BLOCKED_DOCKER_LINUX_ENGINE_UNAVAILABLE' -and $verification.hostedLinux -ceq 'PASS_12_OF_12_BUILD_SBOM_SCAN'
    $vulnerability = $Value.vulnerabilities
    $expectedCves = @('CVE-2026-77117','CVE-2026-80489','CVE-2026-19499','CVE-2026-19542','CVE-2026-6791','CVE-2026-6368','CVE-2026-8674','CVE-2026-89092','CVE-2026-18374','CVE-2016-20013')
    $checks['Reviewed findings unchanged and available fixes not installed'] = $vulnerability.d04ToD05Delta -eq 0 -and $vulnerability.total -eq 120 -and $vulnerability.critical -eq 0 -and $vulnerability.high -eq 0 -and $vulnerability.medium -eq 108 -and $vulnerability.negligible -eq 12 -and $vulnerability.images -eq 12 -and $vulnerability.perImage -eq 10 -and $vulnerability.package -ceq 'libc6' -and $vulnerability.installedVersion -ceq '2.39-0ubuntu8.8' -and -not $vulnerability.fixInstalled -and (Test-Sequence $vulnerability.findings.id $expectedCves) -and @($vulnerability.findings | Where-Object { $_.severity -ceq 'Medium' -and $_.fixState -ceq 'fixed' -and $_.fixVersion -ceq '2.39-0ubuntu8.9' }).Count -eq 6 -and @($vulnerability.findings | Where-Object fixState -ceq 'not-fixed').Count -eq 3 -and $vulnerability.findings[9].fixState -ceq 'wont-fix'
    $checks['Stage gates, open decisions, conditional VS03 and fixture limits'] = $Value.stageGates.'SG-01' -ceq 'READY' -and $Value.stageGates.'SG-02' -ceq 'CONDITIONALLY_READY' -and $Value.stageGates.'SG-03' -ceq 'BLOCKED' -and $Value.stageGates.'SG-04' -ceq 'BLOCKED' -and (Test-Sequence $Value.openDecisions $scope.decisionDependencies) -and $Value.vs03 -ceq 'CONDITIONALLY_READY' -and $Value.fixtures -ceq 'ENGINEERING_VERIFICATION_ONLY_NOT_CLIENT_APPROVED_METHODOLOGY'
    $checks['Publication and stronger readiness not claimed'] = -not $Value.publication.packages -and -not $Value.publication.images -and -not $Value.publication.deployment -and -not $Value.publication.tag -and (Test-Sequence $Value.notClaimed @('APPROVED_CUSTOMER_FINANCIAL_METHODOLOGY','APPROVED_SCORES_FORMULAS_OR_ROUNDING_POLICY','FULL_VS03_COMPLETION','PROVIDER_SANDBOX','PHYSICAL_PERSISTENCE_DURABILITY','DURABLE_BROKER_OR_OUTBOX_DELIVERY','INTEGRATION_READY','UAT_READY','PRODUCTION_READY'))
    $exception = $Value.preflightException
    $checks['Architecture exception is non-evidence; no mutation or stash absorption'] = -not $exception.sourceIsAcceptedArchitectureEvidence -and -not $exception.architectureMutationAuthorized -and $exception.architectureHead -ceq 'e4ab9a1bf62891c73b400eab9faa0fe78bc200f5' -and $exception.remoteDeliveryHead -ceq $exception.architectureHead -and $exception.localFileSha256 -ceq '32CB4B0A6DDDDEC46FF486E923C2949AA21D87166680E94997D7010176588AD5' -and $exception.appendAddedLines -eq 390 -and $exception.deletedLines -eq 0 -and $exception.fileLines -eq 690 -and $exception.preservedStash -ceq '8667213f8630eb305b94a9546fb3adf361d6f9c4' -and $exception.expires -ceq 'IMMEDIATELY_AFTER_APPLICATION_ACCEPTANCE_STEP'
    $policy = $Value.acceptanceVerificationPolicy
    $checks['Fresh new-head verification and explicit STOP controls required'] = $policy.freshLocalAndHostedRequired -and $policy.reviewedRunsAreNotNewHeadProof -and $policy.frontendTeardownRecurrence -ceq 'STOP_NO_RERUN_UNTIL_GREEN' -and $policy.vulnerabilityDifference -ceq 'STOP_REPORT_EXACT_DELTA' -and $Value.approvalBasis.newAcceptanceHeadRequiresExactCommitMergeApproval
    $checks['Closure-only file set and protected-tree identity fixed'] = (Test-Sequence $Value.closureBoundary.allowedPaths $expectedPaths) -and $Value.closureBoundary.protectedGitTreeEntries -eq 229 -and $Value.closureBoundary.protectedGitTreeSha256 -ceq '144F32789E1428D2D514AF00487940EAF5F5F7E8612EB2CACA61F762EEA82FC7' -and -not $Value.closureBoundary.businessContractFixtureDependencySecurityFrontendChanges
    return $checks
}

if ($SelfTest) {
    $cases = [ordered]@{}
    $cases['valid acceptance'] = @((Get-AcceptanceChecks $record).Values | Where-Object { -not $_ }).Count -eq 0
    foreach ($mutation in @('candidate','requirements','duplicate-feature','duplicate-contract','failed-run','artifact-digest','vulnerability-delta','fix-installed','gate','open-decision','publication','architecture-mutation','old-head-proof','protected-tree')) {
        $copy = $record | ConvertTo-Json -Depth 30 | ConvertFrom-Json
        switch ($mutation) {
            'candidate' { $copy.approvedCandidates.application = 'unreviewed' }
            'requirements' { $copy.startingCommits.architectureRequirements = $copy.startingCommits.architecture }
            'duplicate-feature' { $copy.features[1].id = $copy.features[0].id }
            'duplicate-contract' { $copy.contracts.programWideUnion[1] = $copy.contracts.programWideUnion[0] }
            'failed-run' { $copy.reviewedCandidateWorkflows[2].conclusion = 'success' }
            'artifact-digest' { $copy.reviewedCandidateWorkflows[0].artifact.sha256 = '0' * 64 }
            'vulnerability-delta' { $copy.vulnerabilities.d04ToD05Delta = 1 }
            'fix-installed' { $copy.vulnerabilities.fixInstalled = $true }
            'gate' { $copy.stageGates.'SG-02' = 'READY' }
            'open-decision' { $copy.openDecisions = @($copy.openDecisions | Where-Object { $_ -ne 'OD-08' }) }
            'publication' { $copy.publication.images = $true }
            'architecture-mutation' { $copy.preflightException.architectureMutationAuthorized = $true }
            'old-head-proof' { $copy.acceptanceVerificationPolicy.reviewedRunsAreNotNewHeadProof = $false }
            'protected-tree' { $copy.closureBoundary.protectedGitTreeSha256 = '0' * 64 }
        }
        $cases["reject $mutation"] = @((Get-AcceptanceChecks $copy).Values | Where-Object { -not $_ }).Count -gt 0
    }
    foreach ($case in $cases.GetEnumerator()) { Write-Output "SELF-TEST $(if($case.Value){'PASS'}else{'FAIL'}): $($case.Key)" }
    if (@($cases.Values | Where-Object { -not $_ }).Count) { throw 'D05 acceptance self-tests failed.' }
    Write-Output "D05 acceptance self-tests passed: $($cases.Count)/$($cases.Count)."
    exit 0
}
$checks = Get-AcceptanceChecks $record
$manifest = Get-Content (Join-Path $RepositoryRoot 'repository.manifest.json') -Raw | ConvertFrom-Json
$service = (Get-Content (Join-Path $RepositoryRoot 'services/catalog.json') -Raw | ConvertFrom-Json).services | Where-Object id -ceq 'financial-rules'
$checks['Application lifecycle and Rules service catalog agree'] = $manifest.d05Status -ceq 'ACCEPTED_COMPLETE' -and $manifest.d05FeatureState -ceq 'IMPLEMENTATION_ACCEPTED_SIMULATOR_6_OF_6' -and $manifest.d05ContractState -ceq 'BEHAVIORAL_COMPATIBILITY_EVIDENCE_ACCEPTED_SIMULATOR_5_OF_5' -and $manifest.d05EvidenceLevel -ceq 'SIMULATOR_ACCEPTED' -and $service.status -ceq 'D05_IMPLEMENTATION_ACCEPTED_SIMULATOR' -and $service.featureImplementation -ceq 'IMPLEMENTATION_ACCEPTED_SIMULATOR' -and $service.d05Status -ceq 'IMPLEMENTATION_ACCEPTED_SIMULATOR' -and (Test-Sequence $service.d05FeatureIds $featureIds)
$protected = @(& git -C $RepositoryRoot ls-tree -r HEAD | Where-Object { ($_ -split "`t",2)[1] -cnotin $expectedPaths })
if ($LASTEXITCODE -ne 0) { throw 'Cannot read committed application tree.' }
$sha = [Security.Cryptography.SHA256]::Create()
try { $fingerprint = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($protected -join "`n") + "`n")))).Replace('-','') }
finally { $sha.Dispose() }
$checks['Reviewed business, contracts, fixtures, dependencies and tests unchanged'] = $protected.Count -eq $record.closureBoundary.protectedGitTreeEntries -and $fingerprint -ceq $record.closureBoundary.protectedGitTreeSha256
foreach ($check in $checks.GetEnumerator()) { Write-Output "[$(if($check.Value){'PASS'}else{'FAIL'})] $($check.Key)" }
$out = Join-Path $RepositoryRoot '.artifacts/d05'
New-Item -ItemType Directory -Path $out -Force | Out-Null
[ordered]@{deliverable='MWP-03-D05';applicationLifecycle='ACCEPTED_COMPLETE';mergeAuthorization='NOT_AUTHORIZED';sourceCommit=(& git -C $RepositoryRoot rev-parse HEAD).Trim();sourceTree=(& git -C $RepositoryRoot rev-parse 'HEAD^{tree}').Trim();protectedTreeSha256=$fingerprint;checks=$checks;acceptanceRecordSha256=(Get-FileHash (Join-Path $RepositoryRoot 'build/governance/d05-acceptance.json') -Algorithm SHA256).Hash} | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $out 'acceptance-verification.json') -Encoding utf8
if (@($checks.Values | Where-Object { -not $_ }).Count) { throw 'D05 acceptance verification failed.' }
Write-Output "D05 acceptance verification passed: $($checks.Count)/$($checks.Count)."
