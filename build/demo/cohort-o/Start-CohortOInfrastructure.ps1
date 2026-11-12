[CmdletBinding()]
param([string]$RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')))
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$template=Join-Path $RepositoryRoot 'build/demo/cohort-n/Start-CohortNInfrastructure.ps1'
$source=Get-Content -LiteralPath $template -Raw -Encoding utf8
$replace=[ordered]@{
  'p2-cohort-n-acceptance'='p2-cohort-o-acceptance'; 'monergy-p2n-20261112'='monergy-p2o-20261112';
  '55642'='55652'; '58543'='58553'; 'monergy-evidence-n'='monergy-evidence-o';
  '.artifacts/cohort-n'='.artifacts/cohort-o'; 'p2n_admin'='p2o_admin'; 'p2n-fixture'='p2o-fixture';
  'p2n-document-intelligence-migrations'='p2o-document-intelligence-migrations'; 'P2N'='P2O';
  'Cohort N'='Cohort O'; 'build/demo/cohort-n/compose.override.yml'='build/demo/cohort-o/compose.override.yml'
}
foreach($entry in $replace.GetEnumerator()){$source=$source.Replace([string]$entry.Key,[string]$entry.Value)}
$bootstrap=[scriptblock]::Create($source)
& $bootstrap -RepositoryRoot $RepositoryRoot
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
