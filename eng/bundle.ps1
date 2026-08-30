[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [string]$ImagePrefix = "sharplabnext",
    [string[]]$Image = @(),
    [string]$SigningKey,
    [string]$SigningPublicKey,
    [string]$SigningKeyId,
    [string]$SourceRevision,
    [switch]$AllowUncommittedSourceForDevelopment,
    [switch]$AllowDevelopmentImageInputs,
    [switch]$MetadataOnly
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$useContentSourceIdentity = [string]::IsNullOrWhiteSpace($SigningKey)
$previousSourceIdentityMode = [Environment]::GetEnvironmentVariable("SHARPLABNEXT_SOURCE_IDENTITY_MODE")
$lockPath = Join-Path $repositoryRoot "profiles/lock.json"
$releaseId = [string](& dotnet run (Join-Path $repositoryRoot "eng/tools/read-release-id.cs") -- $lockPath | Select-Object -Last 1)
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($releaseId)) {
    throw "Could not read the release id from profiles/lock.json."
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $repositoryRoot "artifacts/sharplabnext-$releaseId" }
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw "Bundle output already exists: $OutputDirectory" }
if ([string]::IsNullOrWhiteSpace($SigningKey) -ne [string]::IsNullOrWhiteSpace($SigningPublicKey)) {
    throw "SigningKey and SigningPublicKey are required together."
}
if (-not [string]::IsNullOrWhiteSpace($SigningKeyId) -and [string]::IsNullOrWhiteSpace($SigningKey)) {
    throw "SigningKeyId requires SigningKey and SigningPublicKey."
}
if (($AllowUncommittedSourceForDevelopment -or $AllowDevelopmentImageInputs) -and
    -not [string]::IsNullOrWhiteSpace($SigningKey)) {
    throw "Development source or image inputs cannot be used for a signed bundle."
}

Push-Location $repositoryRoot
try {
    if ($useContentSourceIdentity) {
        $env:SHARPLABNEXT_SOURCE_IDENTITY_MODE = "content"
    }
    else {
        Remove-Item Env:SHARPLABNEXT_SOURCE_IDENTITY_MODE -ErrorAction SilentlyContinue
    }
    $sourceArguments = @(
        "run", "eng/tools/resolve-source-provenance.cs", "--",
        "--repository-root", $repositoryRoot
    )
    if (-not [string]::IsNullOrWhiteSpace($SourceRevision)) { $sourceArguments += @("--source-revision", $SourceRevision) }
    if ($AllowUncommittedSourceForDevelopment) { $sourceArguments += "--allow-uncommitted-source-for-development" }
    if (-not [string]::IsNullOrWhiteSpace($SigningKey)) { $sourceArguments += "--verify-git" }
    $sourceOutput = @(& dotnet @sourceArguments)
    if ($LASTEXITCODE -ne 0) { throw "Source provenance validation failed." }
    $revisionPrefix = "SHARPLABNEXT_SOURCE_REVISION="
    $revisionLine = $sourceOutput |
        Where-Object { $_ -is [string] -and $_.StartsWith($revisionPrefix, [StringComparison]::Ordinal) } |
        Select-Object -Last 1
    if ([string]::IsNullOrWhiteSpace($revisionLine)) { throw "Source provenance resolver did not return a revision." }
    $SourceRevision = $revisionLine.Substring($revisionPrefix.Length)

    $arguments = @(
        "run", "--project", "src/Tools/SharpLabNext.BundleBuilder",
        "--configuration", "Release", "--",
        "--repository-root", $repositoryRoot,
        "--output", $OutputDirectory,
        "--image-prefix", $ImagePrefix,
        "--source-revision", $SourceRevision
    )
    if ($MetadataOnly) { $arguments += "--metadata-only" }
    if ($AllowUncommittedSourceForDevelopment) { $arguments += "--allow-uncommitted-source-for-development" }
    if ($AllowDevelopmentImageInputs) { $arguments += "--allow-development-image-inputs" }
    if (-not [string]::IsNullOrWhiteSpace($SigningKey)) {
        $arguments += @("--signing-key", $SigningKey, "--signing-public-key", $SigningPublicKey)
        if (-not [string]::IsNullOrWhiteSpace($SigningKeyId)) { $arguments += @("--signing-key-id", $SigningKeyId) }
    }
    foreach ($override in $Image) { $arguments += @("--image", $override) }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "SharpLabNext.BundleBuilder failed." }
}
finally {
    if ($null -eq $previousSourceIdentityMode) {
        Remove-Item Env:SHARPLABNEXT_SOURCE_IDENTITY_MODE -ErrorAction SilentlyContinue
    }
    else {
        $env:SHARPLABNEXT_SOURCE_IDENTITY_MODE = $previousSourceIdentityMode
    }
    Pop-Location
}

Write-Host "Offline bundle created at $OutputDirectory"
