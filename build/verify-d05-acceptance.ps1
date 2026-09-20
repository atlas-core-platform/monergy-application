[CmdletBinding()]
param([string]$RepositoryRoot, [switch]$SelfTest)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $RepositoryRoot) { $RepositoryRoot = Split-Path -Parent $PSScriptRoot }

function Test-Sequence($Actual, $Expected) { ($Actual -join '|') -ceq ($Expected -join '|') }
$expectedPaths = @('PROJECT-STRUCTURE.md','README.md','services/financial-rules/README.md','repository.manifest.json','services/catalog.json','build/governance/d05-acceptance.json','build/verify-d05-acceptance.ps1','build/verify-financial-rules.ps1','build/verify-bootstrap.ps1','build/verify-toolchain.ps1','build/verify-vs02.ps1','build/release/New-CandidateManifest.ps1','build/release/verify-candidate-manifest.ps1')
$remediationPaths = @('.github/workflows/bootstrap.yml','PROJECT-STRUCTURE.md','README.md','apps/customer-web/tests/FormLifecycle.diagnostic.tsx','apps/customer-web/tests/Vs02Experience.test.tsx','apps/customer-web/tests/componentLifecycle.ts','apps/customer-web/tests/lifecycle.config.ts','build/frontend-lifecycle-reporter.mjs','build/governance/d05-acceptance.json','build/governance/d05-frontend-lifecycle.md','build/probe-frontend-lifecycle.mjs','build/verify-d05-acceptance.ps1')
$scope = Get-Content (Join-Path $RepositoryRoot 'build/governance/d05-scope-lock.json') -Raw -Encoding utf8 | ConvertFrom-Json
$d04 = Get-Content (Join-Path $RepositoryRoot 'build/governance/d04-acceptance.json') -Raw -Encoding utf8 | ConvertFrom-Json
$prior = @(@($d04.contracts.acceptedD03) + @($d04.contracts.d04Applicable) | Sort-Object -Unique)
$newIds = @('CID-037','CID-038','CID-039','CID-040','CID-041')
$union = @($prior + $newIds | Sort-Object -Unique)
$featureIds = @('M2-WS05-E01-F01','M2-WS05-E01-F02','M2-WS05-E01-F03','M2-WS05-E02-F01','M2-WS05-E02-F02','M2-WS05-E02-F03')
$record = Get-Content (Join-Path $RepositoryRoot 'build/governance/d05-acceptance.json') -Raw -Encoding utf8 | ConvertFrom-Json
$testPath = 'tests/financial-rules/Monergy.FinancialRules.Tests/CalculationContractTests.cs'
$governancePaths = @('build/governance/d05-acceptance.json','build/verify-d05-acceptance.ps1')
$approvedTest = '02bc0123c8725a8e0a5b7fb2f10a5f657e798b3d'
$approvedFrontend = 'd6af418450aa2cd08b3a8901e7af387c1939ed58'
$approvedCandidate = '827a7e26a515196047666d05c04ce46b218d2274'
$approvedTestBlob = '74bed03392fd99c3c74e4e9068ef0e94e0498962'
$approvedTestSha256 = '83E4FA035366F07A411379CA2A5E4031581BBEAF881734E22DF63C590E48816F'
$reviewedTestBlob = 'b80e46a11d510a5228ebaa2c40e95c528c7ee44f'
$reviewedTestSha256 = '9A63053B1A1B2D802A601CA89D32C53E191B14826F853A1844A44F593FC3CD79'
$semantics = @('SCENARIO_LOCAL_ALLOWED_SEMANTIC_CATEGORIES','REQUIRED_CATEGORIES_WITHOUT_TOTAL_COUNT','FINANCIAL_PROFILE_READS_PERMITTED','EXACT_LIFECYCLE_PROPERTIES_NO_FINANCIAL_PAYLOAD_FIELDS','OPAQUE_IDS_NOT_NUMERIC_SUBSTRING_TARGETS','NEGATIVE_FINANCIAL_PAYLOAD_FIXTURES_REJECTED')

