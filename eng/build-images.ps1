[CmdletBinding()]
param(
    [string]$ImagePrefix = "sharplabnext",
    [string]$SourceRevision,
    [string]$Target,
    [ValidateRange(1, 8)]
    [int]$MaxParallel = 5,
    [switch]$AllowUncommittedSourceForDevelopment,
    [switch]$AcceptMicrosoftLicenses,
    [switch]$Offline,
    [switch]$PlanOnly,
    [switch]$CacheProbe,
    [switch]$NoReuseExisting,
    [switch]$All
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
if (-not [string]::IsNullOrWhiteSpace($Target)) { $arguments += @("--target", $Target) }
if ($AllowUncommittedSourceForDevelopment) { $arguments += "--allow-uncommitted-source-for-development" }
if ($AcceptMicrosoftLicenses) { $arguments += "--accept-microsoft-licenses" }
if ($Offline) { $arguments += "--offline" }
if ($PlanOnly) { $arguments += "--plan-only" }
if ($CacheProbe) { $arguments += "--cache-probe" }
if ($NoReuseExisting) { $arguments += "--no-reuse-existing" }
if ($All) { $arguments += "--all" }

& node @arguments
if ($LASTEXITCODE -ne 0) { throw "SharpLabNext image build failed." }
