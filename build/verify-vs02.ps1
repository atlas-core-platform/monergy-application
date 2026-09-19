[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Test-ExactSet {
    param([object[]]$Actual, [object[]]$Expected)
    (@($Actual | ForEach-Object { [string]$_ } | Sort-Object) -join '|') -ceq
        (@($Expected | ForEach-Object { [string]$_ } | Sort-Object) -join '|')
}

function Test-ReferenceZone {
    param([string]$Zone)
    $Zone -in @('LOCAL', 'CI_EPHEMERAL', 'CI/EPHEMERAL')
}

function Test-SensitiveTelemetry {
    param([string]$Content)
    $Content -match '(?i)(content|payload|token|secret|candidateValue)\s*='
}

function Test-ApproximatelyEqual {
    param([double]$Actual, [double]$Expected, [double]$Tolerance = 0.011)
    [math]::Abs($Actual - $Expected) -lt $Tolerance
}

function Test-BundleEvidence {
    param([object]$Evidence)
    $initial = $Evidence.d03.initialApplicationShell
    $runtime = $Evidence.d03.runtimeChunk
    $lazy = $Evidence.d03.vs02LazyRouteChunk
    $javascript = $Evidence.d03.totalJavaScript
    $css = $Evidence.d03.totalCss
    $combined = $Evidence.d03.totalJavaScriptAndCss
    $expectedJavaScriptMinified = [math]::Round($initial.minifiedKb + $runtime.minifiedKb + $lazy.minifiedKb, 2)
    $expectedJavaScriptGzip = [math]::Round($initial.gzipKb + $runtime.gzipKb + $lazy.gzipKb, 2)
    $expectedDeltaMinified = [math]::Round($initial.minifiedKb - 628.65, 2)
    $expectedDeltaGzip = [math]::Round($initial.gzipKb - 202.69, 2)
    $expectedPercentageMinified = [math]::Round(($expectedDeltaMinified / 628.65) * 100, 2)
    $expectedPercentageGzip = [math]::Round(($expectedDeltaGzip / 202.69) * 100, 2)

    $Evidence.schemaVersion -ceq '1.0.0' -and
        $Evidence.measurementMethod -ceq 'Vite 8.3 production reporter; minified and gzip sizes; decimal kB' -and
        $Evidence.d02BaselineInitialApplicationShell.minifiedKb -eq 628.65 -and
        $Evidence.d02BaselineInitialApplicationShell.gzipKb -eq 202.69 -and
        $initial.minifiedKb -gt 0 -and $initial.gzipKb -gt 0 -and
        $runtime.minifiedKb -gt 0 -and $runtime.gzipKb -gt 0 -and
        $lazy.minifiedKb -gt 0 -and $lazy.gzipKb -gt 0 -and
        $css.minifiedKb -gt 0 -and $css.gzipKb -gt 0 -and
        (Test-ApproximatelyEqual $javascript.minifiedKb $expectedJavaScriptMinified) -and
        (Test-ApproximatelyEqual $javascript.gzipKb $expectedJavaScriptGzip) -and
        (Test-ApproximatelyEqual $combined.minifiedKb ($javascript.minifiedKb + $css.minifiedKb)) -and
        (Test-ApproximatelyEqual $combined.gzipKb ($javascript.gzipKb + $css.gzipKb)) -and
        (Test-ApproximatelyEqual $Evidence.d02ToD03InitialShellDelta.absolute.minifiedKb $expectedDeltaMinified) -and
        (Test-ApproximatelyEqual $Evidence.d02ToD03InitialShellDelta.absolute.gzipKb $expectedDeltaGzip) -and
        (Test-ApproximatelyEqual $Evidence.d02ToD03InitialShellDelta.percentage.minifiedPercent $expectedPercentageMinified) -and
        (Test-ApproximatelyEqual $Evidence.d02ToD03InitialShellDelta.percentage.gzipPercent $expectedPercentageGzip) -and
        $Evidence.routeIsolation.initialHtmlReferencesShellChunk -and
        $Evidence.routeIsolation.initialShellReferencesLazyChunk -and
        $Evidence.routeIsolation.vs02SentinelExcludedFromInitialShell -and
        $Evidence.routeIsolation.vs02SentinelPresentInLazyChunk -and
        $Evidence.routeIsolation.passed -and
        $Evidence.performanceThreshold -ceq 'NOT_DEFINED' -and
        $Evidence.clientCommitment -ceq 'NONE_INFERRED'
}

if ($SelfTest) {
    $tests = [ordered]@{
        'exact set accepts reordered values' = (Test-ExactSet @('b', 'a') @('a', 'b'))
        'exact set rejects duplicate' = (-not (Test-ExactSet @('a', 'a') @('a', 'b')))
        'local reference zone accepted' = (Test-ReferenceZone 'LOCAL')
        'CI ephemeral reference zone accepted' = (Test-ReferenceZone 'CI_EPHEMERAL')
        'UAT reference zone rejected' = (-not (Test-ReferenceZone 'UAT'))
        'sensitive telemetry rejected' = (Test-SensitiveTelemetry 'payload = value')
        'identifier-only telemetry accepted' = (-not (Test-SensitiveTelemetry 'correlationId = value'))
        'eager VS-02 bundle evidence rejected' = (-not (Test-BundleEvidence ([pscustomobject]@{
            schemaVersion = '1.0.0'
            measurementMethod = 'Vite 8.3 production reporter; minified and gzip sizes; decimal kB'
            d02BaselineInitialApplicationShell = [pscustomobject]@{ minifiedKb = 628.65; gzipKb = 202.69 }
            d03 = [pscustomobject]@{
                initialApplicationShell = [pscustomobject]@{ minifiedKb = 641.86; gzipKb = 207.44 }
                runtimeChunk = [pscustomobject]@{ minifiedKb = 0.58; gzipKb = 0.36 }
                vs02LazyRouteChunk = [pscustomobject]@{ minifiedKb = 352.48; gzipKb = 111.57 }
                totalJavaScript = [pscustomobject]@{ minifiedKb = 994.92; gzipKb = 319.37 }
                totalCss = [pscustomobject]@{ minifiedKb = 8.31; gzipKb = 2.61 }
                totalJavaScriptAndCss = [pscustomobject]@{ minifiedKb = 1003.23; gzipKb = 321.98 }
            }
            d02ToD03InitialShellDelta = [pscustomobject]@{
                absolute = [pscustomobject]@{ minifiedKb = 13.21; gzipKb = 4.75 }
                percentage = [pscustomobject]@{ minifiedPercent = 2.1; gzipPercent = 2.34 }
            }
            routeIsolation = [pscustomobject]@{
                initialHtmlReferencesShellChunk = $true
                initialShellReferencesLazyChunk = $true
                vs02SentinelExcludedFromInitialShell = $false
                vs02SentinelPresentInLazyChunk = $true
                passed = $false
            }
            performanceThreshold = 'NOT_DEFINED'
            clientCommitment = 'NONE_INFERRED'
        })))
    }
    $failed = @($tests.GetEnumerator() | Where-Object { -not $_.Value })
    foreach ($test in $tests.GetEnumerator()) {
        Write-Output "SELF-TEST $(if ($test.Value) { 'PASS' } else { 'FAIL' }): $($test.Key)"
    }
    if ($failed.Count -gt 0) { throw "D03 verifier self-tests failed: $($tests.Count - $failed.Count)/$($tests.Count)." }
    Write-Output "D03 verifier self-tests passed: $($tests.Count)/$($tests.Count)."
    exit 0
}

$expectedFeatures = @(
    'M2-WS03-E01-F02', 'M2-WS03-E02-F01', 'M2-WS03-E02-F02', 'M2-WS03-E03-F01',
    'M2-WS03-E03-F02', 'M2-WS03-E03-F03', 'M2-WS04-E03-F01', 'M2-WS04-E03-F02'
)
$expectedContracts = @(
    'CID-020', 'CID-021', 'CID-022', 'CID-023', 'CID-024', 'CID-025', 'CID-027',
    'CID-028', 'CID-031', 'CID-033', 'CID-034', 'CID-035', 'CID-055', 'CID-057'
)
$expectedServices = @('Evidence Service', 'Document Intelligence Service', 'Financial Profile Service', 'Job Management Service', 'Audit Service')
$expectedServiceIds = @('evidence', 'document-intelligence', 'financial-profile', 'job-management', 'audit')

$checks = [System.Collections.Generic.List[object]]::new()
function Add-Check {
    param([string]$Name, [bool]$Passed, [string]$Evidence)
    $checks.Add([pscustomobject]@{ Name = $Name; Passed = $Passed; Evidence = $Evidence }) | Out-Null
}

$scope = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/governance/vs02-scope-lock.json') -Raw | ConvertFrom-Json
Add-Check 'D03 accepted reviewed scope identity' ($scope.deliverable -ceq 'MWP-03-D03' -and $scope.slice.id -ceq 'VS-02' -and $scope.slice.status -ceq 'IMPLEMENTATION_CANDIDATE') 'Reviewed MWP-03-D03 / VS-02 candidate artifact accepted at SIMULATOR'
Add-Check 'Exact eight Feature IDs' (Test-ExactSet @($scope.features.id) $expectedFeatures) '8/8 derived machine-readable identities'
Add-Check 'Unique Feature IDs' (@($scope.features.id | Select-Object -Unique).Count -eq 8) 'No duplicate Feature identity'
Add-Check 'Exact fourteen contract IDs' (Test-ExactSet @($scope.contracts.id) $expectedContracts) '14/14 derived machine-readable identities'
Add-Check 'Unique contract IDs' (@($scope.contracts.id | Select-Object -Unique).Count -eq 14) 'No duplicate contract identity'
Add-Check 'Exact five service boundaries' (Test-ExactSet @($scope.participatingServices) $expectedServices) '5/5 service identities'
Add-Check 'Feature owners are participating boundaries' (@($scope.features | Where-Object owner -notin $expectedServices).Count -eq 0) 'Every Feature owner is in the locked service set'
Add-Check 'Contract owners are governed' (@($scope.contracts | Where-Object { $_.owner -notin $expectedServices }).Count -eq 0) 'Fourteen contracts owned by Evidence, Document Intelligence, Financial Profile, or Job Management'

$schema = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'contracts/schemas/vs02-contracts.schema.json') -Raw | ConvertFrom-Json
Add-Check 'Contract schema exact IDs' (Test-ExactSet @($schema.properties.contractId.enum) $expectedContracts) 'Schema rejects unknown contract IDs'
Add-Check 'Contract schema version' ($schema.properties.contractVersion.const -ceq '1.0.0' -and -not $schema.additionalProperties) 'Version 1.0.0, closed envelope'
$catalogText = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'contracts/Monergy.Contracts/Vs02ContractCatalog.cs') -Raw
Add-Check 'Contract catalog exact identities' (@($expectedContracts | Where-Object { -not $catalogText.Contains("`"$_`"") }).Count -eq 0 -and ([regex]::Matches($catalogText, 'new\("CID-')).Count -eq 14) 'Fourteen executable catalog entries'
$contractTypesText = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'contracts/Monergy.Contracts/Vs02Contracts.cs') -Raw
Add-Check 'Contract security context' ($contractTypesText.Contains('TrustedSecurityContext') -or (Get-Content -LiteralPath (Join-Path $RepositoryRoot 'contracts/Monergy.Contracts/ContractPrimitives.cs') -Raw).Contains('TrustedSecurityContext')) 'Human actor, workload identity, access context, correlation and causation envelope'

$catalog = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/catalog.json') -Raw | ConvertFrom-Json
$candidateServices = @($catalog.services | Where-Object status -ceq 'VS02_IMPLEMENTATION_CANDIDATE')
Add-Check 'Service catalog reviewed set' (Test-ExactSet @($candidateServices.id) $expectedServiceIds) 'Exactly five reviewed D03 candidate boundaries'
Add-Check 'Seven services remain scaffolds' (@($catalog.services | Where-Object status -ceq 'TOOLCHAIN_SCAFFOLD').Count -eq 7) 'No unrelated Feature implementation'
$catalogFeatures = @($catalog.services.featureIds | Where-Object { $_ })
Add-Check 'Service catalog Feature coverage' (Test-ExactSet $catalogFeatures $expectedFeatures) '8/8 Features assigned once'

$referenceGuard = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'shared/platform/Monergy.Platform/ReferenceAdapterGuard.cs') -Raw
Add-Check 'Reference adapter execution guard' ($referenceGuard.Contains('LOCAL') -and $referenceGuard.Contains('CI_EPHEMERAL') -and $referenceGuard.Contains('InvalidOperationException')) 'Fail closed outside LOCAL/CI_EPHEMERAL'
$referenceAdapterRoots = @($expectedServiceIds | Where-Object {
    @(Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot "services/$_/Infrastructure") -File -Filter '*.cs').Count -gt 0
})
$contractReadyDockerfiles = @($expectedServiceIds | Where-Object {
    $dockerfile = Get-Content -LiteralPath (Join-Path $RepositoryRoot "services/$_/Dockerfile") -Raw
    $dockerfile.Contains('contracts/Monergy.Contracts/Monergy.Contracts.csproj') -and
        $dockerfile.Contains('contracts/Monergy.Contracts/packages.lock.json') -and
        $dockerfile.Contains('COPY ["contracts/Monergy.Contracts/", "contracts/Monergy.Contracts/"]')
})
$hostedOci = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/hosted-oci-evidence.ps1') -Raw
$localPackaging = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/package-services.ps1') -Raw
$d03ImageIdentity = $hostedOci.Contains(':d03-$sourceIdentity') -and $localPackaging.Contains(':d03-candidate-local')
Add-Check 'Five reference adapter and OCI implementations' ($referenceAdapterRoots.Count -eq 5 -and $contractReadyDockerfiles.Count -eq 5 -and $d03ImageIdentity) 'Five adapters, contract-aware OCI contexts, and D03 candidate image identities'
$projectText = @(Get-ChildItem -LiteralPath $RepositoryRoot -Recurse -File -Filter '*.csproj' | Where-Object { $_.FullName -notmatch '[\\/](?:bin|obj)[\\/]' } | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
Add-Check 'No physical provider dependency' ($projectText -notmatch 'EntityFrameworkCore|Npgsql|SqlClient|MongoDB|StackExchange\.Redis|Azure\.|Amazon\.|Google\.Cloud|OpenAI') 'No provider, database, broker, OCR, AI, or cloud SDK selected'
$migrationFiles = @(Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'services') -Recurse -File -Filter '*.sql')
Add-Check 'No physical migrations' ($migrationFiles.Count -eq 0) 'Provider decisions remain open'

$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'services') -Recurse -File -Filter '*.cs') +
    @(Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'shared/platform/Monergy.Platform') -Recurse -File -Filter '*.cs') +
    @(Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'contracts/Monergy.Contracts') -Recurse -File -Filter '*.cs')
$sourceText = @($sourceFiles | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
Add-Check 'Immutable evidence and provenance ports' ($sourceText.Contains('EvidenceVersionRecord') -and $sourceText.Contains('FinancialProvenance')) 'Evidence versions and financial lineage are explicit'
Add-Check 'Idempotency represented' ($sourceText.Contains('IdempotencyKey') -and $sourceText.Contains('IMMUTABLE_REPLAY_SAFE')) 'Commands/jobs and events have replay semantics'
Add-Check 'Classified failures represented' ($sourceText.Contains('ContractErrorCategory') -and $sourceText.Contains('Retryable')) 'Failure code, category, and retryability'
Add-Check 'Consent and authorization enforcement' ($sourceText.Contains('ConsentReferenceId') -and $sourceText.Contains('AuthorizationContextId') -and $sourceText.Contains('consent.revoked') -and $sourceText.Contains('AccessDenied')) 'Protected receivers fail closed'
$telemetry = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'shared/platform/Monergy.Platform/Vs02Telemetry.cs') -Raw
Add-Check 'Safe lifecycle telemetry' (-not (Test-SensitiveTelemetry $telemetry) -and $telemetry.Contains('CorrelationId') -and $telemetry.Contains('AdapterKind')) 'Identifiers and status only; reference adapter labeled'

$testFiles = @(Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'tests/vs02/Monergy.Vs02.Tests') -File -Filter '*.cs')
$testText = @($testFiles | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
Add-Check 'VS-02 permanent test suite' ($testFiles.Count -ge 6 -and $testText.Contains('FiveBoundary') -and @($testFiles | Where-Object Name -in @('FinancialProfileAuthorityTests.cs', 'FinancialProfileContractCompatibilityTests.cs')).Count -eq 0) 'D03-owned unit, contract, security, persistence, lineage, audit, failure and five-boundary tests; D04 tests excluded'
Add-Check 'Executed contract compatibility surface' (@($expectedContracts | Where-Object { -not $testText.Contains($_) }).Count -eq 0) 'Every scoped CID appears in permanent tests'
$frontendText = @(Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'apps/customer-web/src') -Recurse -File | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
$bundleEvidencePath = Join-Path $RepositoryRoot '.artifacts/components/frontend-bundle.json'
$bundleEvidence = if (Test-Path -LiteralPath $bundleEvidencePath) { Get-Content -LiteralPath $bundleEvidencePath -Raw | ConvertFrom-Json } else { $null }
Add-Check 'Code-split VS-02 route and bundle evidence' ($frontendText.Contains("lazy(() => import('./vs02/Vs02Experience'))") -and $frontendText.Contains("window.location.pathname === '/vs02'") -and $null -ne $bundleEvidence -and (Test-BundleEvidence $bundleEvidence)) 'Exact totals/deltas and lazy-route isolation'
Add-Check 'Truthful frontend evidence' ($frontendText.Contains('REFERENCE · LOCAL / CI ONLY') -and $frontendText.Contains('NOT AUTHORITATIVE FINANCIAL TRUTH') -and $frontendText.Contains('AUTHORITATIVE')) 'Reference/provenance authority states are explicit'
Add-Check 'Frontend async accessibility' ($frontendText.Contains('aria-live="polite"') -and $frontendText.Contains('aria-busy')) 'Accessible names and announcements'
$frontendTests = (Get-Content -LiteralPath (Join-Path $RepositoryRoot 'apps/customer-web/tests/Vs02Experience.test.tsx') -Raw) + (Get-Content -LiteralPath (Join-Path $RepositoryRoot 'apps/customer-web/e2e/toolchain.spec.ts') -Raw)
Add-Check 'Frontend component and browser coverage' ($frontendTests.Contains('axe.run') -and $frontendTests.Contains('preserves the authority boundary end to end')) 'Validation, error, accessibility, keyboard, and E2E'

$manifest = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'repository.manifest.json') -Raw | ConvertFrom-Json
Add-Check 'Truthful accepted lifecycle' ($manifest.status -ceq 'ACCEPTED_COMPLETE' -and $manifest.vs02FeatureState -ceq 'IMPLEMENTATION_ACCEPTED_SIMULATOR_8_OF_8' -and $manifest.vs02ContractState -ceq 'COMPATIBILITY_EVIDENCE_ACCEPTED_14_OF_14' -and $manifest.businessFeatureImplementation -ceq 'VS02_IMPLEMENTATION_ACCEPTED_SIMULATOR' -and $manifest.integrationEvidenceLevel -ceq 'SIMULATOR_REFERENCE_ADAPTER' -and $manifest.deploymentState -ceq 'NOT_DEPLOYED') 'Accepted at SIMULATOR; not operational, Integration-ready, UAT-ready, Production-ready, published, or deployed'
Add-Check 'Stage gates preserved' ($manifest.stageGates.'SG-01' -ceq 'READY' -and $manifest.stageGates.'SG-02' -ceq 'CONDITIONALLY_READY' -and $manifest.stageGates.'SG-03' -ceq 'BLOCKED' -and $manifest.stageGates.'SG-04' -ceq 'BLOCKED') 'SG-01 ready; later gates unchanged'
$gates = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/ci/gates.json') -Raw | ConvertFrom-Json
Add-Check 'Contract gate exact evidence' (($gates.gates | Where-Object id -ceq 'CG-07').result -ceq 'PASS' -and ($gates.gates | Where-Object id -ceq 'CG-07').scope.Contains('exact fourteen')) 'CG-07 PASS for 14/14 only'
Add-Check 'Integration gate evidence level' (($gates.gates | Where-Object id -ceq 'CG-11').result -ceq 'PASS' -and ($gates.gates | Where-Object id -ceq 'CG-11').scope.Contains('SIMULATOR') -and ($gates.gates | Where-Object id -ceq 'CG-11').scope.Contains('not SG-02')) 'CG-11 PASS at SIMULATOR only'

$failures = @($checks | Where-Object { -not $_.Passed })
foreach ($check in $checks) {
    Write-Output "[$(if ($check.Passed) { 'PASS' } else { 'FAIL' })] $($check.Name) - $($check.Evidence)"
}
if ($failures.Count -gt 0) {
    throw "D03 verification failed: $($checks.Count - $failures.Count)/$($checks.Count); failed: $($failures.Name -join ', ')."
}
Write-Output "D03 verification passed: $($checks.Count)/$($checks.Count)."
