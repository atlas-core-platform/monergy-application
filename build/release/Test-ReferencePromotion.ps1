[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PromotionPath,
    [Parameter(Mandatory)][string]$CandidateManifestPath,
    [switch]$PassThru
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'ImmutableArtifact.Common.psm1') -Force

$result = Assert-D16Promotion $PromotionPath $CandidateManifestPath
if ($PassThru) {
    Write-Output $result
}
else {
    Write-Output "D16 reference promotion PASS: $($result.Record.promotionIdentity)"
}
