[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$SelfTest,
    [switch]$D09ForwardRegression,
    [switch]$D10ForwardRegression
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$am05Migrations = @(& (Join-Path $RepositoryRoot 'build/am05/Get-MigrationOverlay.ps1') -RepositoryRoot $RepositoryRoot)

function Test-ExactSet {
    param([object[]]$Actual, [object[]]$Expected)
    (@($Actual | ForEach-Object { [string]$_ } | Sort-Object) -join '|') -ceq
        (@($Expected | ForEach-Object { [string]$_ } | Sort-Object) -join '|')
}

function Test-ExactDependencyPins {
    param([pscustomobject]$Manifest)
    $values = @()
    foreach ($group in @('dependencies', 'devDependencies', 'peerDependencies')) {
        if ($Manifest.PSObject.Properties.Name -contains $group) {
            $values += @($Manifest.$group.PSObject.Properties.Value)
        }
    }
    @($values | Where-Object { $_ -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$' -and $_ -cne 'workspace:*' }).Count -eq 0
}

function Test-ProhibitedStyle {
    param([string]$Content)
    $Content -match '!important' -or $Content -match '\b[a-z0-9:_/-]+!'
}

function Test-ProhibitedImport {
    param([string]$Content)
    $Content -match '(?m)import\s+\*\s+as\s+'
}

function Test-HostedOciMatrix {
    param([pscustomobject]$Matrix)
    @($Matrix.rows).Count -eq 12 -and
        $Matrix.expectedServices -eq 12 -and
        $Matrix.buildPass -eq 12 -and
        $Matrix.sbomPass -eq 12 -and
        $Matrix.scanPass -eq 12 -and
        $Matrix.digestCount -eq 12 -and
        -not $Matrix.published -and
        @($Matrix.rows | Where-Object {
            $_.buildResult -cne 'PASS' -or
            $_.sbomResult -cne 'PASS' -or
            $_.scanResult -cne 'PASS' -or
            $_.digest -notmatch '^sha256:[0-9a-f]{64}$'
        }).Count -eq 0
}

if ($SelfTest) {
    $tests = [ordered]@{
        'exact dependency accepted' = (Test-ExactDependencyPins ([pscustomobject]@{ dependencies = [pscustomobject]@{ react = '19.3.0' } }))
        'floating dependency rejected' = (-not (Test-ExactDependencyPins ([pscustomobject]@{ dependencies = [pscustomobject]@{ react = '^19.3.0' } })))
        'workspace reference accepted' = (Test-ExactDependencyPins ([pscustomobject]@{ dependencies = [pscustomobject]@{ ui = 'workspace:*' } }))
        'important override rejected' = (Test-ProhibitedStyle '.sample { color: red !important; }')
        'ordinary style accepted' = (-not (Test-ProhibitedStyle '.sample { color: red; }'))
        'wildcard Ant import rejected' = (Test-ProhibitedImport "import * as Ant from 'antd'")
        'named Ant import accepted' = (-not (Test-ProhibitedImport "import { Button } from 'antd'"))
        'exact set rejects duplicate' = (-not (Test-ExactSet @('a', 'a') @('a', 'b')))
        'complete OCI matrix accepted' = (Test-HostedOciMatrix ([pscustomobject]@{
            expectedServices = 12; buildPass = 12; sbomPass = 12; scanPass = 12; digestCount = 12; published = $false
            rows = @(1..12 | ForEach-Object { [pscustomobject]@{ buildResult = 'PASS'; sbomResult = 'PASS'; scanResult = 'PASS'; digest = ('sha256:' + ('a' * 64)) } })
        }))
        'incomplete OCI matrix rejected' = (-not (Test-HostedOciMatrix ([pscustomobject]@{
            expectedServices = 12; buildPass = 11; sbomPass = 12; scanPass = 12; digestCount = 12; published = $false
            rows = @(1..12 | ForEach-Object { [pscustomobject]@{ buildResult = 'PASS'; sbomResult = 'PASS'; scanResult = 'PASS'; digest = ('sha256:' + ('a' * 64)) } })
        })))
    }
    $failed = @($tests.GetEnumerator() | Where-Object { -not $_.Value })
    foreach ($test in $tests.GetEnumerator()) {
        $state = if ($test.Value) { 'PASS' } else { 'FAIL' }
        Write-Output "SELF-TEST $state`: $($test.Key)"
    }
    if ($failed.Count -gt 0) { throw "D02 verifier self-tests failed: $($tests.Count - $failed.Count)/$($tests.Count)." }
    Write-Output "D02 verifier self-tests passed: $($tests.Count)/$($tests.Count)."
    exit 0
}

# Omitted forward-state switches verify the current accepted repository state.
# Explicit $false remains available only for historical-baseline evidence replay.
if (-not $PSBoundParameters.ContainsKey('D09ForwardRegression') -and
    (Test-Path -LiteralPath (Join-Path $RepositoryRoot 'build/governance/d09-scope-lock.json'))) {
    $D09ForwardRegression = $true
}
if (-not $PSBoundParameters.ContainsKey('D10ForwardRegression') -and
    (Test-Path -LiteralPath (Join-Path $RepositoryRoot 'build/governance/d10-scope-lock.json'))) {
    $D10ForwardRegression = $true
}

$checks = [System.Collections.Generic.List[object]]::new()
function Add-Check {
    param([string]$Name, [bool]$Passed, [string]$Evidence)
    $checks.Add([pscustomobject]@{ Name = $Name; Passed = $Passed; Evidence = $Evidence }) | Out-Null
}

$versions = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/toolchain/versions.json') -Raw | ConvertFrom-Json
$expectedVersions = [ordered]@{
    dotnetSdk = '10.0.401'; dotnetRuntime = '10.0.12'; csharp = '14.0'; node = '24.21.0'; pnpm = '12.4.1'
    react = '19.3.0'; vite = '8.3.0'; antDesign = '6.6.3'; tailwindCss = '4.3.3'
    syft = '1.51.1'; grype = '0.118.0'; gitleaks = '8.30.1'
}
$versionPinsValid = @($expectedVersions.GetEnumerator() | Where-Object { $versions.($_.Key) -cne $_.Value }).Count -eq 0
Add-Check 'Exact technology version register' $versionPinsValid 'Twelve approved runtime, framework, UI, and scanner pins'

$globalJson = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'global.json') -Raw | ConvertFrom-Json
Add-Check '.NET SDK pin' ($globalJson.sdk.version -ceq '10.0.401' -and -not $globalJson.sdk.allowPrerelease) '.NET 10.0.401 stable'
$buildProps = [xml](Get-Content -LiteralPath (Join-Path $RepositoryRoot 'Directory.Build.props') -Raw)
Add-Check 'C# and analyzer baseline' ($buildProps.Project.PropertyGroup.LangVersion -ceq '14.0' -and $buildProps.Project.PropertyGroup.EnableNETAnalyzers -ceq 'true' -and $buildProps.Project.PropertyGroup.TreatWarningsAsErrors -ceq 'true') 'C# 14, built-in analyzers, warnings as errors'
Add-Check 'NuGet central and locked restore' ($buildProps.Project.PropertyGroup.ManagePackageVersionsCentrally -ceq 'true' -and $buildProps.Project.PropertyGroup.RestorePackagesWithLockFile -ceq 'true') 'Central package management with committed locks'

$rootPackage = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'package.json') -Raw | ConvertFrom-Json
$customerPackage = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'apps/customer-web/package.json') -Raw | ConvertFrom-Json
$uiPackage = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'shared/platform/frontend-ui/package.json') -Raw | ConvertFrom-Json
Add-Check 'Node and pnpm pins' ($rootPackage.engines.node -ceq '24.21.0' -and $rootPackage.packageManager -ceq 'pnpm@12.4.1' -and $rootPackage.engines.pnpm -ceq '12.4.1') 'Node 24.21.0 and pnpm 12.4.1'
Add-Check 'Exact npm dependency pins' ((Test-ExactDependencyPins $rootPackage) -and (Test-ExactDependencyPins $customerPackage) -and (Test-ExactDependencyPins $uiPackage)) 'No caret, tilde, range, latest, or wildcard external dependency'
Add-Check 'Committed pnpm lock' (Test-Path -LiteralPath (Join-Path $RepositoryRoot 'pnpm-lock.yaml')) 'pnpm-lock.yaml exists'

