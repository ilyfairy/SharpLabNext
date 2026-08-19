[CmdletBinding()]
param(
    [switch]$LoadImages,
    [string]$TrustedPublicKey,
    [string]$TrustedPublicKeySha256,
    [string]$ExpectedSigningKeyId,
    [switch]$TrustBundledPublicKey,
    [switch]$AllowUnsigned
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$bundlePath = Join-Path $root 'bundle.json'
$bundle = Get-Content -Raw -LiteralPath $bundlePath | ConvertFrom-Json

function Get-Sha256([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Trusted file does not exist: $Path"
    }
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
}

function Normalize-Sha256([string]$Value) {
    $normalized = $Value.Trim().ToLowerInvariant()
    if ($normalized.StartsWith('sha256:')) { $normalized = $normalized.Substring(7) }
    if ($normalized -notmatch '^[0-9a-f]{64}$') { throw 'A SHA-256 fingerprint must contain 64 lowercase hexadecimal characters.' }
    return $normalized
}

$signature = Join-Path $root 'checksums.sha256.sig'
$bundledPublicKey = Join-Path $root 'signing-public-key.pem'
if ($bundle.hasSignature) {
    if ($bundle.signatureAlgorithm -ne 'ed25519') { throw 'The bundle signature algorithm is unsupported.' }
    if ([string]::IsNullOrWhiteSpace([string]$bundle.signatureKeyId)) { throw 'The signed bundle has no signing key ID.' }
    if ([string]::IsNullOrWhiteSpace([string]$bundle.signingPublicKeySha256)) { throw 'The signed bundle has no public-key fingerprint.' }
    if (-not ((Test-Path -LiteralPath $signature -PathType Leaf) -and (Test-Path -LiteralPath $bundledPublicKey -PathType Leaf))) {
        throw 'Bundle signature files are incomplete.'
    }
    if ($ExpectedSigningKeyId -and $ExpectedSigningKeyId -cne [string]$bundle.signatureKeyId) {
        throw "Unexpected signing key ID '$($bundle.signatureKeyId)'."
    }

    $declaredFingerprint = Normalize-Sha256 ([string]$bundle.signingPublicKeySha256)
    $bundledFingerprint = Get-Sha256 $bundledPublicKey
    if ($bundledFingerprint -cne $declaredFingerprint) { throw 'Bundled public-key fingerprint does not match bundle.json.' }

    $verificationKey = $null
    if ($TrustedPublicKey) {
        $trustedFingerprint = Get-Sha256 ([IO.Path]::GetFullPath($TrustedPublicKey))
        if ($TrustedPublicKeySha256 -and $trustedFingerprint -cne (Normalize-Sha256 $TrustedPublicKeySha256)) {
            throw 'Trusted public key does not match the supplied out-of-band fingerprint.'
        }
        if ($trustedFingerprint -cne $declaredFingerprint) { throw 'Trusted public key does not match the bundle signing identity.' }
        $verificationKey = [IO.Path]::GetFullPath($TrustedPublicKey)
    }
    elseif ($TrustedPublicKeySha256) {
        if ((Normalize-Sha256 $TrustedPublicKeySha256) -cne $declaredFingerprint) {
            throw 'Bundle signing key does not match the supplied out-of-band fingerprint.'
        }
        $verificationKey = $bundledPublicKey
    }
    elseif ($TrustBundledPublicKey) {
        $verificationKey = $bundledPublicKey
    }
    else {
        throw 'Signed bundles require a trusted public key or out-of-band SHA-256 fingerprint.'
    }

    & openssl pkeyutl -verify -rawin -pubin -inkey $verificationKey -in (Join-Path $root 'checksums.sha256') -sigfile $signature | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Bundle signature verification failed.' }
}
else {
    if ((Test-Path -LiteralPath $signature) -or (Test-Path -LiteralPath $bundledPublicKey)) {
        throw 'Unsigned bundle contains inconsistent signature material.'
    }
    if (-not $AllowUnsigned) { throw 'Unsigned bundles require -AllowUnsigned.' }
}

$rootPrefix = [IO.Path]::GetFullPath($root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$seenPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
Get-Content -LiteralPath (Join-Path $root 'checksums.sha256') | ForEach-Object {
    if ($_ -notmatch '^([0-9a-f]{64})  (.+)$') { throw "Invalid checksum line: $_" }
    $relative = $Matches[2]
    if ([IO.Path]::IsPathRooted($relative) -or $relative.Contains('\') -or -not $seenPaths.Add($relative)) {
        throw "Unsafe or duplicate checksum path: $relative"
    }
    $path = [IO.Path]::GetFullPath((Join-Path $root $relative))
    if (-not $path.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Checksum path escapes the bundle: $relative" }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing bundle file: $relative" }
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
    if ($actual -cne $Matches[1]) { throw "Checksum mismatch: $relative" }
}

if ($LoadImages) {
    if (-not $bundle.containsImages) { throw 'This is a metadata-only bundle.' }
    docker image load --input (Join-Path $root 'images.tar') | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Docker image load failed.' }
}
Get-Content -LiteralPath (Join-Path $root 'images.expected') | ForEach-Object {
    if ($_ -notmatch '^([^ ]+) (sha256:[0-9a-f]{64})$') { throw "Invalid image identity line: $_" }
    $actual = (docker image inspect --format '{{.Id}}' $Matches[2]).Trim()
    if ($LASTEXITCODE -ne 0 -or $actual -cne $Matches[2]) { throw "Image identity mismatch: $($Matches[1])" }
}
docker compose -f (Join-Path $root 'compose.prod.yaml') -f (Join-Path $root 'compose.generated.yaml') config --quiet
if ($LASTEXITCODE -ne 0) { throw 'Compose validation failed.' }
Write-Host "Verified SharpLabNext release $($bundle.releaseId)."
