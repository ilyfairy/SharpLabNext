[CmdletBinding()]
param(
    [string]$Version,
    [string]$Output = "artifacts/packages",
    [switch]$SkipRestore
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$packArguments = @("--output", $Output)
if ($Version) {
    $packArguments += @("--version", $Version)
}
if ($SkipRestore) {
    $packArguments += "--skip-restore"
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
$env:NUGET_XMLDOC_MODE = "skip"

Push-Location $root
try {
    & dotnet run "$PSScriptRoot/pack-sdk.cs" -- @packArguments
    if ($LASTEXITCODE -ne 0) {
        throw "SDK package build failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}