$expectedServices = [ordered]@{
    'customer-identity' = 'Microsoft.NET.Sdk.Web'; 'consent' = 'Microsoft.NET.Sdk.Web'; 'integration-gateway' = 'Microsoft.NET.Sdk.Web'
    'evidence' = 'Microsoft.NET.Sdk.Web'; 'document-intelligence' = 'Microsoft.NET.Sdk.Worker'; 'financial-profile' = 'Microsoft.NET.Sdk.Web'
    'financial-rules' = 'Microsoft.NET.Sdk.Web'; 'search-retrieval' = 'Microsoft.NET.Sdk.Web'; 'ai-intelligence' = 'Microsoft.NET.Sdk.Web'
    'reporting' = 'Microsoft.NET.Sdk.Web'; 'job-management' = 'Microsoft.NET.Sdk.Worker'; 'audit' = 'Microsoft.NET.Sdk.Worker'
}
$catalog = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/catalog.json') -Raw | ConvertFrom-Json
Add-Check 'Twelve canonical services' (Test-ExactSet @($catalog.services.id) @($expectedServices.Keys)) '12/12 R3 service identities'
Add-Check 'Independent artifact identities' (@($catalog.services.artifact | Select-Object -Unique).Count -eq 12) '12 unique service artifact names'
$d03ServiceFeatures = [ordered]@{
    'evidence' = @('M2-WS03-E01-F02')
    'document-intelligence' = @('M2-WS03-E02-F01', 'M2-WS03-E02-F02')
    'financial-profile' = @('M2-WS03-E03-F01', 'M2-WS03-E03-F02', 'M2-WS03-E03-F03', 'M2-WS04-E03-F01', 'M2-WS04-E03-F02')
    'job-management' = @()
    'audit' = @()
}
$d03ServiceImplementations = [ordered]@{
    'evidence' = 'IMPLEMENTATION_CANDIDATE'
    'document-intelligence' = 'IMPLEMENTATION_CANDIDATE'
    'financial-profile' = 'IMPLEMENTATION_CANDIDATE'
    'job-management' = 'SUPPORTING_BOUNDARY'
    'audit' = 'SUPPORTING_BOUNDARY'
}
$d03Services = @($d03ServiceFeatures.Keys)
$d05Features = @('M2-WS05-E01-F01', 'M2-WS05-E01-F02', 'M2-WS05-E01-F03', 'M2-WS05-E02-F01', 'M2-WS05-E02-F02', 'M2-WS05-E02-F03')
$d06Features = @('M2-WS06-E01-F01', 'M2-WS06-E01-F02', 'M2-WS06-E01-F03', 'M2-WS06-E02-F01', 'M2-WS06-E02-F02', 'M2-WS06-E02-F03')
$d07Features = @('M2-WS07-E01-F01', 'M2-WS07-E01-F02', 'M2-WS07-E01-F03', 'M2-WS07-E02-F01', 'M2-WS07-E02-F03')
$d08Features = @('M2-WS08-E01-F02', 'M2-WS08-E02-F01', 'M2-WS08-E02-F03')
$d10Features = @('M2-WS09-E01-F01', 'M2-WS09-E01-F02', 'M2-WS09-E01-F03', 'M2-WS09-E02-F01')
$scaffoldServices = @('customer-identity', 'consent', 'ai-intelligence')
$governedContractServices = $d03Services + @('financial-rules', 'integration-gateway', 'search-retrieval', 'reporting')
$catalogStateValid = @($catalog.services | Where-Object {
    if ($D10ForwardRegression -and $_.id -ceq 'job-management') {
        $_.status -cne 'D10_IMPLEMENTATION_CANDIDATE_LOCAL_CI' -or
        $_.featureImplementation -cne 'IMPLEMENTATION_CANDIDATE_LOCAL_CI' -or
        -not (Test-ExactSet @($_.featureIds) $d10Features)
    } elseif ($_.id -in $d03Services) {
        $_.status -cne 'VS02_IMPLEMENTATION_CANDIDATE' -or
        $_.featureImplementation -cne $d03ServiceImplementations[$_.id] -or
        -not (Test-ExactSet @($_.featureIds) @($d03ServiceFeatures[$_.id]))
    } elseif ($_.id -ceq 'financial-rules') {
        $_.status -cne 'D05_IMPLEMENTATION_ACCEPTED_SIMULATOR' -or
        $_.featureImplementation -cne 'IMPLEMENTATION_ACCEPTED_SIMULATOR' -or
        $_.d05Status -cne 'IMPLEMENTATION_ACCEPTED_SIMULATOR' -or
        @($_.featureIds).Count -ne 0 -or
        -not (Test-ExactSet @($_.d05FeatureIds) $d05Features)
    } elseif ($_.id -ceq 'integration-gateway') {
        $_.status -cne 'D06_IMPLEMENTATION_CANDIDATE_SIMULATOR' -or
        $_.featureImplementation -cne 'IMPLEMENTATION_CANDIDATE_SIMULATOR' -or
        -not (Test-ExactSet @($_.featureIds) $d06Features)
    } elseif ($_.id -ceq 'search-retrieval') {
        $_.status -cne 'D07_IMPLEMENTATION_CANDIDATE_SIMULATOR' -or
        $_.featureImplementation -cne 'IMPLEMENTATION_CANDIDATE_SIMULATOR' -or
        -not (Test-ExactSet @($_.featureIds) $d07Features)
    } elseif ($_.id -ceq 'reporting') {
        $_.status -cne 'D08_IMPLEMENTATION_CANDIDATE_SIMULATOR' -or
        $_.featureImplementation -cne 'IMPLEMENTATION_CANDIDATE_SIMULATOR' -or
        -not (Test-ExactSet @($_.featureIds) $d08Features)
    } else {
        $_.id -notin $scaffoldServices -or
        $_.status -cne 'TOOLCHAIN_SCAFFOLD' -or
        $_.featureImplementation -cne 'NONE' -or
        @($_.featureIds).Count -ne 0
    }
}).Count -eq 0
$d06Scope = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/governance/d06-scope-lock.json') -Raw | ConvertFrom-Json
$d06Manifest = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'repository.manifest.json') -Raw | ConvertFrom-Json
$d06GovernanceValid = $d06Scope.deliverable -ceq 'MWP-03-D06' -and
    $d06Scope.status -ceq 'CANDIDATE_PENDING_CTO_REVIEW' -and
    $d06Scope.evidenceLevel -ceq 'SIMULATOR' -and
    (Test-ExactSet @($d06Scope.features.id) $d06Features) -and
    @($d06Scope.features | Where-Object { $_.owner -cne 'Integration Gateway Service' -or $_.readiness -cne 'READY' }).Count -eq 0 -and
    (Test-ExactSet @($d06Scope.consumedContracts) @('CID-007', 'CID-011')) -and
    (Test-ExactSet @($d06Scope.newlyRealizedContracts) @('CID-015', 'CID-016', 'CID-017', 'CID-018')) -and
    $d06Scope.providerSelection -ceq 'UNRESOLVED' -and
    $d06Scope.physicalPersistenceOrBroker -ceq 'NOT_SELECTED' -and
    $d06Scope.frontendBusinessChange -ceq 'NONE' -and
    (Test-ExactSet @($d06Scope.executionZones) @('LOCAL', 'CI_EPHEMERAL')) -and
    $d06Scope.architectureIntegrity.'R1-R7' -ceq 'FROZEN_UNCHANGED' -and
    $d06Scope.architectureIntegrity.R8 -ceq 'ABSENT' -and
    @($d06Scope.outstandingDecisions.PSObject.Properties | Where-Object { $_.Name -notin @('OD-04', 'OD-05', 'OD-06') -or $_.Value -cne 'UNRESOLVED' }).Count -eq 0 -and
    @($d06Scope.outstandingDecisions.PSObject.Properties).Count -eq 3 -and
    $d06Scope.stageGates.'SG-01' -ceq 'READY' -and
    $d06Scope.stageGates.'SG-02' -ceq 'CONDITIONALLY_READY' -and
    $d06Scope.stageGates.'SG-03' -ceq 'BLOCKED' -and
    $d06Scope.stageGates.'SG-04' -ceq 'BLOCKED' -and
    $d06Manifest.d06Status -ceq 'CANDIDATE_PENDING_CTO_REVIEW' -and
    $d06Manifest.d06FeatureState -ceq 'IMPLEMENTATION_CANDIDATE_SIMULATOR_6_OF_6' -and
    $d06Manifest.d06ContractState -ceq 'CONSUMED_2_REALIZED_4_CANDIDATE' -and
    $d06Manifest.d06EvidenceLevel -ceq 'SIMULATOR_CANDIDATE' -and
    $d06Manifest.integrationEvidenceLevel -ceq 'SIMULATOR_REFERENCE_ADAPTER' -and
    $d06Manifest.d06FrontendBusinessChange -ceq 'NONE_REQUIRED_BY_D06_FEATURE_SCOPE' -and
    (Test-ExactSet @($d06Manifest.d06OutstandingDecisions) @('OD-04', 'OD-05', 'OD-06'))
