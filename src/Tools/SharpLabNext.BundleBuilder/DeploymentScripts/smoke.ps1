[CmdletBinding()]
param(
    [string]$ReleaseRoot,
    [string]$ExpectedReleaseId,
    [int]$TimeoutSeconds = 180,
    [string]$BaseAddress
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ReleaseRoot)) {
    $ReleaseRoot = $PSScriptRoot
}
if (-not $ExpectedReleaseId) {
    $ExpectedReleaseId = [string](Get-Content -Raw -LiteralPath (Join-Path $ReleaseRoot 'bundle.json') | ConvertFrom-Json).releaseId
}
if ($ExpectedReleaseId -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$') { throw 'Expected release ID is unsafe.' }
if (-not $BaseAddress) {
    $port = if ($env:SHARPLABNEXT_HTTP_PORT) { $env:SHARPLABNEXT_HTTP_PORT } else { '8080' }
    $BaseAddress = "http://127.0.0.1:$port"
}
$BaseAddress = $BaseAddress.TrimEnd('/')
$compose = @(
    'compose', '--project-name', 'sharplabnext',
    '-f', (Join-Path $ReleaseRoot 'compose.prod.yaml'),
    '-f', (Join-Path $ReleaseRoot 'compose.generated.yaml')
)
$priorReleaseId = $env:SHARPLABNEXT_RELEASE_ID
$env:SHARPLABNEXT_RELEASE_ID = $ExpectedReleaseId
$deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
$lastFailure = 'No readiness attempt was made.'
try {
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        try {
            $expected = @(& docker @compose config --services)
            if ($LASTEXITCODE -ne 0 -or $expected.Count -eq 0) { throw 'Could not enumerate Compose services.' }
            $running = @(& docker @compose ps --status running --services)
            if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate running Compose services.' }
            $missing = @($expected | Where-Object { $_ -notin $running })
            if ($missing.Count -gt 0) { throw "Compose services are not running: $($missing -join ', ')" }

            $system = Invoke-RestMethod -Uri "$BaseAddress/api/v1/system" -TimeoutSec 10
            $catalog = Invoke-RestMethod -Uri "$BaseAddress/api/v1/catalog" -TimeoutSec 10
            if ($system.PSObject.Properties.Name -cnotcontains 'ReleaseId' -or
                [string]$system.ReleaseId -cne $ExpectedReleaseId) { throw 'Gateway release identity does not match.' }
            if ($catalog.PSObject.Properties.Name -cnotcontains 'ReleaseId' -or
                [string]$catalog.ReleaseId -cne $ExpectedReleaseId) { throw 'Catalog release identity does not match.' }
            Write-Host "SharpLabNext release $ExpectedReleaseId passed deployment smoke checks."
            return
        }
        catch {
            $lastFailure = $_.Exception.Message
            Start-Sleep -Seconds 2
        }
    }
}
finally {
    $env:SHARPLABNEXT_RELEASE_ID = $priorReleaseId
}
throw "SharpLabNext release $ExpectedReleaseId did not become ready in $TimeoutSeconds seconds: $lastFailure"
