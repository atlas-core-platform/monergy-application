[CmdletBinding()]
param(
    [string]$RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')),
    [string]$Profile = 'p2p-durable-documents',
    [int]$Port = 4311
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$template = Join-Path $RepositoryRoot 'build/demo/cohort-n/Prepare-CohortNProfile.ps1'
$source = Get-Content -LiteralPath $template -Raw -Encoding utf8
$replace = [ordered]@{
    'p2n-admin-connectivity'='p2p-durable-documents'
    '4291'='4311'
    '.artifacts/cohort-n'='.artifacts/cohort-p'
    'cohort-n'='cohort-p'
    'Cohort N'='Cohort P'
    'COHORT_N'='COHORT_P'
}
foreach($entry in $replace.GetEnumerator()) { $source=$source.Replace([string]$entry.Key,[string]$entry.Value) }
$templateRoot=(Split-Path -Parent $template).Replace("'","''")
$source=$source.Replace('$PSScriptRoot',"'$templateRoot'")
$source=$source.Replace('EnableCustomerRegistration=$true;AccountAgreementFile=', 'EnableCustomerRegistration=$true;EnablePersonaLogin=$true;AccountAgreementFile=')
$bootstrap=[scriptblock]::Create($source)
& $bootstrap -RepositoryRoot $RepositoryRoot -Profile $Profile -Port $Port
if($LASTEXITCODE-ne 0){exit $LASTEXITCODE}
$agreementPath=Join-Path $RepositoryRoot '.artifacts/cohort-p/private/account-agreements.json'
$agreements=Get-Content -LiteralPath $agreementPath -Raw -Encoding utf8|ConvertFrom-Json
if($null-eq$agreements.Publications.PSObject.Properties['T002']){
    $t001=$agreements.Publications.T001
    $t001.PublishedAt=([DateTimeOffset]$t001.PublishedAt).ToUniversalTime().ToString('O')
    $agreements.Publications|Add-Member -NotePropertyName T002 -NotePropertyValue ([pscustomobject]@{
        TenantId='T002'
        PublishedAt=([DateTimeOffset]$t001.PublishedAt).ToUniversalTime().ToString('O')
        Documents=@($t001.Documents)
    })
    [IO.File]::WriteAllText($agreementPath,($agreements|ConvertTo-Json -Depth 20),[Text.UTF8Encoding]::new($false))
}
[pscustomobject]@{Profile=$Profile;Port=$Port;EvidenceTenants=2;ClassificationTenants=2}|ConvertTo-Json
