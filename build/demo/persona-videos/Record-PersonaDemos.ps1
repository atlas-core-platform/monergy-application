param(
    [ValidateSet('Review','Record','FinalizePrivacy','RefreshRevision')]
    [string]$Action = 'Review',
    [string]$ProfilePath = $env:MONERGY_DEMO_PROFILE,
    [string]$OutputDirectory = 'build/demo/output/persona-videos',
    [int]$Concurrency = 3
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ProfilePath)) {
    throw 'Set MONERGY_DEMO_PROFILE or pass -ProfilePath. The protected profile is read at runtime and is never copied to the demo output.'
}

$mode = switch ($Action) {
    'Review' { '--review' }
    'Record' { '--record' }
    'FinalizePrivacy' { '--finalize-privacy' }
    'RefreshRevision' { '--refresh-revision' }
}

$env:MONERGY_DEMO_PROFILE = $ProfilePath
$env:MONERGY_DEMO_OUTPUT = $OutputDirectory
$env:MONERGY_DEMO_CONCURRENCY = [string]$Concurrency
node ./build/demo/persona-videos/record-persona-demos.mjs $mode
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
