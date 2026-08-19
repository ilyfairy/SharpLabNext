[CmdletBinding()]
param(
    [ValidateSet("check", "resolve", "build", "test", "promote")]
    [string]$Stage = "check",

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$UpdaterArguments = @()
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot

Push-Location $repositoryRoot
try {
    & dotnet run --project src/Tools/SharpLabNext.ProfileUpdater -- $Stage @UpdaterArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Profile updater stage '$Stage' failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}
