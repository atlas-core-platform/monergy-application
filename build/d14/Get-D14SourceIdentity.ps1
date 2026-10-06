[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [string]$Baseline = '698f4b8f8ac180f2eeea1ead9dc0bb2a38b56346'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
}

$tracked = @(git -C $RepositoryRoot diff --name-only --diff-filter=ACMRTUXB $Baseline --)
if ($LASTEXITCODE -ne 0) { throw 'Unable to enumerate tracked D14 candidate files.' }
$untracked = @(git -C $RepositoryRoot ls-files --others --exclude-standard)
if ($LASTEXITCODE -ne 0) { throw 'Unable to enumerate untracked D14 candidate files.' }

[string[]]$paths = @($tracked + $untracked |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    ForEach-Object { ([string]$_).Replace('\', '/') } |
    Select-Object -Unique)
[Array]::Sort($paths, [StringComparer]::Ordinal)

$hash = [System.Security.Cryptography.IncrementalHash]::CreateHash(
    [System.Security.Cryptography.HashAlgorithmName]::SHA256)
try {
    foreach ($path in $paths) {
        $pathBytes = [Text.Encoding]::UTF8.GetBytes($path)
        $fileBytes = [IO.File]::ReadAllBytes((Join-Path $RepositoryRoot $path))
        foreach ($value in @($pathBytes.Length, $fileBytes.Length)) {
            $lengthBytes = [BitConverter]::GetBytes([int]$value)
            if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($lengthBytes) }
            $hash.AppendData($lengthBytes)
        }
        $hash.AppendData($pathBytes)
        $hash.AppendData($fileBytes)
    }
    $digest = ([BitConverter]::ToString($hash.GetHashAndReset()) -replace '-', '').ToLowerInvariant()
}
finally {
    $hash.Dispose()
}

Write-Output 'CANONICAL_PATH_ORDER=StringComparer.Ordinal'
Write-Output 'ENCODING=UTF-8 paths; worktree file bytes unchanged'
Write-Output 'FRAMING=big-endian Int32 path-byte-length + file-byte-length + path bytes + file bytes'
Write-Output "BASELINE=$Baseline"
Write-Output "PATH_COUNT=$($paths.Count)"
foreach ($path in $paths) { Write-Output "PATH=$path" }
Write-Output "SHA256=$digest"

