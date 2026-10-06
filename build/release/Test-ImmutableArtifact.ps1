[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ManifestPath,
    [switch]$PassThru
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'ImmutableArtifact.Common.psm1') -Force

$result = Assert-D16Candidate $ManifestPath
if ($PassThru) {
    Write-Output $result
}
else {
    Write-Output "D16 immutable artifact PASS: $($result.Manifest.candidateIdentity)"
}
