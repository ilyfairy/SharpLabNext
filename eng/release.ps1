[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [string]$ImagePrefix = "sharplabnext",
    [string]$SourceRevision,
    [ValidateRange(1, 8)]
    [int]$MaxParallel = 5,
    [switch]$AcceptMicrosoftLicenses,
    [switch]$Offline,
    [switch]$MetadataOnly,
    [switch]$RebuildImages,
    [string[]]$RebuildTarget = @(),
    [switch]$BundleOnly
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $AcceptMicrosoftLicenses) { throw "AcceptMicrosoftLicenses is required because the complete image set contains Microsoft proprietary inputs." }
if (-not [string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
    if (Test-Path -LiteralPath $OutputDirectory) { throw "Bundle output already exists: $OutputDirectory" }
}

$sourceArguments = @("run", (Join-Path $repositoryRoot "eng/tools/resolve-source-provenance.cs"), "--", "--repository-root", $repositoryRoot)
if (-not [string]::IsNullOrWhiteSpace($SourceRevision)) { $sourceArguments += @("--source-revision", $SourceRevision) }
$sourceOutput = @(& dotnet @sourceArguments)
if ($LASTEXITCODE -ne 0) { throw "Source provenance validation failed." }
$revisionLine = $sourceOutput | Where-Object { $_ -is [string] -and $_.StartsWith("SHARPLABNEXT_SOURCE_REVISION=", [StringComparison]::Ordinal) } | Select-Object -Last 1
if ($null -eq $revisionLine) { throw "Source provenance resolver did not return a revision." }
$SourceRevision = $revisionLine.Substring("SHARPLABNEXT_SOURCE_REVISION=".Length)

$buildArguments = @{
    ImagePrefix = $ImagePrefix
    MaxParallel = $MaxParallel
    All = $true
}
$buildArguments.SourceRevision = $SourceRevision
if ($RebuildTarget.Count -gt 0) { $buildArguments.RebuildTarget = $RebuildTarget }
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

$bundleArguments = @{ ImagePrefix = $ImagePrefix }
$bundleArguments.SourceRevision = $SourceRevision
if (-not [string]::IsNullOrWhiteSpace($OutputDirectory)) { $bundleArguments.OutputDirectory = $OutputDirectory }
if ($MetadataOnly) { $bundleArguments.MetadataOnly = $true }

& (Join-Path $PSScriptRoot "bundle.ps1") @bundleArguments
if ($LASTEXITCODE -ne 0) { throw "SharpLabNext bundle creation failed." }
