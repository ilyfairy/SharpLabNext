[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$SkipRestore,
    [switch]$SkipFrontend,
    [switch]$SkipSchemas,
    [switch]$SkipValidation
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

function Invoke-Checked {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $FilePath $($Arguments -join ' ')"
    }
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
$env:NUGET_XMLDOC_MODE = "skip"

Push-Location $root
try {
    if (-not $SkipRestore) {
        Invoke-Checked -FilePath "dotnet" -Arguments @(
            "run", "eng/verify-ilsense-inputs.cs", "--",
            "--repository-root", $root,
            "--verify-restore"
        )
        Invoke-Checked -FilePath "dotnet" -Arguments @(
            "restore",
            "SharpLabNext.slnx",
            "--locked-mode"
        )
    }

    if (-not $SkipFrontend) {
        if (-not $SkipRestore) {
            Invoke-Checked -FilePath "npm" -Arguments @(
                "--prefix", "frontend",
                "ci",
                "--no-audit",
                "--no-fund"
            )
        }

        Invoke-Checked -FilePath "npm" -Arguments @("--prefix", "frontend", "run", "lint")
        Invoke-Checked -FilePath "npm" -Arguments @("--prefix", "frontend", "run", "build")
    }

    Invoke-Checked -FilePath "dotnet" -Arguments @(
        "build",
        "SharpLabNext.slnx",
        "--configuration", $Configuration,
        "--no-restore"
    )

    if (-not $SkipValidation -and -not $SkipSchemas) {
        Invoke-Checked -FilePath "node" -Arguments @(
            "--test",
            "eng/runtime-profile-channel-validation.test.mjs",
            "eng/runtime-wine-packages.test.mjs",
            "eng/prerequisite-cache.test.mjs",
            "eng/image-build-inputs.test.mjs",
            "eng/cppcli-netfx-sdk-extraction.test.mjs",
            "eng/build-images.test.mjs",
            "eng/build-wine-coreclr-operator.test.mjs",
            "eng/runtime-candidate-input-validation.test.mjs",
            "eng/runtime-candidate-environment.test.mjs",
            "eng/runtime-framework-installers.test.mjs",
            "eng/build-framework-matrix-context.test.mjs",
            "eng/build-framework-matrix-parent.test.mjs",
            "eng/committed-source-context.test.mjs",
            "eng/rebuild-runtime-candidate.test.mjs",
            "eng/create-runtime-framework-candidate-input.test.mjs",
            "eng/framework-prefix-matrix.test.mjs",
            "eng/runtime-matrix-deployment-bridge.test.mjs",
            "eng/runtime-matrix-generator.test.mjs",
            "eng/runtime-promotion-receipt-validation.test.mjs",
            "eng/wine-netfx-framework-preflight.test.mjs"
        )
        Invoke-Checked -FilePath "node" -Arguments @("eng/validate-bake-inputs.mjs")
        Invoke-Checked -FilePath "node" -Arguments @("eng/validate-schemas.mjs")
        Invoke-Checked -FilePath "node" -Arguments @("eng/validate-compose.mjs")
    }
}
finally {
    Pop-Location
}
