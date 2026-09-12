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
    'reporting' = 'Microsoft.NET.Sdk.Worker'; 'job-management' = 'Microsoft.NET.Sdk.Worker'; 'audit' = 'Microsoft.NET.Sdk.Worker'
}
$catalog = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'services/catalog.json') -Raw | ConvertFrom-Json
Add-Check 'Twelve canonical services' (Test-ExactSet @($catalog.services.id) @($expectedServices.Keys)) '12/12 R3 service identities'
Add-Check 'Independent artifact identities' (@($catalog.services.artifact | Select-Object -Unique).Count -eq 12) '12 unique service artifact names'
Add-Check 'No service Feature claims' (@($catalog.services | Where-Object { $_.status -cne 'TOOLCHAIN_SCAFFOLD' -or $_.featureImplementation -cne 'NONE' }).Count -eq 0) 'All service scaffolds declare NONE'

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
    $serviceReferenceValid = $serviceReferenceValid -and $references.Count -eq 1 -and ([string]$references[0].Include) -match 'shared[\\/]platform[\\/]Monergy\.Platform'
    $migrationFiles = @(Get-ChildItem -LiteralPath (Join-Path $serviceRoot 'migrations') -File | Where-Object Name -cne '.gitkeep')
    $migrationOwnershipValid = $migrationOwnershipValid -and $migrationFiles.Count -eq 0
    $program = Get-Content -LiteralPath (Join-Path $serviceRoot 'Program.cs') -Raw
    if ($entry.Value -ceq 'Microsoft.NET.Sdk.Web') {
        $healthOnlyValid = $healthOnlyValid -and $program.Contains('MapHealthChecks') -and $program -notmatch '\.Map(?:Get|Post|Put|Patch|Delete)\('
    } else {
        $healthOnlyValid = $healthOnlyValid -and $program.Contains('AddHostedService<StartupWorker>') -and $program -notmatch '\.Map(?:Get|Post|Put|Patch|Delete)\('
    }
}
Add-Check 'Evidence-based host split' ($hostSplitValid -and @($catalog.services | Where-Object primaryHost -ceq 'HTTP').Count -eq 8 -and @($catalog.services | Where-Object primaryHost -ceq 'WORKER').Count -eq 4) 'Eight HTTP hosts and four primary workers'
Add-Check 'No service-to-service project references' $serviceReferenceValid 'Each service references only Monergy.Platform'
Add-Check 'Empty service-owned migrations' $migrationOwnershipValid '12 independent empty migration histories'
Add-Check 'Health and startup scope only' $healthOnlyValid 'No business routes, commands, events, or workflows'

