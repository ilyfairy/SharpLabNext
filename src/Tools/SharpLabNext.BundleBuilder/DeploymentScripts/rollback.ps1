[CmdletBinding()]
param(
    [string]$InstallRoot,
    [switch]$KeepCurrentArtifactData,
    [int]$ReadyTimeoutSeconds = 180,
    [string]$SmokeBaseAddress
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $scriptRoot 'deployment-common.ps1')
$InstallRoot = Resolve-InstallRoot $InstallRoot
$current = Get-ReleasePointer $InstallRoot 'current-release'
$previous = Get-ReleasePointer $InstallRoot 'previous-release'
if (-not $previous) { throw 'No previous SharpLabNext release is recorded.' }
$previousRoot = Join-Path (Join-Path $InstallRoot 'releases') $previous
$currentRoot = if ($current) { Join-Path (Join-Path $InstallRoot 'releases') $current } else { $null }
$safetyRoot = $null

try {
    if ($currentRoot -and -not $KeepCurrentArtifactData -and (Test-Path -LiteralPath (Join-Path $currentRoot 'rollback/artifact-data'))) {
        $safetyRoot = Join-Path $InstallRoot ".rollback-safety.$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $safetyRoot | Out-Null
        Backup-ArtifactStore $currentRoot $current $safetyRoot
        Restore-ArtifactStoreBackup $currentRoot $previousRoot $previous
    }
    Restore-InstalledRelease $previousRoot $ReadyTimeoutSeconds $SmokeBaseAddress
}
catch {
    $rollbackFailure = $_
    if ($currentRoot) {
        try {
            if ($safetyRoot -and (Test-Path -LiteralPath (Join-Path $safetyRoot 'rollback/artifact-data'))) {
                Restore-ArtifactStoreBackup $safetyRoot $currentRoot $current
            }
            Restore-InstalledRelease $currentRoot $ReadyTimeoutSeconds $SmokeBaseAddress
        }
        catch { throw "Rollback to '$previous' failed and current release '$current' could not be restored. Rollback: $($rollbackFailure.Exception.Message) Restoration: $($_.Exception.Message)" }
    }
    throw "Rollback to '$previous' failed; current release '$current' was restored: $($rollbackFailure.Exception.Message)"
}
finally {
    if ($safetyRoot -and (Test-Path -LiteralPath $safetyRoot)) { Remove-Item -Recurse -Force -LiteralPath $safetyRoot }
}

Set-ReleasePointer $InstallRoot 'current-release' $previous
if ($current -and $current -cne $previous) { Set-ReleasePointer $InstallRoot 'previous-release' $current }
Write-Host "Rolled back SharpLabNext to release $previous"
