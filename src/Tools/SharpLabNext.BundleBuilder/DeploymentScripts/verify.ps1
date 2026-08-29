[CmdletBinding()]
param(
    [switch]$LoadImages,
    [string]$TrustedPublicKey,
    [string]$TrustedPublicKeySha256,
    [string]$ExpectedSigningKeyId,
    [switch]$TrustBundledPublicKey,
    [switch]$AllowUnsigned,
    [switch]$InstalledCopy
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

function Test-CanonicalBundlePath([string]$Value) {
    return -not [string]::IsNullOrWhiteSpace($Value) -and
        -not [IO.Path]::IsPathRooted($Value) -and
        -not $Value.Contains('\') -and
        ($Value -split '/') -notcontains '' -and
        ($Value -split '/') -notcontains '.' -and
        ($Value -split '/') -notcontains '..'
}

function Get-ExactPropertyNames($Value) {
    return @($Value.PSObject.Properties.Name | Sort-Object)
}

function Get-OptionalProperty($Value, [string]$Name) {
    $property = $Value.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Test-ByteSequence([byte[]]$Left, [byte[]]$Right) {
    if ($Left.Length -ne $Right.Length) { return $false }
    for ($index = 0; $index -lt $Left.Length; $index++) {
        if ($Left[$index] -ne $Right[$index]) { return $false }
    }
    return $true
}

function Assert-CanonicalStringList($Values, [string]$Label) {
    $actual = [string[]]@($Values | ForEach-Object { [string]$_ })
    $expected = [string[]]@($actual)
    [Array]::Sort($expected, [StringComparer]::Ordinal)
    if ($actual.Length -ne $expected.Length) { throw "$Label is invalid." }
    for ($index = 0; $index -lt $actual.Length; $index++) {
        if ($actual[$index] -cne $expected[$index]) {
            throw "$Label is not canonically ordered and distinct."
        }
    }
}

function Invoke-CanonicalEd25519Verification(
    [string]$ContentPath,
    [string]$SignaturePath,
    [string]$PublicKeyPath,
    [string]$Label) {
    $signatureTextBytes = [IO.File]::ReadAllBytes($SignaturePath)
    if ($signatureTextBytes.Length -ne 89 -or $signatureTextBytes[88] -ne 10 -or
        $signatureTextBytes -contains [byte]13) {
        throw "$Label signature is not canonical 64-byte Ed25519 Base64 text."
    }
    $signatureText = [Text.Encoding]::ASCII.GetString($signatureTextBytes, 0, 88)
    if ($signatureText -cnotmatch '^[A-Za-z0-9+/]{86}==$') {
        throw "$Label signature is not canonical 64-byte Ed25519 Base64 text."
    }
    try {
        $signatureBytes = [Convert]::FromBase64String($signatureText)
    }
    catch [FormatException] {
        throw "$Label signature could not be decoded."
    }
    if ($signatureBytes.Length -ne 64 -or [Convert]::ToBase64String($signatureBytes) -cne $signatureText) {
        throw "$Label signature is not one canonical 64-byte Ed25519 signature."
    }
    $decodedPath = [IO.Path]::GetTempFileName()
    try {
        [IO.File]::WriteAllBytes($decodedPath, $signatureBytes)
        & openssl pkeyutl -verify -rawin -pubin -inkey $PublicKeyPath -in $ContentPath -sigfile $decodedPath | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "$Label signature verification failed." }
    }
    finally {
        Remove-Item -Force -LiteralPath $decodedPath -ErrorAction SilentlyContinue
    }
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

$deploymentChecksumPath = Join-Path $root 'deployment.sha256'
if ($InstalledCopy) {
    $deploymentChecksumItem = Get-Item -Force -LiteralPath $deploymentChecksumPath -ErrorAction SilentlyContinue
    if (-not $deploymentChecksumItem -or $deploymentChecksumItem.PSIsContainer -or
        ($deploymentChecksumItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'An installed copy requires a regular non-link deployment.sha256 file.'
    }
    $expectedDeploymentPaths = @(
        '.env',
        'bundle.json',
        'catalog.json',
        'lock.json',
        'profile-update-status.json',
        'compose.prod.yaml',
        'compose.generated.yaml',
        'github-oauth-client-secret.disabled',
        'images.expected',
        'checksums.sha256',
        'THIRD-PARTY-NOTICES.md',
        'security/README.md',
        'security/THIRD-PARTY-NOTICES.md',
        'security/inventory.json',
        'security/sharplabnext-runtime-job-v1.apparmor',
        'security/licenses/moby-profiles-Apache-2.0.txt')
    $deploymentLines = @([IO.File]::ReadAllLines($deploymentChecksumItem.FullName))
    if ($deploymentLines.Count -ne $expectedDeploymentPaths.Count) {
        throw 'Installed deployment checksum manifest does not contain the exact expected files.'
    }
    for ($index = 0; $index -lt $deploymentLines.Count; $index++) {
        $line = $deploymentLines[$index]
        if ($line -notmatch '^([0-9a-f]{64})  ((?:[A-Za-z0-9._-]+/)*[A-Za-z0-9._-]+)$') {
            throw "Invalid installed deployment checksum line: $line"
        }
        $relative = $Matches[2]
        if ($relative -cne $expectedDeploymentPaths[$index] -or
            ($relative -cne 'checksums.sha256' -and -not $seenPaths.Contains($relative))) {
            throw "Unexpected or unchecksummed installed deployment path: $relative"
        }
        $path = [IO.Path]::GetFullPath((Join-Path $root $relative.Replace('/', [IO.Path]::DirectorySeparatorChar)))
        if (-not $path.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Installed deployment path escapes its release: $relative"
        }
        $item = Get-Item -Force -LiteralPath $path -ErrorAction SilentlyContinue
        if (-not $item -or $item.PSIsContainer -or
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Missing or unsafe installed deployment file: $relative"
        }
        $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $item.FullName).Hash.ToLowerInvariant()
        if ($actual -cne $Matches[1]) { throw "Installed deployment checksum mismatch: $relative" }
    }
}

$allowedFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($relative in $seenPaths) { [void]$allowedFiles.Add($relative) }
[void]$allowedFiles.Add('checksums.sha256')
if ($bundle.hasSignature) { [void]$allowedFiles.Add('checksums.sha256.sig') }
if ($InstalledCopy) { [void]$allowedFiles.Add('deployment.sha256') }
foreach ($item in Get-ChildItem -LiteralPath $root -Force -Recurse) {
    if ($item.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint) -or $item.LinkType) {
        throw "Bundle contains a link or reparse point: $($item.FullName)"
    }
    if ($item -is [IO.FileInfo]) {
        $relative = [IO.Path]::GetRelativePath($root, $item.FullName).Replace('\', '/')
        if (-not $allowedFiles.Contains($relative)) { throw "Bundle contains an unchecksummed file: $relative" }
    }
}

$promotionRoot = Join-Path $root 'promotion-evidence'
if (Test-Path -LiteralPath $promotionRoot) {
    if (-not (Test-Path -LiteralPath $promotionRoot -PathType Container)) {
        throw 'Promotion evidence root is not a directory.'
    }
    $manifestPath = Join-Path $promotionRoot 'manifest.json'
    $manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
    if (($manifestBytes.Length -ge 3 -and
         $manifestBytes[0] -eq 0xEF -and $manifestBytes[1] -eq 0xBB -and $manifestBytes[2] -eq 0xBF) -or
        $manifestBytes -contains [byte]13) {
        throw 'Promotion evidence manifest must be UTF-8 without BOM and LF-only.'
    }
    $strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
    $manifest = $strictUtf8.GetString($manifestBytes) | ConvertFrom-Json
    $verificationManifestBytes = [IO.File]::ReadAllBytes((Join-Path $promotionRoot 'manifest.tsv'))
    if (($verificationManifestBytes.Length -ge 3 -and
         $verificationManifestBytes[0] -eq 0xEF -and $verificationManifestBytes[1] -eq 0xBB -and $verificationManifestBytes[2] -eq 0xBF) -or
        $verificationManifestBytes -contains [byte]13) {
        throw 'Promotion evidence verification manifest must be UTF-8 without BOM and LF-only.'
    }
    if (@(Get-ExactPropertyNames $manifest) -join ',' -cne 'buildSourceRevision,entries,promotedRuntimeIds,releaseSourceRevision,schemaVersion' -or
        $manifest.schemaVersion -ne 1 -or
        [string]$manifest.buildSourceRevision -notmatch '^[0-9a-f]{40,64}$' -or
        [string]$manifest.releaseSourceRevision -notmatch '^[0-9a-f]{40,64}$') {
        throw 'Promotion evidence manifest has an invalid identity.'
    }
    $runtimeIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($runtimeId in @($manifest.promotedRuntimeIds)) {
        if ([string]$runtimeId -notmatch '^[a-z0-9][a-z0-9.-]*$' -or -not $runtimeIds.Add([string]$runtimeId)) {
            throw 'Promotion evidence manifest has invalid or duplicate promoted runtime IDs.'
        }
    }
    if ($runtimeIds.Count -eq 0) { throw 'Promotion evidence manifest has no promoted runtime IDs.' }
    Assert-CanonicalStringList -Values @($manifest.promotedRuntimeIds) -Label 'Promotion evidence manifest runtime IDs'
    $bundlePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $entriesBySource = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $observedRuntimeIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $runtimeKinds = @{}
    foreach ($entry in @($manifest.entries)) {
        if (@(Get-ExactPropertyNames $entry) -join ',' -cne 'bundlePath,kind,profileIds,runtimeIds,sha256,sizeBytes,sourcePath' -or
            [string]$entry.kind -notin @('active-profile', 'candidate-profile', 'capability-evidence', 'performance-evidence', 'performance-policy', 'plan', 'plan-signature', 'plan-signature-public-key', 'preflight-profile', 'receipt', 'operator-receipt', 'operator-receipt-signature', 'operator-receipt-public-key', 'source-closure') -or
            -not (Test-CanonicalBundlePath ([string]$entry.sourcePath)) -or
            [string]$entry.bundlePath -cne "source/$($entry.sourcePath)" -or
            -not $bundlePaths.Add([string]$entry.bundlePath) -or
            -not $entriesBySource.TryAdd([string]$entry.sourcePath, $entry) -or
            [string]$entry.sha256 -notmatch '^sha256:[0-9a-f]{64}$' -or
            [int64]$entry.sizeBytes -lt 1) {
            throw 'Promotion evidence manifest has an invalid entry.'
        }
        $path = [IO.Path]::GetFullPath((Join-Path $promotionRoot ([string]$entry.bundlePath)))
        $promotionPrefix = [IO.Path]::GetFullPath($promotionRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $path.StartsWith($promotionPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-Sha256 $path) -cne ([string]$entry.sha256).Substring(7) -or
            ([IO.FileInfo]$path).Length -ne [int64]$entry.sizeBytes) {
            throw "Promotion evidence entry verification failed: $($entry.bundlePath)"
        }
        $profiles = @($entry.profileIds)
        $entryRuntimeIds = @($entry.runtimeIds)
        Assert-CanonicalStringList -Values $profiles -Label "Promotion evidence profile IDs for '$($entry.sourcePath)'"
        Assert-CanonicalStringList -Values $entryRuntimeIds -Label "Promotion evidence runtime IDs for '$($entry.sourcePath)'"
        if ($profiles.Count -ne $entryRuntimeIds.Count -or (($profiles -join ',') -cne ($entryRuntimeIds -join ','))) {
            throw 'Promotion evidence manifest has inconsistent profile/runtime bindings.'
        }
        foreach ($runtimeId in $entryRuntimeIds) {
            if (-not $runtimeIds.Contains([string]$runtimeId)) { throw 'Promotion evidence entry references an unknown runtime.' }
            [void]$observedRuntimeIds.Add([string]$runtimeId)
            if (-not $runtimeKinds.ContainsKey([string]$runtimeId)) { $runtimeKinds[[string]$runtimeId] = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal) }
            [void]$runtimeKinds[[string]$runtimeId].Add([string]$entry.kind)
        }
    }
    Assert-CanonicalStringList -Values @($manifest.entries | ForEach-Object { [string]$_.sourcePath }) -Label 'Promotion evidence manifest entries'
    foreach ($runtimeId in $runtimeIds) {
        if (-not $observedRuntimeIds.Contains($runtimeId)) { throw "Promotion evidence is missing runtime '$runtimeId'." }
        foreach ($kind in @('candidate-profile', 'plan', 'plan-signature', 'plan-signature-public-key', 'preflight-profile', 'receipt', 'capability-evidence', 'performance-evidence')) {
            if (-not $runtimeKinds[$runtimeId].Contains($kind)) { throw "Promotion evidence for '$runtimeId' is missing '$kind'." }
        }
    }
    $actualSourceFiles = Get-ChildItem -LiteralPath (Join-Path $promotionRoot 'source') -File -Recurse |
        ForEach-Object { [IO.Path]::GetRelativePath($promotionRoot, $_.FullName).Replace('\', '/') }
    $actualSourceFilesText = @($actualSourceFiles | Sort-Object) -join ','
    $expectedSourceFilesText = @($bundlePaths | Sort-Object) -join ','
    if ($actualSourceFilesText -cne $expectedSourceFilesText) {
        throw 'Promotion evidence contains missing or unlisted source files.'
    }

    function Add-PromotionExpectation([hashtable]$Expected, [string]$SourcePath, [string]$Kind, [string]$RuntimeId) {
        if (-not $Expected.ContainsKey($SourcePath)) {
            $Expected[$SourcePath] = [PSCustomObject]@{
                Kind = $Kind
                RuntimeIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            }
        }
        $current = $Expected[$SourcePath]
        if ($current.Kind -cne $Kind) { throw "Promotion evidence source '$SourcePath' has conflicting derived kinds." }
        [void]$current.RuntimeIds.Add($RuntimeId)
    }

    function Get-PromotionSourceEntry([string]$SourcePath) {
        if (-not $entriesBySource.ContainsKey($SourcePath)) { throw "Promotion evidence is missing '$SourcePath'." }
        return $entriesBySource[$SourcePath]
    }

    function Get-PromotionSourceJson([string]$SourcePath) {
        $entry = Get-PromotionSourceEntry $SourcePath
        $path = Join-Path $promotionRoot ([string]$entry.bundlePath)
        return Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
    }

    function Require-PromotionDigest([string]$SourcePath, [string]$ExpectedDigest) {
        if ($ExpectedDigest -notmatch '^sha256:[0-9a-f]{64}$') { throw "Promotion evidence has an invalid expected digest for '$SourcePath'." }
        $entry = Get-PromotionSourceEntry $SourcePath
        if ([string]$entry.sha256 -cne $ExpectedDigest) { throw "Promotion evidence digest binding mismatch for '$SourcePath'." }
    }

    function Get-StringSet($Values) {
        return @($Values | ForEach-Object { [string]$_ } | Sort-Object -Unique)
    }

    function Require-ExactStringSet($Actual, $Expected, [string]$Label) {
        $actualText = (Get-StringSet $Actual) -join ','
        $expectedText = (Get-StringSet $Expected) -join ','
        if ($actualText -cne $expectedText) { throw "$Label does not have the exact expected set." }
    }

    $derived = @{}
    $sharedSourceClosure = @(
        'deploy/images.json',
        'profiles/catalog/catalog.json',
        'profiles/lock.json',
        'profiles/runtime-matrix.json')
    $matrix = Get-PromotionSourceJson 'profiles/runtime-matrix.json'
    $matrixRuntimeIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    function Add-MatrixRuntime($Capability, [string]$RuntimeId) {
        if ($null -eq $Capability -or [string]$Capability.promotionState -ne 'verified') { return }
        if ([string]$Capability.promotionReceipt.path -cne "profiles/runtime-promotion-receipts/$RuntimeId.json" -or
            [string]$Capability.promotionReceipt.sha256 -notmatch '^sha256:[0-9a-f]{64}$' -or
            -not $matrixRuntimeIds.Add($RuntimeId)) {
            throw "Runtime matrix has an invalid or duplicate verified promotion '$RuntimeId'."
        }
    }
    foreach ($target in @($matrix.coreClr)) {
        Add-MatrixRuntime $target.linuxCapability ($target.id + '-linux-x64')
        Add-MatrixRuntime $target.wineCapability ('wine-' + $target.id + '-linux-x64')
    }
    Add-MatrixRuntime $matrix.mono.capability ([string]$matrix.mono.id)
    foreach ($target in @($matrix.framework.targets)) {
        Add-MatrixRuntime $target.capability ('wine-' + $target.id + '-linux-x64')
    }
    Require-ExactStringSet $runtimeIds $matrixRuntimeIds 'Promotion manifest runtime IDs derived from runtime matrix'
    foreach ($runtimeId in $runtimeIds) {
        foreach ($sourcePath in $sharedSourceClosure) {
            Add-PromotionExpectation $derived $sourcePath 'source-closure' $runtimeId
        }

        $activePath = "profiles/runtimes/$runtimeId.json"
        Add-PromotionExpectation $derived $activePath 'active-profile' $runtimeId
        $active = Get-PromotionSourceJson $activePath
        if ([string]$active.id -cne $runtimeId -or $null -eq $active.promotionReceipt) {
            throw "Promotion active profile '$runtimeId' is not bound to its receipt."
        }
        $receiptPath = [string]$active.promotionReceipt.path
        if ($receiptPath -cne "profiles/runtime-promotion-receipts/$runtimeId.json") {
            throw "Promotion active profile '$runtimeId' has a noncanonical receipt path."
        }
        Add-PromotionExpectation $derived $receiptPath 'receipt' $runtimeId
        Require-PromotionDigest $receiptPath ([string]$active.promotionReceipt.sha256)
        $receipt = Get-PromotionSourceJson $receiptPath
        if ([string]$receipt.profileId -cne $runtimeId -or $receipt.schemaVersion -ne 2) {
            throw "Promotion receipt '$runtimeId' has an invalid identity."
        }
        if ([string]$receipt.sourceRevision -cne [string]$manifest.buildSourceRevision) {
            throw "Promotion receipt '$runtimeId' has an invalid source revision."
        }
        Require-ExactStringSet $active.capabilities $receipt.checks.capability "Promotion active profile '$runtimeId' capabilities"

        $planPath = "profiles/runtime-promotion-plans/$runtimeId.json"
        Add-PromotionExpectation $derived $planPath 'plan' $runtimeId
        Require-PromotionDigest $planPath ([string]$receipt.planSha256)
        $planSignaturePath = [string]$receipt.planSignature.path
        if ($planSignaturePath -cne "$planPath.sig" -or
            [string]$receipt.planSignature.keyId -cne '__RUNTIME_PROMOTION_PLAN_KEY_ID__') {
            throw "Promotion receipt '$runtimeId' has an invalid plan-signature binding."
        }
        Add-PromotionExpectation $derived $planSignaturePath 'plan-signature' $runtimeId
        Require-PromotionDigest $planSignaturePath ([string]$receipt.planSignature.sha256)
        Add-PromotionExpectation $derived '__RUNTIME_PROMOTION_PLAN_PUBLIC_KEY_PATH__' 'plan-signature-public-key' $runtimeId
        Require-PromotionDigest '__RUNTIME_PROMOTION_PLAN_PUBLIC_KEY_PATH__' '__RUNTIME_PROMOTION_PLAN_PUBLIC_KEY_SHA256__'
        $plan = Get-PromotionSourceJson $planPath
        if ([string]$plan.profileId -cne $runtimeId -or $plan.schemaVersion -ne 1 -or
            [string]$plan.sourceRevision -cne [string]$manifest.buildSourceRevision) {
            throw "Promotion plan '$runtimeId' has an invalid identity."
        }
        $planEntry = Get-PromotionSourceEntry $planPath
        $planSignatureEntry = Get-PromotionSourceEntry $planSignaturePath
        $planPublicKeyEntry = Get-PromotionSourceEntry '__RUNTIME_PROMOTION_PLAN_PUBLIC_KEY_PATH__'
        Invoke-CanonicalEd25519Verification `
            (Join-Path $promotionRoot ([string]$planEntry.bundlePath)) `
            (Join-Path $promotionRoot ([string]$planSignatureEntry.bundlePath)) `
            (Join-Path $promotionRoot ([string]$planPublicKeyEntry.bundlePath)) `
            "Promotion plan '$runtimeId'"
        $candidatePath = "profiles/runtimes/candidates/$runtimeId.json"
        Add-PromotionExpectation $derived $candidatePath 'candidate-profile' $runtimeId
        Require-PromotionDigest $candidatePath ([string]$plan.profileSha256)
        $candidate = Get-PromotionSourceJson $candidatePath
        if ([string]$candidate.id -cne $runtimeId) {
            throw "Promotion candidate profile '$runtimeId' has an invalid identity."
        }
        Require-ExactStringSet $plan.capabilities $receipt.checks.capability "Promotion plan '$runtimeId' capabilities"
        $requiresWineOperator = switch ([string]$plan.family) {
            'coreclr-wine' { $true; break }
            'netfx-clr-wine' { $true; break }
            'coreclr' { $false; break }
            'mono' { $false; break }
            default { throw "Promotion plan '$runtimeId' has an unsupported runtime family." }
        }
        $operatorEvidenceKinds = @('operator-receipt', 'operator-receipt-signature', 'operator-receipt-public-key')
        $planWineOperator = Get-OptionalProperty $plan 'wineOperator'
        $receiptWineOperator = Get-OptionalProperty $receipt 'wineOperator'
        if ($requiresWineOperator) {
            if ($null -eq $planWineOperator -or $null -eq $receiptWineOperator) {
                throw "Promotion Wine runtime '$runtimeId' is missing its required Wine operator binding."
            }
            foreach ($operatorEvidenceKind in $operatorEvidenceKinds) {
                if (-not $runtimeKinds[$runtimeId].Contains($operatorEvidenceKind)) {
                    throw "Promotion Wine runtime '$runtimeId' is missing required Wine operator evidence."
                }
            }
            $operatorReceiptPath = [string]$receiptWineOperator.receiptPath
            $operatorSignaturePath = [string]$receiptWineOperator.signaturePath
            $operatorKeyId = 'sha256:16cdb3dd05ddc65de942187de063606b06c7c56c60e1a3394d166724d649e5a1'
            if ($operatorReceiptPath -cne "profiles/runtime-operator-receipts/wine-coreclr-$($manifest.buildSourceRevision).json" -or
                $operatorSignaturePath -cne "$operatorReceiptPath.sig" -or
                [string]$receiptWineOperator.keyId -cne $operatorKeyId) {
                throw "Promotion receipt '$runtimeId' has an invalid Wine operator receipt binding."
            }
            Add-PromotionExpectation $derived $operatorReceiptPath 'operator-receipt' $runtimeId
            Add-PromotionExpectation $derived $operatorSignaturePath 'operator-receipt-signature' $runtimeId
            Add-PromotionExpectation $derived 'eng/profiles/trust/wine-coreclr-operator-receipt-public.pem' 'operator-receipt-public-key' $runtimeId
            Require-PromotionDigest $operatorReceiptPath ([string]$receiptWineOperator.receiptSha256)
            Require-PromotionDigest $operatorSignaturePath ([string]$receiptWineOperator.signatureSha256)
            Require-PromotionDigest 'eng/profiles/trust/wine-coreclr-operator-receipt-public.pem' 'sha256:890cb122b7d50f2f437cf47ac71a57c624fc96bbef75dac6e187290742d01b3f'
            $operatorReceipt = Get-PromotionSourceJson $operatorReceiptPath
            $operatorProperties = @(
                'imageId', 'keyId', 'lineageKind', 'receiptPath', 'receiptSha256', 'reference',
                'signaturePath', 'signatureSha256', 'sizeBytes', 'sourceRevision', 'sourceTree')
            if ([string]$receiptWineOperator.lineageKind -ne 'direct') {
                $operatorProperties += @('intermediaryImageId', 'intermediaryReference', 'intermediarySizeBytes')
            }
            $operatorProperties = @($operatorProperties | Sort-Object)
            if ((@(Get-ExactPropertyNames $receiptWineOperator) -join ',') -cne ($operatorProperties -join ',') -or
                (@(Get-ExactPropertyNames $planWineOperator) -join ',') -cne ($operatorProperties -join ',') -or
                $operatorReceipt.schemaVersion -ne 1 -or [string]$operatorReceipt.keyId -cne $operatorKeyId -or
                [string]$operatorReceipt.source.revision -cne [string]$manifest.buildSourceRevision -or
                [string]$planWineOperator.keyId -cne [string]$operatorReceipt.keyId -or
                [string]$planWineOperator.reference -cne [string]$operatorReceipt.operator.reference -or
                [string]$planWineOperator.imageId -cne [string]$operatorReceipt.operator.imageId -or
                [int64]$planWineOperator.sizeBytes -ne [int64]$operatorReceipt.operator.sizeBytes -or
                [string]$planWineOperator.sourceRevision -cne [string]$operatorReceipt.source.revision -or
                [string]$planWineOperator.sourceTree -cne [string]$operatorReceipt.source.tree) {
                throw "Promotion receipt '$runtimeId' has an invalid Wine operator identity."
            }
            foreach ($property in $operatorProperties) {
                if ([string]$planWineOperator.$property -cne [string]$receiptWineOperator.$property) {
                    throw "Promotion receipt '$runtimeId' has a plan/operator binding mismatch."
                }
            }
            $operatorReceiptEntry = Get-PromotionSourceEntry $operatorReceiptPath
            $operatorSignatureEntry = Get-PromotionSourceEntry $operatorSignaturePath
            $operatorPublicKeyEntry = Get-PromotionSourceEntry 'eng/profiles/trust/wine-coreclr-operator-receipt-public.pem'
            Invoke-CanonicalEd25519Verification `
                (Join-Path $promotionRoot ([string]$operatorReceiptEntry.bundlePath)) `
                (Join-Path $promotionRoot ([string]$operatorSignatureEntry.bundlePath)) `
                (Join-Path $promotionRoot ([string]$operatorPublicKeyEntry.bundlePath)) `
                "Wine operator receipt '$runtimeId'"
        }
        else {
            if ($null -ne $planWineOperator -or $null -ne $receiptWineOperator) {
                throw "Promotion non-Wine runtime '$runtimeId' must not declare a Wine operator binding."
            }
            foreach ($operatorEvidenceKind in $operatorEvidenceKinds) {
                if ($runtimeKinds[$runtimeId].Contains($operatorEvidenceKind)) {
                    throw "Promotion non-Wine runtime '$runtimeId' must not retain Wine operator evidence."
                }
            }
        }

        $preflightPath = [string]$plan.preflightProfile.path
        if ($preflightPath -cne "profiles/runtime-promotion-plans/$runtimeId.profile.json") {
            throw "Promotion plan '$runtimeId' has a noncanonical preflight profile path."
        }
        Add-PromotionExpectation $derived $preflightPath 'preflight-profile' $runtimeId
        Require-PromotionDigest $preflightPath ([string]$plan.preflightProfile.sha256)
        $preflight = Get-PromotionSourceJson $preflightPath
        if ([string]$preflight.id -cne $runtimeId) { throw "Promotion preflight profile '$runtimeId' has an invalid identity." }

        if ($null -eq $receipt.performance -or $null -eq $plan.performance -or
            [string]$receipt.performance.policyPath -cne [string]$plan.performance.policyPath -or
            [string]$receipt.performance.policySha256 -cne [string]$plan.performance.policySha256 -or
            [string]$receipt.performance.evidencePath -cne [string]$plan.performance.evidencePath) {
            throw "Promotion performance bindings disagree for '$runtimeId'."
        }
        $policyPath = [string]$receipt.performance.policyPath
        if ($policyPath -notmatch '^profiles/runtime-performance-policies/[a-z0-9][a-z0-9._-]*\.json$') {
            throw "Promotion receipt '$runtimeId' has a noncanonical performance policy path."
        }
        Add-PromotionExpectation $derived $policyPath 'performance-policy' $runtimeId
        Require-PromotionDigest $policyPath ([string]$receipt.performance.policySha256)
        $performancePath = [string]$receipt.performance.evidencePath
        if ($performancePath -cne "profiles/runtime-promotion-evidence/$runtimeId/performance.json") {
            throw "Promotion receipt '$runtimeId' has a noncanonical performance evidence path."
        }
        Add-PromotionExpectation $derived $performancePath 'performance-evidence' $runtimeId
        Require-PromotionDigest $performancePath ([string]$receipt.performance.evidenceSha256)

        $checkPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $checkCapabilities = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($check in @($receipt.checks)) {
            $capability = [string]$check.capability
            if ($capability -notin @('run', 'jit-asm', 'inspection', 'execution-flow') -or
                -not $checkCapabilities.Add($capability)) {
                throw "Promotion receipt '$runtimeId' has duplicate or invalid capability evidence."
            }
            $evidencePath = [string]$check.evidencePath
            if ($evidencePath -cne "profiles/runtime-promotion-evidence/$runtimeId/$capability.json" -or
                -not $checkPaths.Add($evidencePath)) {
                throw "Promotion receipt '$runtimeId' has a noncanonical capability evidence path."
            }
            Add-PromotionExpectation $derived $evidencePath 'capability-evidence' $runtimeId
            Require-PromotionDigest $evidencePath ([string]$check.evidenceSha256)
        }
    }
    if ($derived.Count -ne $entriesBySource.Count) { throw 'Promotion evidence has extra or missing derived entries.' }
    foreach ($sourcePath in $derived.Keys) {
        $expected = $derived[$sourcePath]
        $entry = Get-PromotionSourceEntry $sourcePath
        if ([string]$entry.kind -cne $expected.Kind) { throw "Promotion evidence kind is not derivable for '$sourcePath'." }
        Require-ExactStringSet $entry.profileIds $expected.RuntimeIds "Promotion evidence profile bindings for '$sourcePath'"
        Require-ExactStringSet $entry.runtimeIds $expected.RuntimeIds "Promotion evidence runtime bindings for '$sourcePath'"
    }

    # The TSV is an exact, deterministic projection of the signed JSON manifest.
    # Compare bytes so a forged row, blank line, reordering, or missing final LF cannot be ignored.
    $expectedVerificationManifestLines = [Collections.Generic.List[string]]::new()
    [void]$expectedVerificationManifestLines.Add('schemaVersion' + [char]9 + ([int]$manifest.schemaVersion).ToString([Globalization.CultureInfo]::InvariantCulture))
    [void]$expectedVerificationManifestLines.Add('buildSourceRevision' + [char]9 + [string]$manifest.buildSourceRevision)
    [void]$expectedVerificationManifestLines.Add('releaseSourceRevision' + [char]9 + [string]$manifest.releaseSourceRevision)
    [void]$expectedVerificationManifestLines.Add('manifestJsonSha256' + [char]9 + 'sha256:' + (Get-Sha256 $manifestPath))
    [void]$expectedVerificationManifestLines.Add('promotedRuntimeIds' + [char]9 + (@($manifest.promotedRuntimeIds) -join ','))
    foreach ($entry in @($manifest.entries)) {
        $profileIds = @($entry.profileIds)
        $entryRuntimeIds = @($entry.runtimeIds)
        [void]$expectedVerificationManifestLines.Add([string]::Join(
            [char]9,
            @(
                'entry',
                [string]$entry.kind,
                $(if ($profileIds.Count -eq 0) { '-' } else { $profileIds -join ',' }),
                $(if ($entryRuntimeIds.Count -eq 0) { '-' } else { $entryRuntimeIds -join ',' }),
                [string]$entry.sourcePath,
                [string]$entry.bundlePath,
                [string]$entry.sha256,
                ([int64]$entry.sizeBytes).ToString([Globalization.CultureInfo]::InvariantCulture))))
    }
    $expectedVerificationManifestBytes = $strictUtf8.GetBytes((($expectedVerificationManifestLines -join "`n") + "`n"))
    if (-not (Test-ByteSequence $expectedVerificationManifestBytes $verificationManifestBytes)) {
        throw 'Promotion evidence JSON and verification manifests disagree.'
    }
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
Push-Location $root
try {
    docker compose config --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Compose validation failed.' }
}
finally {
    Pop-Location
}
Write-Host "Verified SharpLabNext release $($bundle.releaseId)."
