[CmdletBinding()]
param(
    [string]$ImagePrefix = "sharplabnext",
    [string]$SourceRevision,
    [ValidateRange(1, 8)]
    [int]$MaxParallel = 4,
    [switch]$AllowUncommittedSourceForDevelopment,
    [switch]$AcceptMicrosoftLicenses,
    [switch]$Offline,
    [switch]$PlanOnly,
    [switch]$CacheProbe,
    [switch]$NoReuseExisting
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$arguments = @(
    (Join-Path $repositoryRoot "eng/build-images.mjs"),
    "--repository-root", $repositoryRoot,
    "--image-prefix", $ImagePrefix,
    "--max-parallel", [string]$MaxParallel
)
if (-not [string]::IsNullOrWhiteSpace($SourceRevision)) {
    $arguments += @("--source-revision", $SourceRevision)
}
if ($AllowUncommittedSourceForDevelopment) { $arguments += "--allow-uncommitted-source-for-development" }
if ($AcceptMicrosoftLicenses) { $arguments += "--accept-microsoft-licenses" }
if ($Offline) { $arguments += "--offline" }
if ($PlanOnly) { $arguments += "--plan-only" }
if ($CacheProbe) { $arguments += "--cache-probe" }
if ($NoReuseExisting) { $arguments += "--no-reuse-existing" }

& node @arguments
if ($LASTEXITCODE -ne 0) { throw "SharpLabNext image build failed." }
