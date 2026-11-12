[CmdletBinding()]
param(
    [string]$RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')),
    [string]$HarnessRoot = 'E:\Projects\Atlas\monergy-application\build\demo'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$templateRoot = Join-Path $HarnessRoot 'cohort-h'
$templatePath = Join-Path $templateRoot 'Start-CohortHInfrastructure.ps1'
$source = Get-Content -LiteralPath $templatePath -Raw -Encoding utf8
$replacements = [ordered]@{
    'p2-cohort-h-acceptance' = 'p2-cohort-n-acceptance'
    'monergy-p2h-20261108' = 'monergy-p2n-20261112'
    '55472' = '55642'
    '58373' = '58543'
    'monergy-evidence-h' = 'monergy-evidence-n'
    '.artifacts/cohort-h' = '.artifacts/cohort-n'
    'p2h_admin' = 'p2n_admin'
    'p2h-fixture' = 'p2n-fixture'
    'p2h-document-intelligence-migrations' = 'p2n-document-intelligence-migrations'
    'P2H_OWNER_CONNECTION' = 'P2N_OWNER_CONNECTION'
    'Cohort H' = 'Cohort N'
}
foreach ($entry in $replacements.GetEnumerator()) {
    $source = $source.Replace([string]$entry.Key, [string]$entry.Value)
}
$source = $source.Replace(
    '$composeOverride = Join-Path $PSScriptRoot ''compose.override.yml''',
    '$composeOverride = Join-Path $RepositoryRoot ''build/demo/cohort-n/compose.override.yml''')
$templateRootLiteral = "'$($templateRoot.Replace("'", "''"))'"
$source = $source.Replace('$PSScriptRoot', $templateRootLiteral)
$bootstrap = [scriptblock]::Create($source)
& $bootstrap -RepositoryRoot $RepositoryRoot
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$privateRoot = Join-Path $RepositoryRoot '.artifacts/cohort-n/private'
$statePath = Join-Path $privateRoot 'runtime-state.json'
$state = Get-Content -LiteralPath $statePath -Raw -Encoding utf8 | ConvertFrom-Json
$rules = $state.services.financial_rules
$env:P2N_RULES_OWNER_CONNECTION = "Host=127.0.0.1;Port=$($state.postgresPort);Database=$($rules.database);Username=$($rules.owner);Password=$($rules.ownerPassword);SSL Mode=Disable;Include Error Detail=false"
try {
    dotnet run --project (Join-Path $RepositoryRoot 'build/Monergy.DatabaseMigrator/Monergy.DatabaseMigrator.csproj') --configuration Release -- --service financial-rules --repository-root $RepositoryRoot --connection-environment P2N_RULES_OWNER_CONNECTION
    if ($LASTEXITCODE -ne 0) { throw 'The financial-rules migrations failed for isolated Cohort N storage.' }
}
finally {
    Remove-Item Env:P2N_RULES_OWNER_CONNECTION -ErrorAction SilentlyContinue
}
$rulesConfig = @{ RuntimeConnection="Host=host.docker.internal;Port=$($state.postgresPort);Database=$($rules.database);Username=$($rules.runtime);Password=$($rules.runtimePassword);SSL Mode=Disable;Timeout=5;Include Error Detail=false" }
[IO.File]::WriteAllText((Join-Path $privateRoot 'financial-rules-persistence.json'), ($rulesConfig | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
if ($env:OS -eq 'Windows_NT') {
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent().Name
    & icacls (Join-Path $RepositoryRoot '.artifacts/cohort-n') /inheritance:r /grant:r "${identity}:(OI)(CI)F" 'SYSTEM:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Unable to protect the Cohort N credential directory.' }
}
[pscustomobject]@{
    Project = $state.project
    PostgreSqlPort = $state.postgresPort
    S3Port = $state.s3Port
    FinancialRulesMigrations = (Get-ChildItem (Join-Path $RepositoryRoot 'services/financial-rules/migrations') -Filter '*.sql').Count
    FinancialRulesPersistence = Test-Path -LiteralPath (Join-Path $privateRoot 'financial-rules-persistence.json')
} | ConvertTo-Json