function Get-GitLines([string[]]$Arguments) {
    $lines = @(& git -C $RepositoryRoot @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "Cannot verify Git evidence: $($Arguments -join ' ')" }
    return $lines
}
function Get-TreeFingerprint([string[]]$Entries) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($Entries -join "`n") + "`n")))).Replace('-','') }
    finally { $sha.Dispose() }
}
function Get-BlobSha256([string]$Blob) {
    if ($Blob -cnotmatch '^[0-9a-f]{40}$') { throw 'Invalid Git blob identity.' }
    # Hash the actual committed bytes, not PowerShell's line-oriented native output.
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = (Get-Command git -ErrorAction Stop).Source
    $start.WorkingDirectory = $RepositoryRoot
    $start.Arguments = "cat-file blob $Blob"
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $digest = ([BitConverter]::ToString($sha.ComputeHash($process.StandardOutput.BaseStream))).Replace('-','')
        $errorText = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw "Cannot read committed blob: $errorText" }
        return $digest
    } finally { $sha.Dispose(); $process.Dispose() }
}
function Get-GitBoundaryState {
    $head = (Get-GitLines @('rev-parse','HEAD')).Trim()
    # Existing Actions checkouts are shallow. Retrieve only this head's bounded
    # history when needed; never silently skip an ancestry check or change refs.
    if ((Get-GitLines @('rev-parse','--is-shallow-repository')) -ceq 'true' -and
        $approvedCandidate -cnotin @(Get-GitLines @('log','-8','--format=%H','HEAD'))) {
        Get-GitLines @('fetch','--quiet','--no-tags','--depth=8','origin',$head) | Out-Null
    }
    $ancestors = @{}
    foreach ($commit in @($approvedCandidate,'e669bf248ac2a8f362c538cbddf2e2d5a65174ef',$approvedFrontend,$approvedTest)) {
        Get-GitLines @('cat-file','-e',"$commit`^{commit}") | Out-Null
        & git -C $RepositoryRoot merge-base --is-ancestor $commit HEAD
        if ($LASTEXITCODE -gt 1) { throw 'Cannot establish approved ancestry.' }
        $ancestors[$commit] = $LASTEXITCODE -eq 0
    }
    $currentBlob = (Get-GitLines @('rev-parse',"HEAD:$testPath")).Trim()
    $testBlob = (Get-GitLines @('rev-parse',"${approvedTest}:$testPath")).Trim()
    $oldBlob = (Get-GitLines @('rev-parse',"${approvedCandidate}:$testPath")).Trim()
    return [pscustomobject]@{
        head=$head; ancestors=$ancestors
        testTreeId=(Get-GitLines @('rev-parse',"$approvedTest`^{tree}")).Trim()
        testParent=(Get-GitLines @('rev-parse',"$approvedTest`^")).Trim()
        frontendTreeId=(Get-GitLines @('rev-parse',"$approvedFrontend`^{tree}")).Trim()
        frontendParent=(Get-GitLines @('rev-parse',"$approvedFrontend`^")).Trim()
        acceptanceParent=(Get-GitLines @('rev-parse','e669bf248ac2a8f362c538cbddf2e2d5a65174ef^')).Trim()
        frontendTree=@(Get-GitLines @('ls-tree','-r',$approvedFrontend))
        testTree=@(Get-GitLines @('ls-tree','-r',$approvedTest))
        currentTree=@(Get-GitLines @('ls-tree','-r','HEAD'))
        testChangePaths=@(Get-GitLines @('diff','--name-only',$approvedFrontend,$approvedTest,'--'))
        currentBlob=$currentBlob; currentSha256=(Get-BlobSha256 $currentBlob)
        authorizedBlob=$testBlob; authorizedSha256=(Get-BlobSha256 $testBlob)
        reviewedBlob=$oldBlob; reviewedSha256=(Get-BlobSha256 $oldBlob)
        uncommittedProtectedPaths=@(Get-GitLines @('diff','HEAD','--name-only','--') | Where-Object { $_ -cnotin $governancePaths })
        untrackedPaths=@(Get-GitLines @('ls-files','--others','--exclude-standard'))
    }
}
function Get-BoundaryChecks($Value, $State) {
    $checks = [ordered]@{}
    $remediation = $Value.testRemediation
    $before = @($State.frontendTree | Where-Object { ($_ -split "`t",2)[1] -cne $testPath })
    $after = @($State.testTree | Where-Object { ($_ -split "`t",2)[1] -cne $testPath })
    $expected = @($State.testTree | Where-Object { ($_ -split "`t",2)[1] -cnotin $governancePaths })
    $current = @($State.currentTree | Where-Object { ($_ -split "`t",2)[1] -cnotin $governancePaths })
    $checks['Approved candidate, frontend and test-remediation chain exists in HEAD ancestry'] = $State.ancestors.$approvedCandidate -and $State.ancestors.'e669bf248ac2a8f362c538cbddf2e2d5a65174ef' -and $State.ancestors.$approvedFrontend -and $State.ancestors.$approvedTest -and $State.testParent -ceq $approvedFrontend -and $State.frontendParent -ceq 'e669bf248ac2a8f362c538cbddf2e2d5a65174ef' -and $State.acceptanceParent -ceq $approvedCandidate -and $State.testTreeId -ceq $remediation.tree -and $State.frontendTreeId -ceq $remediation.acceptedFrontend.tree
    $checks['Exact authorized committed test blob and raw SHA-256; no arbitrary exception'] = $State.reviewedBlob -ceq $reviewedTestBlob -and $State.reviewedSha256 -ceq $reviewedTestSha256 -and $State.authorizedBlob -ceq $approvedTestBlob -and $State.authorizedSha256 -ceq $approvedTestSha256 -and $State.currentBlob -ceq $approvedTestBlob -and $State.currentSha256 -ceq $approvedTestSha256
    $checks['Test remediation changed only the authorized path; historical protected set derived from Git'] = (Test-Sequence $State.testChangePaths @($testPath)) -and (Test-Sequence $before $after) -and $after.Count -eq $remediation.historicalOtherTrackedFiles.observedEntries -and (Get-TreeFingerprint $after) -ceq $remediation.historicalOtherTrackedFiles.sha256
    $checks['All current production, dependencies, tests, frontend and CI match approved test tree outside two governance paths'] = (Test-Sequence $expected $current) -and $State.uncommittedProtectedPaths.Count -eq 0 -and $State.untrackedPaths.Count -eq 0
    # Preserve the old fingerprint as well: normalize only the exact authorized
    # test entry back to its reviewed identity. The separate exact-blob assertion
    # above is mandatory, so this cannot authorize any future arbitrary test edit.
    $legacy = @($State.currentTree | Where-Object { ($_ -split "`t",2)[1] -cnotin @($expectedPaths + $remediationPaths) } | ForEach-Object {
        if (($_ -split "`t",2)[1] -ceq $testPath) { "100644 blob $reviewedTestBlob`t$testPath" } else { $_ }
    })
    $checks['Legacy protected fingerprint preserved with exact authorized test transition'] = (Get-TreeFingerprint $legacy) -ceq $Value.lifecycleRemediation.protectedGitTreeSha256 -and $legacy.Count -eq $Value.lifecycleRemediation.protectedGitTreeEntries
    return $checks
}

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
    $remediation = $Value.lifecycleRemediation
    $checks['Initial acceptance remains failed; all historical failures retained'] = $Value.closureReadiness -ceq 'CLOSURE_PENDING_REMEDIATION' -and $remediation.initialAcceptanceCommit -ceq 'e669bf248ac2a8f362c538cbddf2e2d5a65174ef' -and $remediation.initialAcceptanceTree -ceq '9d786d44684419495eaf481534cae646a3189d22' -and $remediation.initialAcceptanceHostedResult -ceq 'FAIL' -and (Test-Sequence $remediation.retainedFailedRuns @(35471222663,35474527894,35474529730)) -and (Test-Sequence $remediation.failedAcceptanceArtifacts.id @(10593928406,10594098190)) -and (Test-Sequence $remediation.failedAcceptanceArtifacts.sha256 @('e4cf27191849bb2a05a7ac6a2910744a4df39a4f397787a7b8404b2f41290cd8','31097309c15f0e41a6598ba872c24ed93896311bdc1b555a81e9a10ca3b39ddd')) -and @($remediation.failedAcceptanceArtifacts | Where-Object result -cne 'FAIL').Count -eq 0
    $checks['Twenty-run probe is bounded evidence, not a retry or preclaimed pass'] = $remediation.probe.platform -ceq 'linux' -and $remediation.probe.executions -eq 20 -and $remediation.probe.existingTestsPerExecution -eq 7 -and $remediation.probe.retry -eq 0 -and $remediation.probe.failurePolicy -ceq 'STOP_NO_RETRY_UNTIL_DIAGNOSED' -and $remediation.probe.purpose -ceq 'REMEDIATION_EVIDENCE_NOT_PRODUCT_NFR' -and $remediation.probe.result -ceq 'GENERATED_AT_EXACT_REMEDIATION_HEAD_NOT_PRECLAIMED'
    $checks['Remediation protects business/dependencies and requires new CTO approval'] = (Test-Sequence $remediation.changedPaths $remediationPaths) -and $remediation.protectedGitTreeEntries -eq 227 -and $remediation.protectedGitTreeSha256 -ceq '2E8961E749A8D42A2B6C841342B88F2505E9E129EB9C1F73989B64ABDAF20F4C' -and $remediation.scope -ceq 'TEST_LIFECYCLE_AND_VERIFICATION_EVIDENCE_ONLY' -and -not $remediation.productBehaviorOrDependencyChanges -and -not $remediation.architectureMutationAuthorized -and $remediation.newExactHeadApprovalRequired
    $test = $Value.testRemediation
    $checks['Accepted frontend and exact test-remediation identities recorded separately'] = $test.state -ceq 'ACCEPTED_FOR_CONTINUATION_NOT_MERGE' -and $test.commit -ceq $approvedTest -and $test.tree -ceq '48e4145e5d1f2ceb0d676d89904c266356db2ee2' -and $test.parent -ceq $approvedFrontend -and $test.acceptedFrontend.commit -ceq $approvedFrontend -and $test.acceptedFrontend.tree -ceq '7c33186a27193b97601e9b86c0e67c9bfe0b6919' -and $test.acceptedFrontend.status -ceq 'ACCEPTED'
    $checks['Exact reviewed-to-authorized test content recorded, never arbitrary future content'] = $test.sourceChange.path -ceq $testPath -and $test.sourceChange.reviewedBlob -ceq $reviewedTestBlob -and $test.sourceChange.reviewedSha256 -ceq $reviewedTestSha256 -and $test.sourceChange.authorizedBlob -ceq $approvedTestBlob -and $test.sourceChange.authorizedSha256 -ceq $approvedTestSha256 -and -not $test.arbitraryFutureTestChangesAllowed
    $checks['Only two governance paths permitted; historical comparison is retained'] = (Test-Sequence $test.governanceUpdatePaths $governancePaths) -and $test.historicalOtherTrackedFiles.delta -eq 0 -and $test.historicalOtherTrackedFiles.sha256 -ceq '380CD5D4CC040DC008CADAC4DDCD258A082099F13FBA62DE116E4D56ABBCC272' -and -not $test.architectureMutationAuthorized -and $test.newExactHeadApprovalRequired
    $evidence = $test.verificationAtTestRemediation
    $checks['Corrected semantic assertions and bounded test evidence recorded without upgrading prior verifier result'] = (Test-Sequence $test.correctedSemantics $semantics) -and $evidence.direct -ceq '2/2 PASS' -and $evidence.privacyRegressions -ceq '6/6 PASS' -and $evidence.stabilityExecutions -eq 20 -and $evidence.casesPerExecution -eq 2 -and $evidence.skipped -eq 0 -and $evidence.retries -eq 0 -and $evidence.stabilityResult -ceq 'PASS' -and $evidence.purpose -ceq 'REMEDIATION_EVIDENCE_NOT_PRODUCT_NFR' -and $evidence.stabilityScope -ceq 'UNCHANGED_PRECOMMIT_WORKTREE_IDENTICAL_TO_COMMITTED_TEST_BLOB' -and $evidence.directTrxSha256 -ceq 'CDBFC7E963F8AB3002E65DDA1E2EF7796146E2F16409FAB3AF914830F6DC16EF' -and $evidence.privacyTrxSha256 -ceq '1A0D078B2639243DEB9D4D293D9CC49E7BB452CEAFA235CA46735A6E4D13ADEE' -and $evidence.stabilitySummarySha256 -ceq '352A97E023C37A8CD6736C7A76E99B33CD135A9C6C6BE66B9A4F4C6DFE7021A0' -and $evidence.dotnet -ceq '145/145 PASS' -and $evidence.frontend -ceq '7/7 PASS' -and $evidence.browser -ceq '2/2 PASS' -and $evidence.acceptanceVerifier -ceq '16/17 FAIL_STALE_PROTECTED_TEST_FINGERPRINT'
    $direct = $test.defects.failedDirectEvidence
    $checks['Original substring defect and failed count correction remain distinct failed evidence'] = $test.defects.original.Contains('LifecycleSignal.ToString()') -and $test.defects.firstCorrection.Contains('expected 6') -and $test.defects.finalCorrection.Contains('no global record-count assumption') -and $direct.result -ceq 'FAIL' -and $direct.executed -eq 2 -and $direct.passed -eq 0 -and $direct.failed -eq 2 -and $direct.skipped -eq 0 -and $direct.expectedCount -eq 6 -and $direct.actualCount -eq 16 -and $direct.trxSha256 -ceq '949B7D9057BF731D21EB591F74C3686A103B3F88C69D93E30E14A30A310EA2E0'
    $checks['All four historical failed workflows stay FAIL; frontend acceptance does not rewrite failed PR'] = (Test-Sequence $test.historicalFailedRuns.id @(35471222663,35474527894,35474529730,35475754194)) -and @($test.historicalFailedRuns | Where-Object result -cne 'FAIL').Count -eq 0 -and $test.acceptedFrontend.pushRun -eq 35475751887 -and $test.acceptedFrontend.pushResult -ceq 'PASS' -and $test.acceptedFrontend.prRun -eq 35475754194 -and $test.acceptedFrontend.prResult -ceq 'FAIL' -and $test.acceptedFrontend.failedArtifact -eq 10594370997 -and $test.acceptedFrontend.failedArtifactSha256 -ceq '9B2F9AC8457E784AED9F01388F313E20931A704975A5A227682B197319EFD200'
    return $checks
}