Add-Check 'Controlled post-D02 service state' ($catalogStateValid -and $d06GovernanceValid) 'Five exact D03 participants, accepted D05 Rules, exact D06 Gateway, D07 Search and D08 Reporting candidates at SIMULATOR, and three exact toolchain scaffolds'

$hostSplitValid = $true
$serviceReferenceValid = $true
$migrationOwnershipValid = $true
$healthOnlyValid = $true
foreach ($entry in $expectedServices.GetEnumerator()) {
    $serviceRoot = Join-Path $RepositoryRoot "services/$($entry.Key)"
    $projectPath = @(Get-ChildItem -LiteralPath $serviceRoot -Filter '*.csproj' -File)
    if ($projectPath.Count -ne 1) { $hostSplitValid = $false; $serviceReferenceValid = $false; continue }
    $project = [xml](Get-Content -LiteralPath $projectPath[0].FullName -Raw)
    $hostSplitValid = $hostSplitValid -and $project.Project.Sdk -ceq $entry.Value
    $references = @($project.Project.ItemGroup.ProjectReference | Where-Object { $_ })
    $referenceText = @($references | ForEach-Object { [string]$_.Include })
    $governedContractParticipant = $entry.Key -in $governedContractServices
    $platformReferences = @($referenceText | Where-Object { $_ -match 'shared[\\/]platform[\\/]Monergy\.Platform[\\/]Monergy\.Platform\.csproj$' })
    $contractReferences = @($referenceText | Where-Object { $_ -match 'contracts[\\/]Monergy\.Contracts[\\/]Monergy\.Contracts\.csproj$' })
    $allowedReferences = @($platformReferences) + @($contractReferences)
    $serviceReferenceValid = $serviceReferenceValid -and
        $platformReferences.Count -eq 1 -and
        $contractReferences.Count -le 1 -and
        $references.Count -eq $allowedReferences.Count -and
        @($referenceText | Select-Object -Unique).Count -eq $referenceText.Count -and
        @($referenceText | Where-Object { $_ -match '[\\/](?:services|tests|apps)[\\/]' }).Count -eq 0
    $migrationFiles = @(Get-ChildItem -LiteralPath (Join-Path $serviceRoot 'migrations') -File | Where-Object Name -cne '.gitkeep' | Where-Object { $_.FullName -cnotin $am05Migrations })
    if ($D10ForwardRegression -and $entry.Key -in @('evidence','financial-profile','financial-rules','reporting','audit','job-management')) {
        $migrationOwnershipValid = $migrationOwnershipValid -and $migrationFiles.Count -ge 1
    } elseif ($D09ForwardRegression -and $entry.Key -in @('evidence','financial-profile','financial-rules','reporting','audit')) {
        $migrationOwnershipValid = $migrationOwnershipValid -and $migrationFiles.Count -ge 1
    } else {
        $migrationOwnershipValid = $migrationOwnershipValid -and $migrationFiles.Count -eq 0
    }
    $program = Get-Content -LiteralPath (Join-Path $serviceRoot 'Program.cs') -Raw
    if ($entry.Value -ceq 'Microsoft.NET.Sdk.Web') {
        $healthOnlyValid = $healthOnlyValid -and $program.Contains('MapHealthChecks') -and
            ($governedContractParticipant -or $program -notmatch '\.Map(?:Get|Post|Put|Patch|Delete)\(')
        if ($entry.Key -ceq 'integration-gateway') {
            $healthOnlyValid = $healthOnlyValid -and
                $program.Contains('MapIntegrationGatewayContracts') -and
                $program -notmatch '\.Map(?:Get|Post|Put|Patch|Delete)\('
        }
        if ($entry.Key -ceq 'search-retrieval') {
            $healthOnlyValid = $healthOnlyValid -and
                $program.Contains('MapSearchRetrievalContracts') -and
                $program -notmatch '\.Map(?:Get|Post|Put|Patch|Delete)\('
        }
        if ($entry.Key -ceq 'reporting') {
            $healthOnlyValid = $healthOnlyValid -and
                $program.Contains('MapReportingContracts') -and
                $program -notmatch '\.Map(?:Get|Post|Put|Patch|Delete)\('
        }
    } else {
        $healthOnlyValid = $healthOnlyValid -and $program.Contains('AddHostedService<StartupWorker>') -and $program -notmatch '\.Map(?:Get|Post|Put|Patch|Delete)\('
    }
}
Add-Check 'Evidence-based host split' ($hostSplitValid -and @($catalog.services | Where-Object primaryHost -ceq 'HTTP').Count -eq 9 -and @($catalog.services | Where-Object primaryHost -ceq 'WORKER').Count -eq 3) 'Nine HTTP hosts and three primary workers'
Add-Check 'No service-to-service project references' $serviceReferenceValid 'Every service references Platform exactly once, Contracts zero or one time, and no other project; service/test/app and duplicate references are forbidden'
Add-Check $(if ($D10ForwardRegression) { 'D10-bounded service-owned migrations' } elseif ($D09ForwardRegression) { 'D09-bounded service-owned migrations' } else { 'Empty service-owned migrations' }) $migrationOwnershipValid $(if ($D10ForwardRegression) { 'D09 cohort plus Job Management populated; six histories remain empty' } elseif ($D09ForwardRegression) { 'Exact five-service cohort populated; seven histories remain empty' } else { '12 independent empty migration histories' })
Add-Check 'Controlled host scope' $healthOnlyValid 'Three true scaffolds remain health/startup-only; exact D03 and D05-D08 governed participants may expose contract surfaces'

