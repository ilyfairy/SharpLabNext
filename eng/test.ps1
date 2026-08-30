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

    $testReferenceSetRoot = Join-Path $root ".tmp/test-coreclr-reference-sets"
    $testReferenceArchiveCache = Join-Path $root ".tmp/reference-package-cache"
    $testReferenceAppsettings = Join-Path $root ".tmp/test-coreclr-reference-sets.appsettings.json"
    $runtimeAssembly = Join-Path $root "src/RuntimeApi/SharpLabNext.Runtime/bin/$Configuration/netstandard2.1/SharpLab.Runtime.dll"
    if (-not (Test-Path -LiteralPath $runtimeAssembly -PathType Leaf)) { throw "The test reference-set materializer requires '$runtimeAssembly'." }
    Invoke-Checked -FilePath "dotnet" -Arguments @(
        "run", "eng/tools/materialize-coreclr-reference-sets.cs", "--",
        "--matrix", "profiles/runtime-matrix.json",
        "--lock", "profiles/lock.json",
        "--output", $testReferenceSetRoot,
        "--archive-cache", $testReferenceArchiveCache,
        "--appsettings-template", "src/Workers/Roslyn.Stable/SharpLabNext.Worker.Roslyn.Stable/appsettings.json",
        "--appsettings-output", $testReferenceAppsettings,
        "--runtime-assembly", $runtimeAssembly
    )
    $env:SHARPLABNEXT_TEST_CORECLR_REFERENCE_SETS = $testReferenceSetRoot
    $env:SHARPLABNEXT_NET10_REF_PATH = Join-Path $testReferenceSetRoot "net10-ref"
    $env:SHARPLABNEXT_NET11_REF_PATH = Join-Path $testReferenceSetRoot "net11-preview-ref"

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
        "eng/tools/runtime-capability-preflight.cs",
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
        $testFiles = @(Get-ChildItem -LiteralPath (Join-Path $root "eng/tests") -Filter "*.test.mjs" -File -Recurse | Sort-Object FullName | ForEach-Object FullName)
        Invoke-Checked -FilePath "node" -Arguments (@("--test") + $testFiles)
        Invoke-Checked -FilePath "node" -Arguments @("eng/validation/validate-bake-inputs.mjs")
        Invoke-Checked -FilePath "node" -Arguments @("eng/validation/validate-schemas.mjs")
    }
}
finally {
    Pop-Location
}
