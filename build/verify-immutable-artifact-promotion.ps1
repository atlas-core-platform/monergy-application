[CmdletBinding()]
param(
    [switch]$SelfTest,
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scopePath = Join-Path $RepositoryRoot 'build/governance/d16-scope-lock.json'
$newArtifactPath = Join-Path $RepositoryRoot 'build/release/New-ImmutableArtifact.ps1'
$testArtifactPath = Join-Path $RepositoryRoot 'build/release/Test-ImmutableArtifact.ps1'
$promotePath = Join-Path $RepositoryRoot 'build/release/Invoke-ReferencePromotion.ps1'
$testPromotionPath = Join-Path $RepositoryRoot 'build/release/Test-ReferencePromotion.ps1'
$modulePath = Join-Path $RepositoryRoot 'build/release/ImmutableArtifact.Common.psm1'
Import-Module $modulePath -Force
$scope = Get-Content -LiteralPath $scopePath -Raw -Encoding utf8 | ConvertFrom-Json

function Test-ExactSet([object[]]$Actual, [object[]]$Expected) {
    [string[]]$actualValues = @($Actual | ForEach-Object { [string]$_ })
    [string[]]$expectedValues = @($Expected | ForEach-Object { [string]$_ })
    [Array]::Sort($actualValues, [StringComparer]::Ordinal)
    [Array]::Sort($expectedValues, [StringComparer]::Ordinal)
    return $actualValues.Count -eq $expectedValues.Count -and ($actualValues -join '|') -ceq ($expectedValues -join '|')
}

function Test-Scope([object]$Value) {
    return $Value.schemaVersion -ceq '1.0.0' -and
        $Value.deliverable -ceq 'MWP-03-D16' -and
        $Value.status -ceq 'CANDIDATE_PENDING_CTO_REVIEW' -and
        $Value.feature.id -ceq 'M2-WS01-E02-F02' -and
        $Value.feature.readiness -ceq 'READY' -and
        $Value.startingCommits.application -ceq 'ac6407c3595f255fd5482ae759e9b7946e6e830d' -and
        $Value.startingCommits.architecture -ceq '41cbd8178884d3f478e027c8d636e50a053edcc7' -and
        $Value.contractTrace -ceq 'INTERNAL_ONLY' -and
        @($Value.changedDomainServices).Count -eq 0 -and
        @($Value.newOrChangedContracts).Count -eq 0 -and
        $Value.businessObjectChanges -ceq 'NONE' -and
        $Value.databaseSchemaChanges -ceq 'NONE' -and
        $Value.frontendProductBehaviorChanges -ceq 'NONE' -and
        $Value.sharedPlatformRuntimeChanges -ceq 'NONE' -and
        $Value.productionTechnologySelection -ceq 'NONE' -and
        $Value.dependencyChange -ceq 'NONE' -and
        $Value.runtimePinChange -ceq 'NONE' -and
        $Value.containerDefinitionChange -ceq 'NONE' -and
        $Value.evidenceBoundary -ceq 'LOCAL_CI_EPHEMERAL_REFERENCE_ONLY' -and
        $Value.promotionMode -ceq 'BUILD_ONCE_PROMOTE_BY_DIGEST_WITHOUT_REBUILD' -and
        $Value.configurationTreatment -ceq 'SEPARATE_VERSIONED_REFERENCE' -and
        (Test-ExactSet @($Value.allowedReferenceTargets) @('DEV', 'QA', 'UAT')) -and
        $Value.productionPromotion -ceq 'NOT_IMPLEMENTED' -and
        $Value.publication -ceq 'NOT_IMPLEMENTED' -and
        $Value.deployment -ceq 'NOT_IMPLEMENTED' -and
        $Value.historicalSemantics -ceq 'D01_THROUGH_D15_PRESERVED' -and
        $Value.stageGates.'SG-01' -ceq 'READY' -and
        $Value.stageGates.'SG-02' -ceq 'CONDITIONALLY_READY' -and
        $Value.stageGates.'SG-03' -ceq 'BLOCKED' -and
        $Value.stageGates.'SG-04' -ceq 'BLOCKED'
}

function Test-D05HistoricalFiles([object]$Value) {
    foreach ($property in $Value.d05HistoricalFiles.PSObject.Properties) {
        $path = Join-Path $RepositoryRoot $property.Name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $false }
        $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -cne [string]$property.Value) { return $false }
    }
    return @($Value.d05HistoricalFiles.PSObject.Properties).Count -eq 4
}