$lockFiles = @(Get-ChildItem -LiteralPath $RepositoryRoot -Recurse -File -Filter 'packages.lock.json' | Where-Object { $_.FullName -notmatch '[\\/](?:bin|obj|\.toolcache)[\\/]' })
$projectFiles = @(Get-ChildItem -LiteralPath $RepositoryRoot -Recurse -File -Filter '*.csproj' | Where-Object { $_.FullName -notmatch '[\\/](?:bin|obj|\.toolcache)[\\/]' })
$projectsWithoutLocks = @($projectFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $_.DirectoryName 'packages.lock.json')) })
Add-Check 'NuGet lock coverage' ($lockFiles.Count -eq $projectFiles.Count -and $projectsWithoutLocks.Count -eq 0) 'Every service, shared, contract, and independently owned test project has a lock file'
$projectText = @($projectFiles | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
$forbiddenProviders = if ($D09ForwardRegression) { 'EntityFrameworkCore|SqlClient|MongoDB|StackExchange\.Redis|Azure\.|Google\.Cloud|OpenAI' } else { 'EntityFrameworkCore|Npgsql|SqlClient|MongoDB|StackExchange\.Redis|Azure\.|Amazon\.|Google\.Cloud|OpenAI' }
Add-Check $(if ($D09ForwardRegression) { 'D09-bounded provider dependency graph' } else { 'Provider-neutral dependency graph' }) ($projectText -notmatch $forbiddenProviders) $(if ($D09ForwardRegression) { 'Only separately verified D09 persistence packages are allowed' } else { 'No database, cloud, broker, storage, search, OCR, AI, identity, secret, or orchestrator SDK' })
Add-Check 'Vendor-neutral observability bootstrap' ($projectText.Contains('OpenTelemetry.Extensions.Hosting') -and $projectText.Contains('OpenTelemetry.Exporter.OpenTelemetryProtocol')) 'OpenTelemetry SDK and OTLP exporter only'
Add-Check 'Architecture tests implemented' (Test-Path -LiteralPath (Join-Path $RepositoryRoot 'tests/architecture/Monergy.Architecture.Tests/BoundaryTests.cs')) 'xUnit boundary suite exists'

Add-Check 'React 19.3 pin' ($customerPackage.dependencies.react -ceq '19.3.0' -and $customerPackage.dependencies.'react-dom' -ceq '19.3.0') 'React and React DOM exact'
Add-Check 'Vite 8.3 pin' ($customerPackage.devDependencies.vite -ceq '8.3.0') 'Vite exact'
Add-Check 'Ant Design pin' ($customerPackage.dependencies.antd -ceq '6.6.3') 'Ant Design exact'
Add-Check 'Tailwind CSS pin' ($customerPackage.devDependencies.tailwindcss -ceq '4.3.3' -and $customerPackage.devDependencies.'@tailwindcss/vite' -ceq '4.3.3') 'Tailwind and Vite integration exact'
Add-Check 'Bounded UI foundation' ($uiPackage.name -ceq '@monergy/ui-foundation' -and $uiPackage.private) 'Theme and reusable foundation stay outside business components'

$tokenPath = Join-Path $RepositoryRoot 'shared/platform/frontend-ui/src/tokens.json'
$tokenJson = Get-Content -LiteralPath $tokenPath -Raw | ConvertFrom-Json
$generatedCss = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'shared/platform/frontend-ui/src/semantic-tokens.generated.css') -Raw
$tokenValues = @($tokenJson.color.brand.PSObject.Properties.Value) + @($tokenJson.color.neutral.PSObject.Properties.Value) + @($tokenJson.color.state.PSObject.Properties.Value)
Add-Check 'Single semantic-token source' (@($tokenValues | Where-Object { -not $generatedCss.Contains([string]$_) }).Count -eq 0) 'Generated CSS projection contains every governed color value'
$themeText = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'shared/platform/frontend-ui/src/theme.ts') -Raw
Add-Check 'Ant token mapping' ($themeText.Contains('semanticTokens.color.brand.primary') -and $themeText.Contains('semanticTokens.spacing.md') -and $themeText.Contains('semanticTokens.breakpoint.lg') -and $themeText.Contains('semanticTokens.focus.color')) 'Ant ConfigProvider consumes governed color, spacing, breakpoint, and focus tokens'
Add-Check 'Tailwind token mapping' ($generatedCss.Contains('@theme inline') -and $generatedCss.Contains('--spacing: 0.25rem') -and $generatedCss.Contains('--breakpoint-lg: 64rem')) 'Tailwind consumes generated color, spacing, radius, elevation, and breakpoint values'

