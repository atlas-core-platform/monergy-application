[CmdletBinding()]
param(
  [string]$RepositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')),
  [string]$Profile='p2o-customer-completion'
)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$template=Join-Path $RepositoryRoot 'build/demo/cohort-n/Initialize-CohortNProfile.ps1'
$source=Get-Content -LiteralPath $template -Raw -Encoding utf8
$replace=[ordered]@{'p2-cohort-n-acceptance'='p2-cohort-o-acceptance';'p2n-admin-connectivity'='p2o-customer-completion';'4291'='4301';'Cohort N'='Cohort O'}
foreach($entry in $replace.GetEnumerator()){$source=$source.Replace([string]$entry.Key,[string]$entry.Value)}
$bootstrap=[scriptblock]::Create($source)
& $bootstrap -RepositoryRoot $RepositoryRoot -Profile $Profile
