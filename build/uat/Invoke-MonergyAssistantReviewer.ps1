[CmdletBinding(DefaultParameterSetName='Change')]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-z][a-z0-9-]{0,30}$')][string]$Profile,
    [Parameter(Mandatory,ParameterSetName='Change')][ValidateSet('Grant','Revoke')][string]$Action,
    [Parameter(Mandatory,ParameterSetName='Change')][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$')][string]$PrincipalId,
    [Parameter(Mandatory,ParameterSetName='Change')][ValidateRange(0,2147483647)][int]$ExpectedPolicyRevision,
    [Parameter(Mandatory,ParameterSetName='Change')][ValidateSet('REVIEW_ASSIGNMENT','REVIEW_REMOVAL','SECURITY_RESPONSE')][string]$ReasonCode,
    [Parameter(Mandatory,ParameterSetName='Change')][Parameter(Mandatory,ParameterSetName='Operation')][Parameter(Mandatory,ParameterSetName='Resume')][guid]$OperationId,
    [Parameter(Mandatory,ParameterSetName='Status')][switch]$Status,
    [Parameter(Mandatory,ParameterSetName='Discover')][switch]$Discover,
    [Parameter(ParameterSetName='Discover')][ValidateLength(0,256)][string]$After,
    [Parameter(ParameterSetName='Discover')][ValidateRange(1,100)][int]$Limit=20,
    [Parameter(ParameterSetName='Discover')][ValidateSet('Pending','Retry','Delivering','Paused')][string]$DeliveryState,
    [Parameter(Mandatory,ParameterSetName='Operation')][switch]$Operation,
    [Parameter(Mandatory,ParameterSetName='Resume')][guid]$EventId,
    [Parameter(Mandatory,ParameterSetName='Resume')][ValidateRange(0,2)][int]$ExpectedRecoveryCount
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$profilePath = Join-Path $repository ".artifacts/uat/$Profile"
$state = Get-Content -LiteralPath (Join-Path $profilePath 'profile.json') -Raw | ConvertFrom-Json
if ($null -eq $state.PSObject.Properties['assistant'] -or $state.assistant.enabled -ne $true -or $state.instanceId -notmatch '^[0-9a-f]{32}$') { throw 'An explicitly enabled existing Assistant LOCAL profile is required.' }
# The protected operator credential remains in its AM-only mounted file. Neither
# this command line nor the ordinary Gateway/feedback configuration receives it.
$mode = '--platform-reviewer-command'
$payload = switch ($PSCmdlet.ParameterSetName) {
    'Status' { $mode='--platform-reviewer-status'; $null }
    'Discover' { $mode='--platform-reviewer-discover'; @{ after=$(if($After){$After}else{$null}); limit=$Limit; state=$(if($DeliveryState){$DeliveryState}else{$null}) } }
    'Operation' { $mode='--platform-reviewer-operation'; @{ operationId=$OperationId.ToString() } }
    'Resume' { $mode='--platform-reviewer-resume'; @{ eventId=$EventId.ToString(); operationId=$OperationId.ToString(); expectedRecoveryCount=$ExpectedRecoveryCount } }
    default { @{ contractVersion='1.0.0'; operationId=$OperationId.ToString(); principalId=$PrincipalId;
        capability='platform.assistant.feedback.review'; action=$Action; expectedPolicyRevision=$ExpectedPolicyRevision; reasonCode=$ReasonCode } }
}
$request = if ($null -eq $payload) { '' } else { $payload | ConvertTo-Json -Compress }
$arguments = @('compose','--env-file',(Join-Path $profilePath 'compose.env'),'-f',(Join-Path $PSScriptRoot 'compose.yml'),
    '-f',(Join-Path $PSScriptRoot 'compose.assistant.yml'),'--project-name',"monergy-uat-$($state.instanceId.Substring(0,8))",
    'exec','-T','access-management','dotnet','Monergy.Services.AccessManagement.dll',$mode)
$request | & docker @arguments
if ($LASTEXITCODE -ne 0) { throw 'The AM owner did not accept the reviewer command. No grant or audit-delivery success is claimed.' }
