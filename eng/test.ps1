[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$SkipBuild,
    [switch]$SkipFrontend,
    [switch]$SkipSchemas,
    [switch]$ComposeE2E
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
    if (-not $SkipBuild) {
        $buildArguments = @{ Configuration = $Configuration }
        if ($SkipFrontend) {
            $buildArguments.SkipFrontend = $true
        }
        if ($SkipSchemas) {
            $buildArguments.SkipSchemas = $true
        }

        & "$PSScriptRoot/build.ps1" @buildArguments
    }

    Invoke-Checked -FilePath "dotnet" -Arguments @(
        "test",
        "SharpLabNext.slnx",
        "--configuration", $Configuration,
        "--no-build",
        "--no-restore"
    )

    Invoke-Checked -FilePath "dotnet" -Arguments @(
        "run",
        "eng/performance/runtime-performance-preflight.cs",
        "--", "--self-test"
    )

    Invoke-Checked -FilePath "dotnet" -Arguments @(
        "run",
        "eng/runtime-capability-preflight.cs",
        "--", "--self-test"
    )

    Invoke-Checked -FilePath "dotnet" -Arguments @(
        "run",
        "--project", "src/Tools/SharpLabNext.CompatibilityCli",
        "--configuration", $Configuration,
        "--no-build",
        "--", "validate",
        "--output", "artifacts/compatibility-report.json"
    )

    if (-not $SkipFrontend) {
        Invoke-Checked -FilePath "npm" -Arguments @(
            "--prefix", "frontend",
            "run", "test",
            "--if-present"
        )

        if ($ComposeE2E) {
            $e2eBaseUrl = if ([string]::IsNullOrWhiteSpace($env:SHARPLABNEXT_E2E_BASE_URL)) {
                "http://127.0.0.1:8080"
            }
            else {
                $env:SHARPLABNEXT_E2E_BASE_URL
            }
            Invoke-Checked -FilePath "dotnet" -Arguments @(
                "run", "eng/smoke/gateway-compose.cs", "--", $e2eBaseUrl, "--full"
            )
            Invoke-Checked -FilePath "dotnet" -Arguments @(
                "run", "eng/smoke/gateway-compose.cs", "--", $e2eBaseUrl, "--security"
            )

            Invoke-Checked -FilePath "npm" -Arguments @(
                "--prefix", "frontend",
                "run", "test:e2e"
            )

            Invoke-Checked -FilePath "dotnet" -Arguments @(
                "run",
                "eng/smoke/runtime-failures.cs",
                "--", $e2eBaseUrl
            )
        }
    }
    elseif ($ComposeE2E) {
        throw "ComposeE2E cannot be combined with SkipFrontend."
    }

    if ($SkipBuild -and -not $SkipSchemas) {
        Invoke-Checked -FilePath "node" -Arguments @(
            "--test",
            "eng/runtime-profile-channel-validation.test.mjs",
            "eng/runtime-candidate-input-validation.test.mjs",
            "eng/runtime-framework-installers.test.mjs",
            "eng/build-framework-matrix-context.test.mjs",
            "eng/build-framework-matrix-parent.test.mjs",
            "eng/runtime-promotion-image-binding.test.mjs",
            "eng/runtime-matrix-generator.test.mjs",
            "eng/runtime-promotion-receipt-validation.test.mjs",
            "eng/wine-netfx-framework-preflight.test.mjs",
            "eng/wine-prefix-layout.test.mjs"
        )
        Invoke-Checked -FilePath "node" -Arguments @("eng/validate-bake-inputs.mjs")
        Invoke-Checked -FilePath "node" -Arguments @("eng/validate-schemas.mjs")
    }
}
finally {
    Pop-Location
}