function Test-Throws([scriptblock]$Operation) {
    try {
        & $Operation *> $null
        return $false
    }
    catch {
        return $true
    }
}

function Write-Json([object]$Value, [string]$Path) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))
}

function Copy-Capsule([string]$ManifestPath, [string]$Destination) {
    $source = Split-Path -Parent $ManifestPath
    Copy-Item -LiteralPath $source -Destination $Destination -Recurse
    return Join-Path $Destination 'artifact-manifest.json'
}

function New-ProvenanceMismatchCandidate {
    param(
        [string]$ManifestPath,
        [string]$Destination,
        [scriptblock]$Mutation
    )
    $copiedManifestPath = Copy-Capsule $ManifestPath $Destination
    $manifest = Get-Content -LiteralPath $copiedManifestPath -Raw | ConvertFrom-Json
    $provenancePath = Join-Path (Split-Path -Parent $copiedManifestPath) $manifest.evidence.provenance.path
    $provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json
    & $Mutation $provenance
    Write-Json $provenance $provenancePath
    $manifest.evidence.provenance.sha256 = (Get-FileHash -LiteralPath $provenancePath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Json $manifest $copiedManifestPath
    return $copiedManifestPath
}

function New-PromotionMismatchRecord {
    param(
        [string]$PromotionPath,
        [string]$Destination,
        [scriptblock]$Mutation
    )
    $record = Get-Content -LiteralPath $PromotionPath -Raw | ConvertFrom-Json
    & $Mutation $record
    Write-Json $record $Destination
    return $Destination
}

if ($SelfTest) {
    $selfTestRoot = Join-Path $RepositoryRoot '.artifacts/d16/self-test'
    $caseRoot = Join-Path $selfTestRoot ([Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $caseRoot -Force | Out-Null
    try {
        $inputRoot = Join-Path $caseRoot 'input'
        New-Item -ItemType Directory -Path $inputRoot -Force | Out-Null
        $payloadPath = Join-Path $inputRoot 'monergy-application.bundle'
        $sbomPath = Join-Path $inputRoot 'sbom.json'
        $vulnerabilityPath = Join-Path $inputRoot 'vulnerability.json'
        [IO.File]::WriteAllText($payloadPath, 'already-built-monergy-application', [Text.UTF8Encoding]::new($false))
        Write-Json ([ordered]@{ bomFormat = 'CycloneDX'; specVersion = '1.6'; components = @() }) $sbomPath
        Write-Json ([ordered]@{ schemaVersion = '1.0.0'; check = 'vulnerability'; result = 'PASS' }) $vulnerabilityPath

        $sourceRevision = 'ac6407c3595f255fd5482ae759e9b7946e6e830d'
        $sourceTreeOutput = @(& git -C $RepositoryRoot rev-parse --verify "$sourceRevision^{tree}")
        if ($LASTEXITCODE -ne 0 -or $sourceTreeOutput.Count -ne 1) { throw 'Self-test source tree could not be resolved.' }
        $sourceTree = ([string]$sourceTreeOutput[0]).Trim().ToLowerInvariant()
        $candidateRoot = Join-Path $caseRoot 'candidates'
        $candidateOutput = @(& $newArtifactPath `
            -ReleaseIdentity 'mwp03-d16-self-test' `
            -Component 'monergy-application' `
            -Version '16.0.0-candidate.1' `
            -SourceRevision $sourceRevision `
            -SourceTree $sourceTree `
            -PipelineIdentity 'd16-local-self-test' `
            -BuildInvocationIdentity 'd16-self-test-001' `
            -BuiltAtUtc '2026-10-06T00:00:00Z' `
            -ArtifactPath $payloadPath `
            -SbomPath $sbomPath `
            -VulnerabilityEvidencePath $vulnerabilityPath `
            -ArtifactStoreRoot $candidateRoot)
        $candidateManifestPath = [string]$candidateOutput[-1]
        $candidate = & $testArtifactPath -ManifestPath $candidateManifestPath -PassThru
        $normalizedIsoTimestamp = ConvertTo-D16UtcTimestamp '2026-10-06T00:00:00Z' 'Self-test ISO timestamp'
        $utcDateTimeTimestamp = [DateTime]::new(2026, 10, 6, 0, 0, 0, [DateTimeKind]::Utc)
        $normalizedUtcDateTime = ConvertTo-D16UtcTimestamp $utcDateTimeTimestamp 'Self-test UTC DateTime'
        $zeroOffsetTimestamp = [DateTimeOffset]::new(2026, 10, 6, 0, 0, 0, [TimeSpan]::Zero)
        $normalizedZeroOffset = ConvertTo-D16UtcTimestamp $zeroOffsetTimestamp 'Self-test zero-offset DateTimeOffset'
        $localDateTimeTimestamp = [DateTime]::SpecifyKind($utcDateTimeTimestamp, [DateTimeKind]::Local)
        $nonZeroOffsetTimestamp = [DateTimeOffset]::new(2026, 10, 6, 0, 0, 0, [TimeSpan]::FromHours(1))
        $payloadHashBefore = (Get-FileHash -LiteralPath $candidate.ArtifactPath -Algorithm SHA256).Hash
        $payloadWriteBefore = (Get-Item -LiteralPath $candidate.ArtifactPath).LastWriteTimeUtc.Ticks
        $candidateHashBefore = (Get-FileHash -LiteralPath $candidateManifestPath -Algorithm SHA256).Hash
        $reuseOutput = @(& $newArtifactPath `
            -ReleaseIdentity 'mwp03-d16-self-test' `
            -Component 'monergy-application' `
            -Version '16.0.0-candidate.1' `
            -SourceRevision $sourceRevision `
            -SourceTree $sourceTree `
            -PipelineIdentity 'd16-local-self-test' `
            -BuildInvocationIdentity 'd16-self-test-001' `
            -BuiltAtUtc '2026-10-06T00:00:00Z' `
            -ArtifactPath $payloadPath `
            -SbomPath $sbomPath `
            -VulnerabilityEvidencePath $vulnerabilityPath `
            -ArtifactStoreRoot $candidateRoot)
        $reuseManifestPath = [string]$reuseOutput[-1]
        $candidateHashAfterReuse = (Get-FileHash -LiteralPath $candidateManifestPath -Algorithm SHA256).Hash

        $promotionRoot = Join-Path $caseRoot 'promotions'
        $promotionOutput = @(& $promotePath `
            -CandidateManifestPath $candidateManifestPath `
            -ExecutionZone LOCAL `
            -TargetEnvironmentReference QA `
            -ConfigurationReference 'config/qa/customer-safe' `
            -ConfigurationRevision 'config-revision-001' `
            -OutputRoot $promotionRoot)
        $promotionPath = [string]$promotionOutput[-1]
        $promotion = & $testPromotionPath -PromotionPath $promotionPath -CandidateManifestPath $candidateManifestPath -PassThru
        $repeatPath = [string](@(& $promotePath `
            -CandidateManifestPath $candidateManifestPath `
            -ExecutionZone LOCAL `
            -TargetEnvironmentReference QA `
            -ConfigurationReference 'config/qa/customer-safe' `
            -ConfigurationRevision 'config-revision-001' `
            -OutputRoot $promotionRoot)[-1])
        $secondConfigPath = [string](@(& $promotePath `
            -CandidateManifestPath $candidateManifestPath `
            -ExecutionZone LOCAL `
            -TargetEnvironmentReference QA `
            -ConfigurationReference 'config/qa/customer-safe' `
            -ConfigurationRevision 'config-revision-002' `
            -OutputRoot $promotionRoot)[-1])
        $secondPromotion = & $testPromotionPath -PromotionPath $secondConfigPath -CandidateManifestPath $candidateManifestPath -PassThru

        $tamperedManifest = Copy-Capsule $candidateManifestPath (Join-Path $caseRoot 'tampered-artifact')
        [IO.File]::AppendAllText((Join-Path (Split-Path -Parent $tamperedManifest) 'artifact/payload.bin'), '-tampered')

        $digestMismatchManifest = Copy-Capsule $candidateManifestPath (Join-Path $caseRoot 'digest-mismatch')
        $digestMismatch = Get-Content -LiteralPath $digestMismatchManifest -Raw | ConvertFrom-Json
        $digestMismatch.artifact.digest = 'sha256:' + ('0' * 64)
        Write-Json $digestMismatch $digestMismatchManifest

        $sourceMismatchManifest = Copy-Capsule $candidateManifestPath (Join-Path $caseRoot 'source-mismatch')
        $sourceMismatch = Get-Content -LiteralPath $sourceMismatchManifest -Raw | ConvertFrom-Json
        $sourceProvenancePath = Join-Path (Split-Path -Parent $sourceMismatchManifest) $sourceMismatch.evidence.provenance.path
        $sourceProvenance = Get-Content -LiteralPath $sourceProvenancePath -Raw | ConvertFrom-Json
        $sourceProvenance.source.revision = '2222222222222222222222222222222222222222'
        Write-Json $sourceProvenance $sourceProvenancePath
        $sourceMismatch.evidence.provenance.sha256 = (Get-FileHash -LiteralPath $sourceProvenancePath -Algorithm SHA256).Hash.ToLowerInvariant()
        Write-Json $sourceMismatch $sourceMismatchManifest

        $missingMetadataManifest = Copy-Capsule $candidateManifestPath (Join-Path $caseRoot 'missing-metadata')
        $missingMetadata = Get-Content -LiteralPath $missingMetadataManifest -Raw | ConvertFrom-Json
        $missingMetadata.evidence.PSObject.Properties.Remove('sbom')
        Write-Json $missingMetadata $missingMetadataManifest

        $unauthorizedManifest = Copy-Capsule $candidateManifestPath (Join-Path $caseRoot 'unauthorized')
        $unauthorized = Get-Content -LiteralPath $unauthorizedManifest -Raw | ConvertFrom-Json
        $unauthorized.promotionPolicy.authorizationState = 'NOT_AUTHORIZED'
        Write-Json $unauthorized $unauthorizedManifest

        $malformedPromotionPath = Join-Path $caseRoot 'malformed-promotion.json'
        $malformedPromotion = Get-Content -LiteralPath $promotionPath -Raw | ConvertFrom-Json
        $malformedPromotion.PSObject.Properties.Remove('configuration')
        Write-Json $malformedPromotion $malformedPromotionPath

        $alteredPromotionPath = Join-Path $caseRoot 'altered-promotion.json'
        $alteredPromotion = Get-Content -LiteralPath $promotionPath -Raw | ConvertFrom-Json
        $alteredPromotion.artifact.digest = 'sha256:' + ('f' * 64)
        Write-Json $alteredPromotion $alteredPromotionPath

        $provenanceComponentMismatch = New-ProvenanceMismatchCandidate $candidateManifestPath (Join-Path $caseRoot 'provenance-component-mismatch') {
            param($provenance)
            $provenance.subject.component = 'contradictory-component'
        }
        $provenanceVersionMismatch = New-ProvenanceMismatchCandidate $candidateManifestPath (Join-Path $caseRoot 'provenance-version-mismatch') {
            param($provenance)
            $provenance.subject.version = '99.99.99-contradictory'
        }
        $provenanceRepositoryMismatch = New-ProvenanceMismatchCandidate $candidateManifestPath (Join-Path $caseRoot 'provenance-repository-mismatch') {
            param($provenance)
            $provenance.source.repository = 'contradictory-repository'
        }
        $provenanceBuildTimeMismatch = New-ProvenanceMismatchCandidate $candidateManifestPath (Join-Path $caseRoot 'provenance-build-time-mismatch') {
            param($provenance)
            $provenance.build.builtAtUtc = '2026-10-06T00:00:01Z'
        }
        $provenanceNormalizedBuildTimeMismatch = New-ProvenanceMismatchCandidate $candidateManifestPath (Join-Path $caseRoot 'provenance-normalized-build-time-mismatch') {
            param($provenance)
            $provenance.build.builtAtUtc = '2026-10-06T00:00:00.0000001Z'
        }

        $promotionReleaseMismatch = New-PromotionMismatchRecord $promotionPath (Join-Path $caseRoot 'promotion-release-mismatch.json') {
            param($record)
            $record.artifact.releaseIdentity = 'contradictory-release'
        }
        $promotionComponentMismatch = New-PromotionMismatchRecord $promotionPath (Join-Path $caseRoot 'promotion-component-mismatch.json') {
            param($record)
            $record.artifact.component = 'contradictory-component'
        }
        $promotionVersionMismatch = New-PromotionMismatchRecord $promotionPath (Join-Path $caseRoot 'promotion-version-mismatch.json') {
            param($record)
            $record.artifact.version = '99.99.99-contradictory'
        }

        $promoteSource = Get-Content -LiteralPath $promotePath -Raw -Encoding utf8
        $candidateAfterSecondConfiguration = Get-FileHash -LiteralPath $candidateManifestPath -Algorithm SHA256
        $checks = [ordered]@{
            '1 Build-once candidate and reference promotion succeed' =
                $candidate.Manifest.status -ceq 'CANDIDATE_LOCAL_CI_ONLY' -and $promotion.Record.status -ceq 'DRY_RUN_REFERENCE_ONLY'
            '2 Exact digest is preserved across promotion' =
                $promotion.Record.artifact.digest -ceq $candidate.ArtifactDigest
            '3 Artifact tampering fails closed' =
                (Test-Throws { & $testArtifactPath -ManifestPath $tamperedManifest })
            '4 Digest mismatch fails closed' =
                (Test-Throws { & $testArtifactPath -ManifestPath $digestMismatchManifest })
            '5 Source and provenance mismatch fails closed' =
                (Test-Throws { & $testArtifactPath -ManifestPath $sourceMismatchManifest })
            '6 Missing mandatory integrity metadata fails closed' =
                (Test-Throws { & $testArtifactPath -ManifestPath $missingMetadataManifest })
            '7 Unauthorized promotion state fails closed' =
                (Test-Throws { & $promotePath -CandidateManifestPath $unauthorizedManifest -ExecutionZone LOCAL -TargetEnvironmentReference QA -ConfigurationReference config/qa -ConfigurationRevision revision-1 -OutputRoot $promotionRoot })
            '8 Promotion does not invoke rebuild or mutate artifact material' =
                $promotion.Record.rebuildInvoked -eq $false -and
                $payloadHashBefore -ceq (Get-FileHash -LiteralPath $candidate.ArtifactPath -Algorithm SHA256).Hash -and
                $payloadWriteBefore -eq (Get-Item -LiteralPath $candidate.ArtifactPath).LastWriteTimeUtc.Ticks -and
                -not ($promoteSource -match '(?i)(dotnet|pnpm|npm|docker)\s+(?:run\s+)?build|New-ImmutableArtifact')
            '9 Configuration remains separate and does not change artifact identity' =
                $candidateHashBefore -ceq $candidateAfterSecondConfiguration.Hash -and
                $secondPromotion.Record.artifact.digest -ceq $candidate.ArtifactDigest -and
                $secondPromotion.Record.configuration.revision -ceq 'config-revision-002' -and
                $secondPromotion.Record.promotionIdentity -cne $promotion.Record.promotionIdentity -and
                $null -eq $candidate.Manifest.PSObject.Properties['configuration']
            '10 Promotion output is deterministic and idempotent' =
                $repeatPath -ceq $promotionPath
            '11 Malformed promotion records fail closed' =
                (Test-Throws { & $testPromotionPath -PromotionPath $malformedPromotionPath -CandidateManifestPath $candidateManifestPath })
            '12 Altered promotion identity fails closed' =
                (Test-Throws { & $testPromotionPath -PromotionPath $alteredPromotionPath -CandidateManifestPath $candidateManifestPath })
            '13 Production reference promotion is not implemented' =
                (Test-Throws { & $promotePath -CandidateManifestPath $candidateManifestPath -ExecutionZone LOCAL -TargetEnvironmentReference PRODUCTION -ConfigurationReference config/prod -ConfigurationRevision revision-1 -OutputRoot $promotionRoot })
            '14 D05 historical acceptance semantics remain byte-identical' =
                (Test-D05HistoricalFiles $scope)
            '15 Scope and non-claims remain bounded' =
                (Test-Scope $scope) -and $promotion.Record.nonClaims.published -eq $false -and $promotion.Record.nonClaims.deployed -eq $false
            '16 Syntactically valid but nonexistent source commit is rejected' =
                (Test-Throws { & $newArtifactPath -ReleaseIdentity 'invalid-source-commit' -Component 'monergy-application' -Version '16.0.0-candidate.1' -SourceRevision ('0' * 40) -SourceTree ('1' * 40) -PipelineIdentity 'd16-local-self-test' -BuildInvocationIdentity 'invalid-source-commit' -BuiltAtUtc '2026-10-06T00:00:00Z' -ArtifactPath $payloadPath -SbomPath $sbomPath -VulnerabilityEvidencePath $vulnerabilityPath -ArtifactStoreRoot (Join-Path $caseRoot 'invalid-source-candidates') })
            '17 Syntactically valid but incorrect commit-tree pairing is rejected' =
                (Test-Throws { & $newArtifactPath -ReleaseIdentity 'invalid-source-tree' -Component 'monergy-application' -Version '16.0.0-candidate.1' -SourceRevision $sourceRevision -SourceTree ('2' * 40) -PipelineIdentity 'd16-local-self-test' -BuildInvocationIdentity 'invalid-source-tree' -BuiltAtUtc '2026-10-06T00:00:00Z' -ArtifactPath $payloadPath -SbomPath $sbomPath -VulnerabilityEvidencePath $vulnerabilityPath -ArtifactStoreRoot (Join-Path $caseRoot 'invalid-source-candidates') })
            '18 Contradictory provenance subject component is rejected' =
                (Test-Throws { & $testArtifactPath -ManifestPath $provenanceComponentMismatch })
            '19 Contradictory provenance subject version is rejected' =
                (Test-Throws { & $testArtifactPath -ManifestPath $provenanceVersionMismatch })
            '20 Contradictory provenance source repository is rejected' =
                (Test-Throws { & $testArtifactPath -ManifestPath $provenanceRepositoryMismatch })
            '21 Contradictory provenance build time is rejected' =
                (Test-Throws { & $testArtifactPath -ManifestPath $provenanceBuildTimeMismatch })
            '22 Contradictory promotion release identity is rejected' =
                (Test-Throws { & $testPromotionPath -PromotionPath $promotionReleaseMismatch -CandidateManifestPath $candidateManifestPath })
            '23 Contradictory promotion component is rejected' =
                (Test-Throws { & $testPromotionPath -PromotionPath $promotionComponentMismatch -CandidateManifestPath $candidateManifestPath })
            '24 Contradictory promotion version is rejected' =
                (Test-Throws { & $testPromotionPath -PromotionPath $promotionVersionMismatch -CandidateManifestPath $candidateManifestPath })
            '25 Governed ISO UTC string timestamp is accepted' =
                $normalizedIsoTimestamp -ceq '2026-10-06T00:00:00.0000000Z'
            '26 UTC DateTime timestamp is accepted and normalized deterministically' =
                $normalizedUtcDateTime -ceq '2026-10-06T00:00:00.0000000Z'
            '27 Equivalent string DateTime and zero-offset DateTimeOffset values normalize identically' =
                $normalizedIsoTimestamp -ceq $normalizedUtcDateTime -and
                $normalizedIsoTimestamp -ceq $normalizedZeroOffset
            '28 Local DateTime and non-zero DateTimeOffset values fail closed' =
                (Test-Throws { ConvertTo-D16UtcTimestamp $localDateTimeTimestamp 'Self-test local DateTime' }) -and
                (Test-Throws { ConvertTo-D16UtcTimestamp $nonZeroOffsetTimestamp 'Self-test non-zero DateTimeOffset' })
            '29 Malformed and non-Z string timestamps fail closed' =
                (Test-Throws { ConvertTo-D16UtcTimestamp 'not-a-timestamp' 'Self-test malformed timestamp' }) -and
                (Test-Throws { ConvertTo-D16UtcTimestamp '2026-10-06T00:00:00+00:00' 'Self-test non-Z timestamp' })
            '30 Contradictory normalized manifest and provenance UTC times fail closed' =
                (Test-Throws { & $testArtifactPath -ManifestPath $provenanceNormalizedBuildTimeMismatch })
            '31 Existing digest-keyed capsule accepts the equivalent caller UTC string' =
                $reuseManifestPath -ceq $candidateManifestPath -and
                $candidateHashAfterReuse -ceq $candidateHashBefore
        }
        foreach ($entry in $checks.GetEnumerator()) {
            Write-Output "SELF-TEST $(if ($entry.Value) { 'PASS' } else { 'FAIL' }): $($entry.Key)"
        }
        $failed = @($checks.GetEnumerator() | Where-Object { -not $_.Value })
        if ($failed.Count) { throw "D16 negative/self-tests failed: $($checks.Count - $failed.Count)/$($checks.Count)." }
        Write-Output "D16 negative/self-tests passed: $($checks.Count)/$($checks.Count)."
    }
    finally {
        $resolvedSelfTestRoot = [IO.Path]::GetFullPath($selfTestRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
        $resolvedCaseRoot = [IO.Path]::GetFullPath($caseRoot)
        if ((Test-Path -LiteralPath $resolvedCaseRoot) -and
            $resolvedCaseRoot.StartsWith("$resolvedSelfTestRoot$([IO.Path]::DirectorySeparatorChar)", [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolvedCaseRoot -Recurse -Force
        }
    }
    exit 0
}

$requiredPaths = @($scopePath, $newArtifactPath, $testArtifactPath, $promotePath, $testPromotionPath, $modulePath)
$promoteSource = Get-Content -LiteralPath $promotePath -Raw -Encoding utf8
$moduleSource = Get-Content -LiteralPath $modulePath -Raw -Encoding utf8
$checks = [ordered]@{
    'Bounded D16 scope preserves contracts runtime and deployment non-claims' = Test-Scope $scope
    'Immutable artifact and reference-promotion entrypoints are present' =
        @($requiredPaths | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }).Count -eq $requiredPaths.Count
    'D05 historical acceptance evidence remains byte-identical' = Test-D05HistoricalFiles $scope
    'Promotion path contains no application build or external publication command' =
        -not ($promoteSource -match '(?i)(dotnet|pnpm|npm|docker)\s+(?:run\s+)?build|docker\s+push|publish-|deploy-')
    'Candidate validation binds artifact source provenance and security evidence' =
        @('Artifact payload was altered', 'Artifact provenance does not match', 'Artifact vulnerability evidence', 'environmentConfigurationEmbedded') |
            Where-Object { $moduleSource.Contains($_) } |
            Measure-Object | Select-Object -ExpandProperty Count | ForEach-Object { $_ -eq 4 }
    'Reference promotion remains local or CI dry-run and excludes Production' =
        $promoteSource.Contains("[ValidateSet('LOCAL', 'CI_EPHEMERAL')]") -and
        $promoteSource.Contains("[ValidateSet('DEV', 'QA', 'UAT')]") -and
        $promoteSource.Contains("rebuildInvoked = `$false") -and
        $promoteSource.Contains("published = `$false; deployed = `$false; environmentSuccess = `$false")
}
foreach ($entry in $checks.GetEnumerator()) {
    Write-Output "[$(if ($entry.Value) { 'PASS' } else { 'FAIL' })] $($entry.Key)"
}
$failed = @($checks.GetEnumerator() | Where-Object { -not $_.Value })
$report = [ordered]@{
    schemaVersion = '1.0.0'
    deliverable = 'MWP-03-D16'
    result = if ($failed.Count) { 'FAIL' } else { 'PASS' }
    recordedAtUtc = [DateTime]::UtcNow.ToString('o')
    checks = @($checks.GetEnumerator() | ForEach-Object { [ordered]@{ name = $_.Key; passed = [bool]$_.Value } })
    evidenceBoundary = 'LOCAL_CI_EPHEMERAL_REFERENCE_ONLY'
    published = $false
    deployed = $false
}
$reportPath = Join-Path $RepositoryRoot '.artifacts/d16/verification.json'
$reportParent = Split-Path -Parent $reportPath
New-Item -ItemType Directory -Path $reportParent -Force | Out-Null
Write-Json $report $reportPath
if ($failed.Count) { throw "D16 verification failed: $($checks.Count - $failed.Count)/$($checks.Count)." }
Write-Output "D16 verification passed: $($checks.Count)/$($checks.Count). Evidence: $reportPath"
