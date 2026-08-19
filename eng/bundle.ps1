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
    [switch]$SkipRestore,
    [switch]$SkipBuild,
    [switch]$MetadataOnly
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$lockPath = Join-Path $repositoryRoot "profiles/lock.json"
$baseImageManifestPath = Join-Path $repositoryRoot "profiles/base-images.json"
$releaseId = [string](& dotnet run (Join-Path $repositoryRoot "eng/read-release-id.cs") -- $lockPath | Select-Object -Last 1)
if ($LASTEXITCODE -ne 0) {
    throw "Could not read the release id from profiles/lock.json."
}
if ([string]::IsNullOrWhiteSpace($releaseId)) {
    throw "profiles/lock.json does not contain a releaseId."
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot "artifacts/bundles/sharplabnext-$releaseId"
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) {
    throw "Bundle output already exists: $OutputDirectory"
}
if ([string]::IsNullOrWhiteSpace($SigningKey) -ne [string]::IsNullOrWhiteSpace($SigningPublicKey)) {
    throw "SigningKey and SigningPublicKey are required together."
}
if (-not [string]::IsNullOrWhiteSpace($SigningKeyId) -and [string]::IsNullOrWhiteSpace($SigningKey)) {
    throw "SigningKeyId requires SigningKey and SigningPublicKey."
}
if ($AllowUncommittedSourceForDevelopment -and -not [string]::IsNullOrWhiteSpace($SigningKey)) {
    throw "AllowUncommittedSourceForDevelopment cannot be used for a signed release bundle."
}

Push-Location $repositoryRoot
try {
    $ilSenseArguments = @(
        "run", "eng/verify-ilsense-inputs.cs", "--",
        "--repository-root", $repositoryRoot,
        "--lock", $lockPath
    )
    if (-not $SkipRestore) {
        $ilSenseArguments += "--verify-restore"
    }
    & dotnet @ilSenseArguments
    if ($LASTEXITCODE -ne 0) { throw "ILSense source and dependency validation failed." }

    if (-not $SkipBuild) {
        dotnet run eng/verify-buildkit.cs
        if ($LASTEXITCODE -ne 0) { throw "BuildKit capability validation failed." }
    }

    if (-not $SkipRestore) {
        dotnet restore SharpLabNext.slnx --locked-mode
        if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed." }
        npm --prefix frontend ci --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw "npm ci failed." }
    }

    $sourceArguments = @(
        "run", "eng/resolve-source-provenance.cs", "--",
        "--repository-root", $repositoryRoot
    )
    if (-not [string]::IsNullOrWhiteSpace($SourceRevision)) {
        $sourceArguments += @("--source-revision", $SourceRevision)
    }
    if ($AllowUncommittedSourceForDevelopment) {
        $sourceArguments += "--allow-uncommitted-source-for-development"
    }
    $sourceOutput = @(& dotnet @sourceArguments)
    if ($LASTEXITCODE -ne 0) { throw "Source provenance validation failed." }
    $revisionPrefix = "SHARPLABNEXT_SOURCE_REVISION="
    $revisionLine = $sourceOutput |
        Where-Object { $_ -is [string] -and $_.StartsWith($revisionPrefix, [StringComparison]::Ordinal) } |
        Select-Object -Last 1
    if ([string]::IsNullOrWhiteSpace($revisionLine)) {
        throw "Source provenance resolver did not return a revision."
    }
    $SourceRevision = $revisionLine.Substring($revisionPrefix.Length)

    if (-not $SkipBuild) {
        $bakeArguments = @(
            "run", (Join-Path $repositoryRoot "eng/run-with-bake-environment.cs"), "--",
            "--lock", $lockPath,
            "--base-images", $baseImageManifestPath,
            "--source-revision", $SourceRevision,
            "--repository-root", $repositoryRoot,
            "--image-prefix", $ImagePrefix
        )
        if ($AllowUncommittedSourceForDevelopment) {
            $bakeArguments += "--allow-uncommitted-source-for-development"
        }
        $bakeArguments += @(
            "--",
            "docker", "buildx", "bake", "--file", "eng/bake.hcl"
        )
        & dotnet @bakeArguments
        if ($LASTEXITCODE -ne 0) { throw "docker buildx bake failed." }
    }

    $arguments = @(
        "run",
        "--project", "src/Tools/SharpLabNext.BundleBuilder",
        "--",
        "--output", $OutputDirectory,
        "--image-prefix", $ImagePrefix,
        "--source-revision", $SourceRevision
    )
    if ($MetadataOnly) { $arguments += "--metadata-only" }
    if ($AllowUncommittedSourceForDevelopment) {
        $arguments += "--allow-uncommitted-source-for-development"
    }
    if (-not [string]::IsNullOrWhiteSpace($SigningKey)) {
        if ([string]::IsNullOrWhiteSpace($SigningPublicKey)) { throw "SigningPublicKey is required with SigningKey." }
        $arguments += @("--signing-key", $SigningKey, "--signing-public-key", $SigningPublicKey)
        if (-not [string]::IsNullOrWhiteSpace($SigningKeyId)) { $arguments += @("--signing-key-id", $SigningKeyId) }
    }
    foreach ($override in $Image) { $arguments += @("--image", $override) }
    dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "SharpLabNext.BundleBuilder failed." }
}
finally {
    Pop-Location
}

Write-Host "Offline bundle created at $OutputDirectory"