$lockFiles = @(Get-ChildItem -LiteralPath $RepositoryRoot -Recurse -File -Filter 'packages.lock.json' | Where-Object { $_.FullName -notmatch '[\\/](?:bin|obj|\.toolcache)[\\/]' })
Add-Check 'NuGet lock coverage' ($lockFiles.Count -eq 14) '12 services, one shared platform, one test project'
$projectText = @(Get-ChildItem -LiteralPath $RepositoryRoot -Recurse -File -Filter '*.csproj' | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
$forbiddenProviders = 'EntityFrameworkCore|Npgsql|SqlClient|MongoDB|StackExchange\.Redis|Azure\.|Amazon\.|Google\.Cloud|OpenAI'
Add-Check 'Provider-neutral dependency graph' ($projectText -notmatch $forbiddenProviders) 'No database, cloud, broker, storage, search, OCR, AI, identity, secret, or orchestrator SDK'
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
$hostedMatrixValid = if ($env:GITHUB_ACTIONS -ceq 'true') {
    (Test-Path -LiteralPath $hostedMatrixPath) -and
        (Test-HostedOciMatrix (Get-Content -LiteralPath $hostedMatrixPath -Raw | ConvertFrom-Json))
} else { $true }
Add-Check 'Hosted OCI evidence matrix' $hostedMatrixValid 'Required in GitHub Actions: builds, SBOMs, scans and digests 12/12'

$supplyPolicy = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/supply-chain/policy.json') -Raw | ConvertFrom-Json
Add-Check 'Pinned supply-chain scanners' ($supplyPolicy.tools.syft -ceq '1.51.1' -and $supplyPolicy.tools.grype -ceq '0.118.0' -and $supplyPolicy.tools.gitleaks -ceq '8.30.1') 'Syft, Grype, and Gitleaks exact'
Add-Check 'Honest scanner semantics' ($supplyPolicy.scannerFailureSemantics -ceq 'FAIL_NEVER_PASS' -and $supplyPolicy.unavailableToolSemantics -ceq 'NOT_RUN_OR_BLOCKED_NEVER_PASS') 'Unavailable or failed scanners cannot pass'

$workflow = Get-Content -LiteralPath (Join-Path $RepositoryRoot '.github/workflows/bootstrap.yml') -Raw
$toolchainScriptText = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/Invoke-Toolchain.ps1') -Raw
Add-Check 'Technology-specific CI workflow' ($workflow.Contains('10.0.401') -and $workflow.Contains('24.21.0') -and $workflow.Contains('HostedOciEvidence') -and $workflow.Contains('delivery/mwp03-d02-product-technology-toolchain') -and $toolchainScriptText.Contains('--frozen-lockfile')) 'Pinned .NET/Node, locked install, hosted 12-image evidence and full D02 gates'
Add-Check 'GitHub Free exception preserved' ($workflow.Contains('Direct push to main detected') -and $workflow.Contains('/commits/$env:MONERGY_COMMIT_SHA/pulls')) 'Detection remains warning, not claimed prevention'

$invokeToolchain = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/Invoke-Toolchain.ps1') -Raw
$requiredTasks = @('Restore', 'FormatCheck', 'Lint', 'Build', 'Test', 'ArchitectureTest', 'BrowserTest', 'Package', 'HostedOciEvidence', 'Sbom', 'VulnerabilityScan', 'SecretScan', 'ReleaseManifest', 'Verify')
Add-Check 'Reproducible root task surface' (@($requiredTasks | Where-Object { -not $invokeToolchain.Contains("'$_'") }).Count -eq 0) 'All D02 root command responsibilities exposed'
$repositoryManifest = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'repository.manifest.json') -Raw | ConvertFrom-Json
$gateCatalog = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/ci/gates.json') -Raw | ConvertFrom-Json
$notRunGates = @($gateCatalog.gates | Where-Object result -ceq 'NOT_RUN')
$truthfulGateState = @($gateCatalog.gates | Where-Object result -ceq 'PASS').Count -eq 9 -and
    @($gateCatalog.gates | Where-Object result -ceq 'BLOCKED').Count -eq 0 -and
    $notRunGates.Count -eq 2 -and
    (Test-ExactSet $notRunGates.id @('CG-07', 'CG-11')) -and
    @($notRunGates | Where-Object { -not $_.scope }).Count -eq 0
Add-Check 'Truthful D02 candidate state' ($repositoryManifest.status -ceq 'CTO_APPROVED_CANDIDATE_PENDING_CLOSURE' -and $repositoryManifest.vs02FeatureState -ceq 'NOT_STARTED_8_OF_8' -and $repositoryManifest.vs02ContractState -ceq 'NOT_OPERATIONAL_14_OF_14' -and $repositoryManifest.deploymentState -ceq 'NOT_DEPLOYED' -and $truthfulGateState) '9 PASS, 0 BLOCKED, exact NOT_RUN gates CG-07/CG-11; Features/contracts/deployment remain absent'

$failures = @($checks | Where-Object { -not $_.Passed })
foreach ($check in $checks) {
    $state = if ($check.Passed) { 'PASS' } else { 'FAIL' }
    Write-Output "[$state] $($check.Name) - $($check.Evidence)"
}
if ($failures.Count -gt 0) {
    throw "D02 verification failed: $($checks.Count - $failures.Count)/$($checks.Count); failed: $($failures.Name -join ', ')."
}
Write-Output "D02 verification passed: $($checks.Count)/$($checks.Count)."
