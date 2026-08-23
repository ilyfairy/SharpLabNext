[CmdletBinding()]
param(
    [string]$InstallRoot,
    [string]$TrustedPublicKey,
    [string]$TrustedPublicKeySha256,
    [string]$ExpectedSigningKeyId,
    [switch]$AllowUnsigned,
    [switch]$SkipArtifactBackup,
    [switch]$CurrentOnly,
    [int]$ReadyTimeoutSeconds = 180,
    [string]$SmokeBaseAddress
)

$ErrorActionPreference = 'Stop'
$bundleRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $bundleRoot 'deployment-common.ps1')
$internalServiceTokenFile = if ($env:SHARPLABNEXT_INTERNAL_SERVICE_TOKEN_FILE) {
    $env:SHARPLABNEXT_INTERNAL_SERVICE_TOKEN_FILE
}
else {
    Join-Path $bundleRoot 'secrets/internal-service-token'
}
$env:SHARPLABNEXT_INTERNAL_SERVICE_TOKEN_FILE = Resolve-ContainerSecretFile `
    $internalServiceTokenFile `
    'Internal service token'
if ($env:SHARPLABNEXT_GITHUB_OAUTH_ENABLED -ieq 'true') {
    if ([string]::IsNullOrWhiteSpace($env:SHARPLABNEXT_GITHUB_OAUTH_CLIENT_SECRET_FILE)) {
        throw 'GitHub OAuth is enabled but SHARPLABNEXT_GITHUB_OAUTH_CLIENT_SECRET_FILE is empty.'
    }
    $env:SHARPLABNEXT_GITHUB_OAUTH_CLIENT_SECRET_FILE = Resolve-ContainerSecretFile `
        $env:SHARPLABNEXT_GITHUB_OAUTH_CLIENT_SECRET_FILE `
        'GitHub OAuth client secret'
}
$document = Get-ReleaseDocument $bundleRoot
$releaseId = [string]$document.releaseId
$InstallRoot = Resolve-InstallRoot $InstallRoot
New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null
$current = Get-ReleasePointer $InstallRoot 'current-release'
$previous = Get-ReleasePointer $InstallRoot 'previous-release'

$verifyArguments = @{
    ReleaseRoot = $bundleRoot
    LoadImages = $true
}
if ($TrustedPublicKey) { $verifyArguments.TrustedPublicKey = [IO.Path]::GetFullPath($TrustedPublicKey) }
if ($TrustedPublicKeySha256) { $verifyArguments.TrustedPublicKeySha256 = $TrustedPublicKeySha256 }
if ($ExpectedSigningKeyId) { $verifyArguments.ExpectedSigningKeyId = $ExpectedSigningKeyId }
if ($AllowUnsigned) { $verifyArguments.AllowUnsigned = $true }
Invoke-ReleaseVerification @verifyArguments

$releaseRoot = Install-ReleaseAssets $bundleRoot $InstallRoot $releaseId
Test-InstalledDeployment $releaseRoot
try {
    if ($current -and $current -cne $releaseId -and -not $SkipArtifactBackup) {
        Backup-ArtifactStore (Join-Path (Join-Path $InstallRoot 'releases') $current) $current $releaseRoot
    }
    Invoke-ReleaseComposeUp $releaseRoot $releaseId
    Invoke-ReleaseSmoke $releaseRoot $releaseId $ReadyTimeoutSeconds $SmokeBaseAddress
}
catch {
    $deploymentFailure = $_
    try {
        if ($current) {
            $currentRoot = Join-Path (Join-Path $InstallRoot 'releases') $current
            try { Invoke-ReleaseComposeDown $releaseRoot $releaseId } catch { }
            if (Test-Path -LiteralPath (Join-Path $releaseRoot 'rollback/artifact-data')) {
                Restore-ArtifactStoreBackup $releaseRoot $currentRoot $current
            }
            Restore-InstalledRelease $currentRoot $ReadyTimeoutSeconds $SmokeBaseAddress
        }
        else {
            Invoke-ReleaseComposeDown $releaseRoot $releaseId
        }
    }
    catch {
        throw "Release '$releaseId' failed and restoration also failed. Deployment: $($deploymentFailure.Exception.Message) Restoration: $($_.Exception.Message)"
    }
    throw "Release '$releaseId' failed readiness checks; release '$current' was restored: $($deploymentFailure.Exception.Message)"
}

if ($CurrentOnly) {
    $retainedPrevious = if ($current -and $current -cne $releaseId) { $current } else { $previous }
    $null = Test-CurrentOnlyRetentionSources `
        $InstallRoot `
        $releaseId `
        $retainedPrevious `
        $previous `
        $current `
        $previous
    Set-ReleasePointerPair $InstallRoot $releaseId $current $previous
    Remove-CurrentOnlyPreviousRelease $InstallRoot $releaseId $retainedPrevious $previous
}
else {
    Set-ReleasePointerPair $InstallRoot $releaseId $current $previous
}
Write-Host "Installed SharpLabNext release $releaseId at $releaseRoot"
