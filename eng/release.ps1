[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [string]$ImagePrefix = "sharplabnext",
    [string]$SourceRevision,
    [ValidateRange(1, 8)]
    [int]$MaxParallel = 4,
    [switch]$AllowUncommittedSourceForDevelopment,
    [switch]$AcceptMicrosoftLicenses,
    [switch]$Offline,
    [switch]$MetadataOnly,
    [switch]$RebuildImages,
    [switch]$BundleOnly
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $AcceptMicrosoftLicenses) {
    throw "AcceptMicrosoftLicenses is required because the complete image set contains Microsoft proprietary inputs."
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $lockPath = Join-Path $repositoryRoot "profiles/lock.json"
    $releaseId = [string](& dotnet run (Join-Path $repositoryRoot "eng/read-release-id.cs") -- $lockPath | Select-Object -Last 1)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($releaseId)) {
        throw "Could not read the release id from profiles/lock.json."
    }
    $OutputDirectory = Join-Path $repositoryRoot "artifacts/sharplabnext-$releaseId"
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) {
    throw "Bundle output already exists: $OutputDirectory"
}

$buildArguments = @{
    ImagePrefix = $ImagePrefix
    MaxParallel = $MaxParallel
}
if (-not [string]::IsNullOrWhiteSpace($SourceRevision)) {
    $buildArguments.SourceRevision = $SourceRevision
}
if ($AllowUncommittedSourceForDevelopment) { $buildArguments.AllowUncommittedSourceForDevelopment = $true }
if ($AcceptMicrosoftLicenses) { $buildArguments.AcceptMicrosoftLicenses = $true }
if ($Offline) { $buildArguments.Offline = $true }
if ($RebuildImages) { $buildArguments.NoReuseExisting = $true }

if (-not $BundleOnly) {
    $imageCacheHit = $false
    if (-not $RebuildImages) {
        $probeArguments = $buildArguments.Clone()
        $probeArguments.Remove("NoReuseExisting")
        $probeArguments.CacheProbe = $true
        $probeOutput = @(& (Join-Path $PSScriptRoot "build-images.ps1") @probeArguments 2>&1)
        $probeExitCode = $LASTEXITCODE
        $probeOutput | ForEach-Object { Write-Host $_ }
        if ($probeExitCode -ne 0) { throw "SharpLabNext image cache probe failed." }
        $imageCacheHit = $probeOutput | Where-Object {
            [string]::Equals([string]$_, "SHARPLABNEXT_IMAGE_CACHE=hit", [StringComparison]::Ordinal)
        } | Select-Object -First 1
        $imageCacheHit = $null -ne $imageCacheHit
    }

    if (-not $imageCacheHit) {
        & (Join-Path $PSScriptRoot "build.ps1") -Configuration Release -SkipValidation
        if ($LASTEXITCODE -ne 0) { throw "SharpLabNext host build and static validation failed." }

        & (Join-Path $PSScriptRoot "build-images.ps1") @buildArguments
        if ($LASTEXITCODE -ne 0) { throw "SharpLabNext image build failed." }
    }
    else {
        Write-Host "All release images are cached; skipping host and Docker image builds."
    }
}

$bundleArguments = @{
    OutputDirectory = $OutputDirectory
    ImagePrefix = $ImagePrefix
    AllowDevelopmentImageInputs = $true
}
if (-not [string]::IsNullOrWhiteSpace($SourceRevision)) {
    $bundleArguments.SourceRevision = $SourceRevision
}
if ($AllowUncommittedSourceForDevelopment) { $bundleArguments.AllowUncommittedSourceForDevelopment = $true }
if ($MetadataOnly) { $bundleArguments.MetadataOnly = $true }

& (Join-Path $PSScriptRoot "bundle.ps1") @bundleArguments
if ($LASTEXITCODE -ne 0) { throw "SharpLabNext bundle creation failed." }
