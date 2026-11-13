[CmdletBinding()]
param(
    [string]$RepositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')),
    [string]$Profile='p2p-durable-documents',
    [switch]$AttachOnly
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$template=Join-Path $RepositoryRoot 'build/demo/cohort-n/Start-CohortNProfile.ps1'
$source=Get-Content -LiteralPath $template -Raw -Encoding utf8
$replace=[ordered]@{
    'p2n-admin-connectivity'='p2p-durable-documents'
    '.artifacts/cohort-n'='.artifacts/cohort-p'
    'cohort-n'='cohort-p'
    'Cohort N'='Cohort P'
    'COHORT_N'='COHORT_P'
}
foreach($entry in $replace.GetEnumerator()){$source=$source.Replace([string]$entry.Key,[string]$entry.Value)}
$scriptRoot=$PSScriptRoot.Replace("'","''")
$source=$source.Replace('$PSScriptRoot',"'$scriptRoot'")
$source=$source.Replace('RequireRetainedDocumentProcessing=$true;RequireRetainedFinancialRules=$true;RequireCustomerSetup=$true', 'RequireRetainedDocumentProcessing=$true;RequireRetainedFinancialRules=$true;RequireCustomerSetup=$true;EnablePersonaLogin=$true')
$bootstrap=[scriptblock]::Create($source)
& $bootstrap -RepositoryRoot $RepositoryRoot -Profile $Profile -AttachOnly:$AttachOnly
if($LASTEXITCODE-ne 0){exit $LASTEXITCODE}
