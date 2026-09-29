[CmdletBinding()]
param(
    [ValidateSet('Restore', 'FormatCheck', 'Lint', 'Build', 'Test', 'ArchitectureTest', 'BrowserTest', 'Package', 'HostedOciEvidence', 'Sbom', 'VulnerabilityScan', 'SecretScan', 'ReleaseManifest', 'D01Verification', 'D02Verification', 'D03Verification', 'D04Verification', 'D05Verification', 'D05Regression', 'D06Verification', 'D07Verification', 'D08Verification', 'D09Verification', 'Verify')]
    [string]$Task = 'Verify',
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$localDotnet = Join-Path $RepositoryRoot '.toolcache/dotnet/dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
if (Test-Path -LiteralPath $localDotnet) { $env:PATH = "$(Split-Path -Parent $localDotnet);$env:PATH" }
$localNodeRoot = Join-Path $RepositoryRoot '.toolcache/node-v24.21.0-win-x64'
$localCorepackShims = $null
if (Test-Path -LiteralPath (Join-Path $localNodeRoot 'node.exe')) {
    $localCorepackShims = Join-Path $localNodeRoot 'node_modules/corepack/shims'
    $env:PATH = if (Test-Path -LiteralPath $localCorepackShims) {
        "$localNodeRoot;$localCorepackShims;$env:PATH"
    } else {
        "$localNodeRoot;$env:PATH"
    }
}
$node = (Get-Command node -ErrorAction Stop).Source
$localPnpmCommand = if ($null -ne $localCorepackShims) { Join-Path $localCorepackShims 'pnpm.cmd' } else { $null }
$pnpm = if ($null -ne $localPnpmCommand -and (Test-Path -LiteralPath $localPnpmCommand)) {
    $localPnpmCommand
} else {
    (Get-Command pnpm -ErrorAction Stop).Source
}

function Assert-Version {
    param([string]$Name, [string]$Actual, [string]$Expected)
    if ($Actual.TrimStart('v') -cne $Expected) { throw "$Name $Expected is required; found $Actual." }
}

Assert-Version '.NET SDK' (& $dotnet --version) '10.0.401'
Assert-Version 'Node.js' (& $node --version) '24.21.0'
Assert-Version 'pnpm' (& $pnpm --version) '12.4.1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$d09ForwardArguments = @{}
if ($Task -ceq 'Verify' -and (Test-Path -LiteralPath (Join-Path $RepositoryRoot 'build/governance/d09-scope-lock.json'))) {
    $d09ForwardArguments.D09ForwardRegression = $true
}

function Invoke-Checked {
    param([scriptblock]$Operation, [string]$Failure)
    & $Operation
    if ($LASTEXITCODE -ne 0) { throw $Failure }
}

function Invoke-Task {
    param([string]$Name)
    Write-Output "=== D02 $Name ==="
    switch ($Name) {
        'Restore' {
            Invoke-Checked { & $dotnet restore (Join-Path $RepositoryRoot 'Monergy.Application.slnx') --locked-mode } 'Locked NuGet restore failed.'
            Invoke-Checked { & $pnpm install --dir $RepositoryRoot --frozen-lockfile } 'Frozen pnpm install failed.'
        }
        'FormatCheck' {
            Invoke-Checked { & $dotnet format (Join-Path $RepositoryRoot 'Monergy.Application.slnx') --verify-no-changes --no-restore } '.NET format verification failed.'
            Invoke-Checked { & $pnpm --dir $RepositoryRoot run format:check } 'Prettier verification failed.'
            Invoke-Checked { & git -C $RepositoryRoot diff --check } 'Git whitespace verification failed.'
        }
        'Lint' {
            Invoke-Checked { & $pnpm --dir $RepositoryRoot run lint } 'ESLint security/static analysis failed.'
            Invoke-Checked { & $pnpm --dir $RepositoryRoot run typecheck } 'TypeScript strict no-emit check failed.'
        }
        'Build' {
            Invoke-Checked { & $dotnet build (Join-Path $RepositoryRoot 'Monergy.Application.slnx') --configuration Release --no-restore } '.NET build failed.'
            Invoke-Checked { & $pnpm --dir $RepositoryRoot run build } 'Frontend production build failed.'
        }
        'Test' {
            Invoke-Checked { & $dotnet test (Join-Path $RepositoryRoot 'Monergy.Application.slnx') --configuration Release --no-build --filter 'FullyQualifiedName!~Monergy.Persistence.Tests' --logger 'trx' --results-directory (Join-Path $RepositoryRoot '.artifacts/tests') } '.NET test suite failed.'
            Invoke-Checked { & $dotnet test (Join-Path $RepositoryRoot 'tests/financial-rules/Monergy.FinancialRules.Tests/Monergy.FinancialRules.Tests.csproj') --configuration Release --no-build --logger 'trx;LogFileName=Monergy.FinancialRules.Tests.trx' --results-directory (Join-Path $RepositoryRoot '.artifacts/tests') } 'D05 named behavioral evidence failed.'
            Invoke-Checked { & $pnpm --dir $RepositoryRoot run test } 'Frontend component/accessibility tests failed.'
        }
        'ArchitectureTest' {
            Invoke-Checked { & $dotnet test (Join-Path $RepositoryRoot 'tests/architecture/Monergy.Architecture.Tests/Monergy.Architecture.Tests.csproj') --configuration Release --no-build } 'Architecture-boundary tests failed.'
        }
        'BrowserTest' {
            Invoke-Checked { & $pnpm --dir $RepositoryRoot run test:browser } 'Playwright smoke test failed.'
        }
        'Package' {
            & (Join-Path $RepositoryRoot 'build/package-services.ps1') -RepositoryRoot $RepositoryRoot
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
        'HostedOciEvidence' {
            & (Join-Path $RepositoryRoot 'build/hosted-oci-evidence.ps1') -RepositoryRoot $RepositoryRoot
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
        'Sbom' {
            & (Join-Path $RepositoryRoot 'build/supply-chain/Invoke-SupplyChain.ps1') -Task Sbom -RepositoryRoot $RepositoryRoot
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
        'VulnerabilityScan' {
            & (Join-Path $RepositoryRoot 'build/supply-chain/Invoke-SupplyChain.ps1') -Task VulnerabilityScan -RepositoryRoot $RepositoryRoot
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
        'SecretScan' {
            & (Join-Path $RepositoryRoot 'build/supply-chain/Invoke-SupplyChain.ps1') -Task SecretScan -RepositoryRoot $RepositoryRoot
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
        'ReleaseManifest' {
            & (Join-Path $RepositoryRoot 'build/release/New-CandidateManifest.ps1') -RepositoryRoot $RepositoryRoot
            & (Join-Path $RepositoryRoot 'build/release/verify-candidate-manifest.ps1') -RepositoryRoot $RepositoryRoot
        }
        'D01Verification' {
            & (Join-Path $RepositoryRoot 'build/verify-bootstrap.ps1') -RepositoryRoot $RepositoryRoot @d09ForwardArguments
        }
        'D02Verification' {
            & (Join-Path $RepositoryRoot 'build/verify-toolchain.ps1') -RepositoryRoot $RepositoryRoot -SelfTest @d09ForwardArguments
            & (Join-Path $RepositoryRoot 'build/verify-toolchain.ps1') -RepositoryRoot $RepositoryRoot @d09ForwardArguments
        }
        'D03Verification' {
            & (Join-Path $RepositoryRoot 'build/verify-vs02.ps1') -RepositoryRoot $RepositoryRoot -SelfTest @d09ForwardArguments
            & (Join-Path $RepositoryRoot 'build/verify-vs02.ps1') -RepositoryRoot $RepositoryRoot @d09ForwardArguments
        }
        'D04Verification' {
            & (Join-Path $RepositoryRoot 'build/verify-financial-profile-authority.ps1') -RepositoryRoot $RepositoryRoot -SelfTest @d09ForwardArguments
            & (Join-Path $RepositoryRoot 'build/verify-financial-profile-authority.ps1') -RepositoryRoot $RepositoryRoot @d09ForwardArguments
        }
        'D05Verification' {
            & (Join-Path $RepositoryRoot 'build/verify-financial-rules.ps1') -RepositoryRoot $RepositoryRoot -SelfTest
            & (Join-Path $RepositoryRoot 'build/verify-financial-rules.ps1') -RepositoryRoot $RepositoryRoot -RequireBehavior
        }
        'D05Regression' {
            & (Join-Path $RepositoryRoot 'build/verify-financial-rules.ps1') -RepositoryRoot $RepositoryRoot -RegressionOnly -SelfTest @d09ForwardArguments
            & (Join-Path $RepositoryRoot 'build/verify-financial-rules.ps1') -RepositoryRoot $RepositoryRoot -RegressionOnly -RequireBehavior @d09ForwardArguments
        }
        'D06Verification' {
            & (Join-Path $RepositoryRoot 'build/verify-integration-gateway-core.ps1') -RepositoryRoot $RepositoryRoot -RegressionOnly -SelfTest
            & (Join-Path $RepositoryRoot 'build/verify-integration-gateway-core.ps1') -RepositoryRoot $RepositoryRoot -RegressionOnly
        }
        'D07Verification' {
            & (Join-Path $RepositoryRoot 'build/verify-authorized-search.ps1') -RepositoryRoot $RepositoryRoot -SelfTest
            & (Join-Path $RepositoryRoot 'build/verify-authorized-search.ps1') -RepositoryRoot $RepositoryRoot
        }
        'D08Verification' {
            & (Join-Path $RepositoryRoot 'build/verify-trusted-reporting.ps1') -RepositoryRoot $RepositoryRoot -SelfTest @d09ForwardArguments
            & (Join-Path $RepositoryRoot 'build/verify-trusted-reporting.ps1') -RepositoryRoot $RepositoryRoot @d09ForwardArguments
        }
        'D09Verification' {
            & (Join-Path $RepositoryRoot 'build/verify-persistent-data-foundation.ps1') -RepositoryRoot $RepositoryRoot -SelfTest
            & (Join-Path $RepositoryRoot 'build/verify-persistent-data-foundation.ps1') -RepositoryRoot $RepositoryRoot
        }
    }
}

$taskOrder = if ($Task -ceq 'Verify') {
    @('Restore', 'FormatCheck', 'Lint', 'Build', 'Test', 'ArchitectureTest', 'BrowserTest', 'Package', 'Sbom', 'VulnerabilityScan', 'SecretScan', 'ReleaseManifest', 'D01Verification', 'D02Verification', 'D03Verification', 'D04Verification', 'D05Regression', 'D06Verification', 'D07Verification', 'D08Verification', 'D09Verification')
} else { @($Task) }

foreach ($current in $taskOrder) { Invoke-Task $current }
Write-Output "Monergy controlled-implementation task '$Task' completed."
