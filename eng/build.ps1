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
$previousSourceIdentityMode = [Environment]::GetEnvironmentVariable("SHARPLABNEXT_SOURCE_IDENTITY_MODE")
$env:SHARPLABNEXT_SOURCE_IDENTITY_MODE = "content"

Push-Location $root
try {
    if (-not $SkipRestore) {
        Invoke-Checked -FilePath "dotnet" -Arguments @(
            "run", "eng/tools/verify-ilsense-inputs.cs", "--",
            "--repository-root", $root,
            "--verify-restore",
            "--allow-missing-git"
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
        $testFiles = @(Get-ChildItem -LiteralPath (Join-Path $root "eng/tests") -Filter "*.test.mjs" -File -Recurse | Sort-Object FullName | ForEach-Object FullName)
        Invoke-Checked -FilePath "node" -Arguments (@("--test") + $testFiles)
        Invoke-Checked -FilePath "node" -Arguments @("eng/validation/validate-bake-inputs.mjs")
        Invoke-Checked -FilePath "node" -Arguments @("eng/validation/validate-schemas.mjs")
        Invoke-Checked -FilePath "node" -Arguments @("eng/validation/validate-compose.mjs")
    }
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
