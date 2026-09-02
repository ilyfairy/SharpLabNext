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
if ($AcceptMicrosoftLicenses) { $buildArguments.AcceptMicrosoftLicenses = $true }
if ($Offline) { $buildArguments.Offline = $true }

if (-not $BundleOnly) {
    & (Join-Path $PSScriptRoot "build.ps1") -Configuration Release -SkipValidation
    if ($LASTEXITCODE -ne 0) { throw "SharpLabNext host build and static validation failed." }

    & (Join-Path $PSScriptRoot "build-images.ps1") @buildArguments
    if ($LASTEXITCODE -ne 0) { throw "SharpLabNext image build failed." }
}

$bundleArguments = @{ ImagePrefix = $ImagePrefix }
$bundleArguments.SourceRevision = $SourceRevision
if (-not [string]::IsNullOrWhiteSpace($OutputDirectory)) { $bundleArguments.OutputDirectory = $OutputDirectory }
if ($MetadataOnly) { $bundleArguments.MetadataOnly = $true }

& (Join-Path $PSScriptRoot "bundle.ps1") @bundleArguments
if ($LASTEXITCODE -ne 0) { throw "SharpLabNext bundle creation failed." }
