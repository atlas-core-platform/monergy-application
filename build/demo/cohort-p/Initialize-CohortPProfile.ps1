[CmdletBinding()]
param(
    [string]$RepositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')),
    [string]$Profile='p2p-durable-documents'
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$template=Join-Path $RepositoryRoot 'build/demo/cohort-n/Initialize-CohortNProfile.ps1'
$source=Get-Content -LiteralPath $template -Raw -Encoding utf8
$replace=[ordered]@{
    'p2-cohort-n-acceptance'='p2-cohort-p-acceptance'
    'p2n-admin-connectivity'='p2p-durable-documents'
    '4291'='4311'
    'Cohort N'='Cohort P'
}
foreach($entry in $replace.GetEnumerator()){$source=$source.Replace([string]$entry.Key,[string]$entry.Value)}
$bootstrap=[scriptblock]::Create($source)
$global:LASTEXITCODE=0
& $bootstrap -RepositoryRoot $RepositoryRoot -Profile $Profile
