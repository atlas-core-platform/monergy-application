[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-z][a-z0-9-]{0,30}$')][string]$Profile,
    [Parameter(Mandatory)][ValidateSet('Grant','Revoke')][string]$Action,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$')][string]$PrincipalId,
    [Parameter(Mandatory)][ValidateRange(0,2147483647)][int]$ExpectedPolicyRevision,
    [Parameter(Mandatory)][ValidateSet('REVIEW_ASSIGNMENT','REVIEW_REMOVAL','SECURITY_RESPONSE')][string]$ReasonCode,
    [Parameter(Mandatory)][guid]$OperationId
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$profilePath = Join-Path $repository ".artifacts/uat/$Profile"
$state = Get-Content -LiteralPath (Join-Path $profilePath 'profile.json') -Raw | ConvertFrom-Json
if ($null -eq $state.PSObject.Properties['assistant'] -or $state.assistant.enabled -ne $true -or $state.instanceId -notmatch '^[0-9a-f]{32}$') { throw 'An explicitly enabled existing Assistant LOCAL profile is required.' }
# The protected operator credential remains in its AM-only mounted file. Neither
# this command line nor the ordinary Gateway/feedback configuration receives it.
$request = @{ contractVersion='1.0.0'; operationId=$OperationId.ToString(); principalId=$PrincipalId;
    capability='platform.assistant.feedback.review'; action=$Action; expectedPolicyRevision=$ExpectedPolicyRevision; reasonCode=$ReasonCode } | ConvertTo-Json -Compress
$arguments = @('compose','--env-file',(Join-Path $profilePath 'compose.env'),'-f',(Join-Path $PSScriptRoot 'compose.yml'),
    '-f',(Join-Path $PSScriptRoot 'compose.assistant.yml'),'--project-name',"monergy-uat-$($state.instanceId.Substring(0,8))",
    'exec','-T','access-management','dotnet','Monergy.Services.AccessManagement.dll','--platform-reviewer-command')
$request | & docker @arguments
if ($LASTEXITCODE -ne 0) { throw 'The AM owner did not accept the reviewer command. No grant or audit-delivery success is claimed.' }
