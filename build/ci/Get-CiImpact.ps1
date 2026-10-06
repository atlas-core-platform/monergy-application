[CmdletBinding(DefaultParameterSetName = 'GitDiff')]
param(
    [Parameter(ParameterSetName = 'Paths')]
    [AllowEmptyCollection()]
    [string[]]$ChangedPath,
    [Parameter(ParameterSetName = 'GitDiff')]
    [string]$BaseSha,
    [Parameter(ParameterSetName = 'GitDiff')]
    [string]$HeadSha = 'HEAD',
    [ValidateSet('PullRequest', 'MergeIntegrity', 'FullRegression')]
    [string]$Mode = 'PullRequest',
    [string]$MapPath,
    [string]$OutputPath,
    [switch]$EmitGitHubOutputs,
    [string]$RepositoryRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($MapPath)) {
    $MapPath = Join-Path $RepositoryRoot 'build/governance/ci-impact-map.json'
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $RepositoryRoot '.artifacts/ci/impact.json'
}

function Test-Glob([string]$Path, [string]$Pattern) {
    $normalizedPath = $Path.Replace('\', '/')
    $normalizedPattern = $Pattern.Replace('\', '/')
    $regex = [regex]::Escape($normalizedPattern)
    $regex = $regex.Replace('\*\*', '.*').Replace('\*', '[^/]*').Replace('\?', '[^/]')
    return $normalizedPath -match "^$regex$"
}

function Test-AnyPattern([string]$Path, [object[]]$Patterns) {
    foreach ($pattern in @($Patterns)) {
        if (Test-Glob $Path ([string]$pattern)) { return $true }
    }
    return $false
}

function Add-Unique([System.Collections.Generic.HashSet[string]]$Set, [object[]]$Values) {
    foreach ($value in @($Values)) {
        if (-not [string]::IsNullOrWhiteSpace([string]$value)) { $null = $Set.Add([string]$value) }
    }
}

function Get-PropertyValues([object]$Value, [string]$Name) {
    $property = $Value.PSObject.Properties[$Name]
    if ($null -eq $property) { return @() }
    return @($property.Value)
}

function Get-Sorted([System.Collections.Generic.HashSet[string]]$Set) {
    [string[]]$values = @($Set)
    [Array]::Sort($values, [StringComparer]::Ordinal)
    return $values
}

if (-not (Test-Path -LiteralPath $MapPath)) { throw "CI impact map '$MapPath' does not exist." }
$map = Get-Content -LiteralPath $MapPath -Raw -Encoding utf8 | ConvertFrom-Json
if ($map.schemaVersion -cne '1.0.0' -or $map.unmappedProductionPolicy -cne 'FULL_REGRESSION') {
    throw 'Unsupported or fail-open CI impact map.'
}

$paths = if ($PSCmdlet.ParameterSetName -ceq 'Paths') {
    @($ChangedPath)
} else {
    if ([string]::IsNullOrWhiteSpace($BaseSha)) { throw 'BaseSha is required when changed paths are not supplied.' }
    $range = "$BaseSha...$HeadSha"
    @(& git -C $RepositoryRoot diff --name-only --diff-filter=ACDMRTUXB $range --)
}

$normalizedPaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($path in @($paths)) {
    if ([string]::IsNullOrWhiteSpace([string]$path)) { continue }
    $normalized = ([string]$path).Replace('\', '/').Trim()
    if ([IO.Path]::IsPathRooted($normalized) -or $normalized -match '(^|/)\.\.(/|$)' -or $normalized.StartsWith('/')) {
        throw "Changed path '$path' is not a safe repository-relative path."
    }
    $null = $normalizedPaths.Add($normalized)
}
$paths = @(Get-Sorted $normalizedPaths)

$serviceIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$testProjects = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$verifiers = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$ociServices = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$flags = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$reasons = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$unmapped = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$serviceById = @{}
foreach ($service in @($map.services)) { $serviceById[[string]$service.id] = $service }
$allServiceIds = @($map.services | ForEach-Object { [string]$_.id })
$allVerifiers = @($map.fullRegression.verifiers | ForEach-Object { [string]$_ })

function Add-Service([string]$Id, [string]$Reason) {
    if (-not $serviceById.ContainsKey($Id)) { throw "Impact map references unknown service '$Id'." }
    if ($serviceIds.Add($Id)) {
        $service = $serviceById[$Id]
        Add-Unique $testProjects @(Get-PropertyValues $service 'testProjects')
        Add-Unique $verifiers @(Get-PropertyValues $service 'verifiers')
        Add-Unique $flags @(Get-PropertyValues $service 'flags')
    }
    if ($Reason) { $null = $reasons.Add($Reason) }
}

$documentationOnly = $paths.Count -gt 0 -and @($paths | Where-Object {
    -not (Test-AnyPattern $_ @($map.documentationPatterns))
}).Count -eq 0
$fullRegression = $Mode -ceq 'FullRegression'

if (-not $documentationOnly -and -not $fullRegression) {
    foreach ($path in $paths) {
        if (Test-AnyPattern $path @($map.documentationPatterns)) {
            $null = $reasons.Add("documentation:$path")
            continue
        }
        $mapped = $false
        $matchedServiceIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)

        foreach ($service in @($map.services)) {
            if ((Test-AnyPattern $path @($service.sourcePatterns)) -or
                (Test-AnyPattern $path @($service.testPatterns))) {
                Add-Service ([string]$service.id) "service:$($service.id):$path"
                $null = $matchedServiceIds.Add([string]$service.id)
                $mapped = $true
            }
        }

        foreach ($rule in @($map.rules)) {
            if (-not (Test-AnyPattern $path @($rule.patterns))) { continue }
            $mapped = $true
            $null = $reasons.Add("rule:$($rule.id):$path")
            Add-Unique $testProjects @(Get-PropertyValues $rule 'testProjects')
            Add-Unique $verifiers @(Get-PropertyValues $rule 'verifiers')
            Add-Unique $flags @(Get-PropertyValues $rule 'flags')
            foreach ($serviceId in @(Get-PropertyValues $rule 'services')) { Add-Service ([string]$serviceId) "rule:$($rule.id):$path" }
            if ($rule.PSObject.Properties['allServices'] -and $rule.allServices) {
                foreach ($serviceId in $allServiceIds) { Add-Service $serviceId "rule:$($rule.id):$path" }
            }
            if ($rule.PSObject.Properties['allVerifiers'] -and $rule.allVerifiers) {
                Add-Unique $verifiers $allVerifiers
            }
            if ($rule.PSObject.Properties['allOci'] -and $rule.allOci) {
                Add-Unique $ociServices $allServiceIds
            }
            if (@(Get-PropertyValues $rule 'flags') -contains 'affectedServiceOci') {
                Add-Unique $ociServices @($matchedServiceIds)
            }
        }

        if (-not $mapped) {
            $null = $unmapped.Add($path)
        }
    }

    if ($unmapped.Count -gt 0) {
        $fullRegression = $true
        $null = $reasons.Add('fail-safe:unmapped-path')
    }
}

if ($fullRegression) {
    Add-Unique $serviceIds $allServiceIds
    Add-Unique $testProjects @($map.fullRegression.testProjects)
    Add-Unique $verifiers @($map.fullRegression.verifiers)
    Add-Unique $ociServices $allServiceIds
    Add-Unique $flags @($map.fullRegression.flags)
    $null = $reasons.Add($(if ($Mode -ceq 'FullRegression') { 'mode:full-regression' } else { 'fail-safe:full-regression' }))
}

$selectedServices = @(Get-Sorted $serviceIds)
$selectedTests = @(Get-Sorted $testProjects)
$selectedVerifiers = @(Get-Sorted $verifiers)
$selectedOci = @(Get-Sorted $ociServices)
$selectedFlags = @(Get-Sorted $flags)
$effectiveMode = if ($fullRegression) { 'FullRegression' } else { $Mode }
$result = [ordered]@{
    schemaVersion = '1.0.0'
    requestedMode = $Mode
    effectiveMode = $effectiveMode
    fullRegression = $fullRegression
    documentationOnly = $documentationOnly
    changedPaths = @($paths)
    unmappedPaths = @(Get-Sorted $unmapped)
    services = $selectedServices
    testProjects = $selectedTests
    verifiers = $selectedVerifiers
    ociServices = $selectedOci
    flags = $selectedFlags
    gates = [ordered]@{
        universal = $true
        serviceTests = $selectedTests.Count -gt 0
        frontendTests = $selectedFlags -contains 'frontendTests'
        browser = $selectedFlags -contains 'browser'
        persistence = $selectedFlags -contains 'persistence'
        jobAudit = $selectedFlags -contains 'jobAudit'
        d11 = $selectedFlags -contains 'd11'
        oci = $selectedOci.Count -gt 0
        dependencySecurity = $selectedFlags -contains 'dependencySecurity'
        impactPolicy = $true
        directPushDetection = $Mode -ceq 'MergeIntegrity'
    }
    reasons = @(Get-Sorted $reasons)
}

$parent = Split-Path -Parent $OutputPath
if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
[IO.File]::WriteAllText($OutputPath, ($result | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))

if ($EmitGitHubOutputs) {
    if ([string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) { throw 'GITHUB_OUTPUT is required with EmitGitHubOutputs.' }
    $outputs = [ordered]@{
        effective_mode = $effectiveMode
        full_regression = $fullRegression.ToString().ToLowerInvariant()
        documentation_only = $documentationOnly.ToString().ToLowerInvariant()
        run_service_tests = $result.gates.serviceTests.ToString().ToLowerInvariant()
        run_frontend_tests = $result.gates.frontendTests.ToString().ToLowerInvariant()
        run_browser = $result.gates.browser.ToString().ToLowerInvariant()
        run_persistence = $result.gates.persistence.ToString().ToLowerInvariant()
        run_job_audit = $result.gates.jobAudit.ToString().ToLowerInvariant()
        run_d11 = $result.gates.d11.ToString().ToLowerInvariant()
        run_oci = $result.gates.oci.ToString().ToLowerInvariant()
        run_dependency_security = $result.gates.dependencySecurity.ToString().ToLowerInvariant()
        oci_services = (ConvertTo-Json -InputObject @($selectedOci) -Compress)
    }
    foreach ($entry in $outputs.GetEnumerator()) {
        Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "$($entry.Key)=$($entry.Value)"
    }
}

Write-Output "CI impact: mode=$effectiveMode; paths=$($paths.Count); services=$($selectedServices.Count); tests=$($selectedTests.Count); verifiers=$($selectedVerifiers.Count); OCI=$($selectedOci.Count); unmapped=$($unmapped.Count)."
Write-Output $OutputPath
