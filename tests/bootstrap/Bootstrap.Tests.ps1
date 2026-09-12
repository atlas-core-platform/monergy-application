[CmdletBinding()]
param([string]$RepositoryRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))

$ErrorActionPreference = 'Stop'

& (Join-Path $RepositoryRoot 'build/verify-bootstrap.ps1') -RepositoryRoot $RepositoryRoot -SelfTest
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& (Join-Path $RepositoryRoot 'build/verify-bootstrap.ps1') -RepositoryRoot $RepositoryRoot
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Output 'Bootstrap tests passed.'