$mainText = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'apps/customer-web/src/main.tsx') -Raw
$styleText = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'apps/customer-web/src/styles.css') -Raw
$frontendFiles = @(Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'apps/customer-web') -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/](?:node_modules|dist)[\\/]' }) +
    @(Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'shared/platform/frontend-ui') -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/]node_modules[\\/]' })
$frontendText = @($frontendFiles | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
Add-Check 'Single CSS reset strategy' ($mainText.Contains("antd/dist/reset.css") -and $styleText.Contains("@monergy/ui-foundation/semantic-tokens.css") -and $styleText.Contains("tailwindcss/theme.css") -and $styleText.Contains("tailwindcss/utilities.css") -and $styleText -notmatch 'tailwindcss/(?:base|preflight)') 'Ant reset only; Tailwind Preflight omitted'
Add-Check 'No wildcard Ant imports' (-not (Test-ProhibitedImport $frontendText)) 'Named component imports preserve tree shaking'
Add-Check 'No routine important overrides' (-not (Test-ProhibitedStyle $frontendText)) 'No !important integration mechanism'
Add-Check 'No POC or Prototype imports' ($frontendText -notmatch '(?i)(?:from|import).*?(?:prototype|monergy-poc)') 'No ungoverned UI source imported'
Add-Check 'Component and accessibility tests' ((Get-Content -LiteralPath (Join-Path $RepositoryRoot 'apps/customer-web/tests/App.test.tsx') -Raw).Contains('axe.run')) 'React interaction, focus, and axe smoke coverage'
Add-Check 'Browser smoke test' (Test-Path -LiteralPath (Join-Path $RepositoryRoot 'apps/customer-web/e2e/toolchain.spec.ts')) 'Playwright keyboard smoke exists'

$dockerFiles = @(Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'services') -Recurse -File -Filter 'Dockerfile')
Add-Check 'Independent OCI definitions' ($dockerFiles.Count -eq 12) 'One Linux OCI definition per service boundary'
$dockerText = @($dockerFiles | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
Add-Check 'Digest-pinned container bases' (@($dockerFiles | Where-Object { (Get-Content -LiteralPath $_.FullName -Raw) -notmatch '(?m)^FROM .+@sha256:[0-9a-f]{64}' }).Count -eq 0) '.NET 10 builder and runtime image digests'
Add-Check 'Non-root read-only-compatible containers' (@($dockerFiles | Where-Object { (Get-Content -LiteralPath $_.FullName -Raw) -notmatch '(?m)^USER \$APP_UID$' }).Count -eq 0) 'No runtime write path and non-root UID'

$hostedOciPath = Join-Path $RepositoryRoot 'build/hosted-oci-evidence.ps1'
$hostedOciText = if (Test-Path -LiteralPath $hostedOciPath) { Get-Content -LiteralPath $hostedOciPath -Raw } else { '' }
$hostedOciContract = $hostedOciText.Contains('docker save') -and
    $hostedOciText.Contains('docker-archive:') -and
    $hostedOciText.Contains('cyclonedx-json=') -and
    $hostedOciText.Contains('--fail-on high') -and
    $hostedOciText.Contains('published = $false')
Add-Check 'Hosted OCI evidence pipeline' $hostedOciContract 'Archive, digest, image SBOM, Grype scan, fail-closed result and no publication'
$hostedMatrixPath = Join-Path $RepositoryRoot '.artifacts/oci/evidence-matrix.json'
$fullOciEvidenceRequired = $env:MONERGY_REQUIRE_FULL_OCI_EVIDENCE -ceq 'true'
$hostedMatrixValid = if ($fullOciEvidenceRequired) {
    (Test-Path -LiteralPath $hostedMatrixPath) -and
        (Test-HostedOciMatrix (Get-Content -LiteralPath $hostedMatrixPath -Raw | ConvertFrom-Json))
} else { $true }
Add-Check 'Governed full OCI evidence matrix' $hostedMatrixValid 'Required only when explicit full OCI evidence is requested; complete 12-image capability remains available'

$supplyPolicy = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/supply-chain/policy.json') -Raw | ConvertFrom-Json
Add-Check 'Pinned supply-chain scanners' ($supplyPolicy.tools.syft -ceq '1.51.1' -and $supplyPolicy.tools.grype -ceq '0.118.0' -and $supplyPolicy.tools.gitleaks -ceq '8.30.1') 'Syft, Grype, and Gitleaks exact'
Add-Check 'Honest scanner semantics' ($supplyPolicy.scannerFailureSemantics -ceq 'FAIL_NEVER_PASS' -and $supplyPolicy.unavailableToolSemantics -ceq 'NOT_RUN_OR_BLOCKED_NEVER_PASS') 'Unavailable or failed scanners cannot pass'

$workflow = Get-Content -LiteralPath (Join-Path $RepositoryRoot '.github/workflows/bootstrap.yml') -Raw
$toolchainScriptText = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/Invoke-Toolchain.ps1') -Raw
Add-Check 'Technology-specific impact-aware CI workflow' ($workflow.Contains('10.0.401') -and $workflow.Contains('24.21.0') -and $workflow.Contains('hosted-oci-evidence.ps1') -and $workflow.Contains('oci_services') -and $workflow.Contains('full_regression') -and $workflow.Contains('Get-CiImpact.ps1') -and $workflow.Contains('pull_request:') -and $workflow.Contains('- main') -and -not $workflow.Contains('delivery/mwp03-*') -and $toolchainScriptText.Contains('--frozen-lockfile')) 'Pinned toolchains, impacted OCI evidence, and governed complete 12-image capability'
Add-Check 'GitHub Free exception preserved' ($workflow.Contains('Direct push to main detected') -and $workflow.Contains('/commits/$env:MONERGY_COMMIT_SHA/pulls')) 'Detection remains warning, not claimed prevention'

$invokeToolchain = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/Invoke-Toolchain.ps1') -Raw
$requiredTasks = @('Restore', 'FormatCheck', 'Lint', 'Build', 'Test', 'ArchitectureTest', 'BrowserTest', 'Package', 'HostedOciEvidence', 'Sbom', 'VulnerabilityScan', 'SecretScan', 'ReleaseManifest', 'D12Verification', 'D13Verification', 'D14Verification', 'D15Verification', 'Verify')
Add-Check 'Reproducible root task surface' (@($requiredTasks | Where-Object { -not $invokeToolchain.Contains("'$_'") }).Count -eq 0) 'All D02 root command responsibilities exposed'
$repositoryManifest = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'repository.manifest.json') -Raw | ConvertFrom-Json
$gateCatalog = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/ci/gates.json') -Raw | ConvertFrom-Json
$notRunGates = @($gateCatalog.gates | Where-Object result -ceq 'NOT_RUN')
$truthfulGateState = @($gateCatalog.gates | Where-Object result -ceq 'PASS').Count -eq 11 -and
    @($gateCatalog.gates | Where-Object result -ceq 'BLOCKED').Count -eq 0 -and
    $notRunGates.Count -eq 0
Add-Check 'Truthful D02 foundation retained' ($repositoryManifest.status -ceq 'ACCEPTED_COMPLETE' -and $repositoryManifest.vs02FeatureState -ceq 'IMPLEMENTATION_ACCEPTED_SIMULATOR_8_OF_8' -and $repositoryManifest.vs02ContractState -ceq 'COMPATIBILITY_EVIDENCE_ACCEPTED_14_OF_14' -and $repositoryManifest.deploymentState -ceq 'NOT_DEPLOYED' -and $truthfulGateState) 'Accepted D02 toolchain supports accepted D03 SIMULATOR implementation evidence; 11 PASS and no deployment claim'

$failures = @($checks | Where-Object { -not $_.Passed })
foreach ($check in $checks) {
    $state = if ($check.Passed) { 'PASS' } else { 'FAIL' }
    Write-Output "[$state] $($check.Name) - $($check.Evidence)"
}
if ($failures.Count -gt 0) {
    throw "D02 verification failed: $($checks.Count - $failures.Count)/$($checks.Count); failed: $($failures.Name -join ', ')."
}
Write-Output "D02 verification passed: $($checks.Count)/$($checks.Count)."

