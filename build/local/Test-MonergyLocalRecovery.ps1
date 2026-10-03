[CmdletBinding()]
param([string]$RepositoryRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
}
Import-Module (Join-Path $RepositoryRoot 'build/local/Monergy.LocalProcess.psm1') -Force

function Assert-Equal([object]$Expected, [object]$Actual, [string]$Evidence) {
    if ([string]$Expected -cne [string]$Actual) {
        throw "$Evidence expected '$Expected' but observed '$Actual'."
    }
}

$retainedCases = @(
    @{ name='clean first run'; secrets=$false; postgres=$false; seaweed=$false; expected='CLEAN_FIRST_RUN' },
    @{ name='Seaweed-only without secrets'; secrets=$false; postgres=$false; seaweed=$true; expected='BLOCKED_RECOVERY_REQUIRED' },
    @{ name='PostgreSQL-only without secrets'; secrets=$false; postgres=$true; seaweed=$false; expected='BLOCKED_RECOVERY_REQUIRED' },
    @{ name='secrets with one retained volume'; secrets=$true; postgres=$true; seaweed=$false; expected='BLOCKED_RECOVERY_REQUIRED' },
    @{ name='complete retained restart'; secrets=$true; postgres=$true; seaweed=$true; expected='COMPLETE_RETAINED_STATE' }
)
foreach ($case in $retainedCases) {
    $actual = Resolve-D11RetainedState -SecretsPresent:$case.secrets -Volumes @{
        postgres=$case.postgres; seaweed=$case.seaweed
    }
    Assert-Equal $case.expected $actual $case.name
}

function New-CompleteCredentialState {
    $services = [ordered]@{}
    foreach ($service in @('evidence', 'financial_profile', 'financial_rules', 'reporting', 'audit')) {
        $services[$service] = [pscustomobject]@{
            database = "monergy_$service"
            owner = "monergy_${service}_owner"
            ownerPassword = "owner-password-$service"
            runtime = "monergy_${service}_runtime"
            runtimePassword = "runtime-password-$service"
        }
    }
    return [pscustomobject]@{ services = [pscustomobject]$services }
}

$completeCredentials = New-CompleteCredentialState
Assert-Equal $true (Test-D11RetainedServiceCredentials -State $completeCredentials) `
    'Complete retained service credentials'
$missingEntry = New-CompleteCredentialState
$missingEntry.services.PSObject.Properties.Remove('audit')
Assert-Equal $false (Test-D11RetainedServiceCredentials -State $missingEntry) `
    'Missing retained service credential entry'
$missingOwnerPassword = New-CompleteCredentialState
$missingOwnerPassword.services.reporting.ownerPassword = ''
Assert-Equal $false (Test-D11RetainedServiceCredentials -State $missingOwnerPassword) `
    'Missing retained owner password'
$missingRuntimePassword = New-CompleteCredentialState
$missingRuntimePassword.services.financial_rules.runtimePassword = $null
Assert-Equal $false (Test-D11RetainedServiceCredentials -State $missingRuntimePassword) `
    'Missing retained runtime password'

Assert-Equal 'BLOCKED_RECOVERY_REQUIRED' (Resolve-D11StartJournalState -OwnershipStates @('UNRESOLVED_LAUNCH')) `
    'Interrupted launch journal'
Assert-Equal 'BLOCKED_RECOVERY_REQUIRED' (Resolve-D11StartJournalState -OwnershipStates @('IDENTITY_MISMATCH')) `
    'Mismatched process identity'
Assert-Equal 'BLOCKED_RECOVERY_REQUIRED' (Resolve-D11StartJournalState -OwnershipStates @('VERIFIED_RUNNING','NOT_RUNNING')) `
    'Partial startup journal'
Assert-Equal 'SAFE_TO_START' (Resolve-D11StartJournalState -OwnershipStates @('NOT_RUNNING','NOT_RUNNING')) `
    'Stopped interrupted runtime reconciliation'
Assert-Equal 'ALREADY_RUNNING' (Resolve-D11StartJournalState -OwnershipStates @('VERIFIED_RUNNING','VERIFIED_RUNNING')) `
    'Repeated start'
Assert-Equal 'BLOCKED' (Resolve-D11StopJournalState -HasUnresolvedIdentity:$true) `
    'Unresolved stop metadata preservation'
Assert-Equal 'DEGRADED' (Resolve-D11ObservedRuntimeState -AllProcessesOwned:$true -StoresReady:$false `
    -VolumesReady:$true -HealthReady:$true -OwnerContractReady:$true -PreparedIdentityCurrent:$true) `
    'Provider outage with live application PIDs'
Assert-Equal 'BLOCKED' (Resolve-D11ObservedRuntimeState -AllProcessesOwned:$false -StoresReady:$true `
    -VolumesReady:$true -HealthReady:$true -OwnerContractReady:$true -PreparedIdentityCurrent:$true) `
    'Interrupted partial startup'
Assert-Equal 'READY_FOR_PROFILE' (Resolve-D11ObservedRuntimeState -AllProcessesOwned:$true -StoresReady:$true `
    -VolumesReady:$true -HealthReady:$true -OwnerContractReady:$true -PreparedIdentityCurrent:$true) `
    'Complete reconciled runtime'

$scratch = Join-Path ([IO.Path]::GetTempPath()) "monergy-d11-identity-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $scratch | Out-Null
try {
    [IO.File]::WriteAllText((Join-Path $scratch 'z.txt'), 'z', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $scratch 'A.txt'), 'A', [Text.UTF8Encoding]::new($false))
    $priorCulture = [Globalization.CultureInfo]::CurrentCulture
    try {
        [Globalization.CultureInfo]::CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo('tr-TR')
        $turkish = Get-CanonicalFileSetIdentity $scratch @('z.txt','A.txt')
        [Globalization.CultureInfo]::CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo('en-US')
        $english = Get-CanonicalFileSetIdentity $scratch @('A.txt','z.txt')
    }
    finally { [Globalization.CultureInfo]::CurrentCulture = $priorCulture }
    Assert-Equal $turkish.digest $english.digest 'Culture-independent canonical source identity'
    Assert-Equal 'A.txt' $english.files[0].path 'Ordinal case-sensitive canonical ordering'
    if ($english.files | Where-Object { $_.sha256 -cne $_.sha256.ToLowerInvariant() }) {
        throw 'Canonical file identity did not use lowercase SHA-256.'
    }
}
finally {
    $resolved = [IO.Path]::GetFullPath($scratch)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $resolved.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Recovery-test cleanup escaped the operating-system temporary directory.'
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}

Write-Output "D11 controller recovery tests passed: $($retainedCases.Count) retained-state cases, 4 retained-credential cases, interrupted/partial journals, unresolved stop preservation, provider outage, safe reconciliation, and canonical identity determinism."