$boundaryState = Get-GitBoundaryState
if ($SelfTest) {
    $cases = [ordered]@{}
    $cases['valid acceptance'] = @((Get-AcceptanceChecks $record).Values | Where-Object { -not $_ }).Count -eq 0
    foreach ($mutation in @('candidate','requirements','duplicate-feature','duplicate-contract','failed-run','artifact-digest','vulnerability-delta','fix-installed','gate','open-decision','publication','architecture-mutation','old-head-proof','protected-tree','acceptance-failure','probe-retry','remediation-scope','remediation-tree','authorized-test-hash','frontend-identity-missing','frontend-identity-altered','fabricated-test-lineage','failed-count-relabelled','failed-pr-relabelled','c10-resolved','vs03-complete','stronger-evidence','broader-governance-exception','privacy-semantics-removed','targeted-retries','prior-verifier-relabelled')) {
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
            'acceptance-failure' { $copy.lifecycleRemediation.initialAcceptanceHostedResult = 'PASS' }
            'probe-retry' { $copy.lifecycleRemediation.probe.retry = 1 }
            'remediation-scope' { $copy.lifecycleRemediation.productBehaviorOrDependencyChanges = $true }
            'remediation-tree' { $copy.lifecycleRemediation.protectedGitTreeSha256 = '0' * 64 }
            'authorized-test-hash' { $copy.testRemediation.sourceChange.authorizedSha256 = '0' * 64 }
            'frontend-identity-missing' { $copy.testRemediation.acceptedFrontend.commit = '' }
            'frontend-identity-altered' { $copy.testRemediation.acceptedFrontend.commit = $approvedCandidate }
            'fabricated-test-lineage' { $copy.testRemediation.parent = $approvedCandidate }
            'failed-count-relabelled' { $copy.testRemediation.defects.failedDirectEvidence.result = 'PASS' }
            'failed-pr-relabelled' { $copy.testRemediation.acceptedFrontend.prResult = 'PASS' }
            'c10-resolved' { $copy.openDecisions = @($copy.openDecisions | Where-Object { $_ -ne 'C-10' }) }
            'vs03-complete' { $copy.vs03 = 'COMPLETE' }
            'stronger-evidence' { $copy.evidenceLevel = 'PRODUCTION_COMPATIBILITY' }
            'broader-governance-exception' { $copy.testRemediation.governanceUpdatePaths += $testPath }
            'privacy-semantics-removed' { $copy.testRemediation.correctedSemantics = @($semantics | Select-Object -Skip 1) }
            'targeted-retries' { $copy.testRemediation.verificationAtTestRemediation.retries = 1 }
            'prior-verifier-relabelled' { $copy.testRemediation.verificationAtTestRemediation.acceptanceVerifier = 'PASS' }
        }
        $cases["reject $mutation"] = @((Get-AcceptanceChecks $copy).Values | Where-Object { -not $_ }).Count -gt 0
    }
    foreach ($index in 0..3) {
        $copy = $record | ConvertTo-Json -Depth 30 | ConvertFrom-Json
        $copy.testRemediation.historicalFailedRuns[$index].result = 'PASS'
        $cases["reject historical run $($copy.testRemediation.historicalFailedRuns[$index].id) relabelled"] = @((Get-AcceptanceChecks $copy).Values | Where-Object { -not $_ }).Count -gt 0
    }
    $cases['exact approved Git boundary positive fixture'] = @((Get-BoundaryChecks $record $boundaryState).Values | Where-Object { -not $_ }).Count -eq 0
    foreach ($mutation in @('test-blob','test-sha256','production-file','lockfile','frontend-lifecycle-file','ci-workflow','ancestry','test-parent','additional-test-change','dirty-production','untracked-file')) {
        $copy = $boundaryState | ConvertTo-Json -Depth 30 | ConvertFrom-Json
        switch ($mutation) {
            'test-blob' { $copy.currentBlob = '0' * 40 }
            'test-sha256' { $copy.currentSha256 = '0' * 64 }
            'ancestry' { $copy.ancestors.$approvedTest = $false }
            'test-parent' { $copy.testParent = $approvedCandidate }
            'additional-test-change' { $copy.testChangePaths += 'tests/unauthorized.cs' }
            'dirty-production' { $copy.uncommittedProtectedPaths = @('services/financial-rules/Application/FinancialRulesApplication.cs') }
            'untracked-file' { $copy.untrackedPaths = @('unauthorized.cs') }
            default {
                $path = switch ($mutation) {
                    'production-file' { 'services/financial-rules/Application/FinancialRulesApplication.cs' }
                    'lockfile' { 'pnpm-lock.yaml' }
                    'frontend-lifecycle-file' { 'apps/customer-web/tests/componentLifecycle.ts' }
                    'ci-workflow' { '.github/workflows/bootstrap.yml' }
                }
                if (@($copy.currentTree | Where-Object { ($_ -split "`t",2)[1] -ceq $path }).Count -ne 1) { throw 'Missing negative-fixture path.' }
                $copy.currentTree = @($copy.currentTree | ForEach-Object { if (($_ -split "`t",2)[1] -ceq $path) { "100644 blob $('0' * 40)`t$path" } else { $_ } })
            }
        }
        $cases["reject Git boundary $mutation"] = @((Get-BoundaryChecks $record $copy).Values | Where-Object { -not $_ }).Count -gt 0
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
$boundaryChecks = Get-BoundaryChecks $record $boundaryState
foreach ($entry in $boundaryChecks.GetEnumerator()) { $checks[$entry.Key] = $entry.Value }
$protected = @($boundaryState.currentTree | Where-Object { ($_ -split "`t",2)[1] -cnotin $governancePaths })
$fingerprint = Get-TreeFingerprint $protected
foreach ($check in $checks.GetEnumerator()) { Write-Output "[$(if($check.Value){'PASS'}else{'FAIL'})] $($check.Key)" }
$out = Join-Path $RepositoryRoot '.artifacts/d05'
New-Item -ItemType Directory -Path $out -Force | Out-Null
[ordered]@{deliverable='MWP-03-D05';applicationLifecycle='ACCEPTED_COMPLETE';closureReadiness=$record.closureReadiness;initialAcceptanceCommit=$record.lifecycleRemediation.initialAcceptanceCommit;initialAcceptanceHostedResult='FAIL';frontendRemediationCommit=$approvedFrontend;testRemediationCommit=$approvedTest;testBlob=$boundaryState.currentBlob;testSha256=$boundaryState.currentSha256;retainedFailedRuns=$record.testRemediation.historicalFailedRuns;mergeAuthorization='NOT_AUTHORIZED';sourceCommit=$boundaryState.head;sourceTree=(Get-GitLines @('rev-parse','HEAD^{tree}')).Trim();protectedEntries=$protected.Count;protectedTreeSha256=$fingerprint;boundaryDerivedFrom=$approvedTest;governanceExceptions=$governancePaths;checks=$checks;acceptanceRecordSha256=(Get-FileHash (Join-Path $RepositoryRoot 'build/governance/d05-acceptance.json') -Algorithm SHA256).Hash} | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $out 'acceptance-verification.json') -Encoding utf8
if (@($checks.Values | Where-Object { -not $_ }).Count) { throw 'D05 acceptance verification failed.' }
Write-Output "D05 acceptance verification passed: $($checks.Count)/$($checks.Count)."
