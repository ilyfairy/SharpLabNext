Set-StrictMode -Version Latest

function Get-ReleaseDocument([string]$ReleaseRoot) {
    $document = Get-Content -Raw -LiteralPath (Join-Path $ReleaseRoot 'bundle.json') | ConvertFrom-Json
    $releaseId = [string]$document.releaseId
    if ($releaseId -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$') { throw 'Bundle release ID is unsafe.' }
    return $document
}

function Resolve-InstallRoot([string]$ConfiguredRoot) {
    if ([string]::IsNullOrWhiteSpace($ConfiguredRoot)) {
        $ConfiguredRoot = if ($env:SHARPLABNEXT_HOME) {
            $env:SHARPLABNEXT_HOME
        }
        elseif ($env:LOCALAPPDATA) {
            Join-Path $env:LOCALAPPDATA 'SharpLabNext'
        }
        else {
            Join-Path $HOME '.local/share/sharplabnext'
        }
    }
    return [IO.Path]::GetFullPath($ConfiguredRoot)
}

function Resolve-ContainerSecretFile([string]$ConfiguredPath, [string]$SecretName) {
    if ([string]::IsNullOrWhiteSpace($ConfiguredPath)) {
        throw "$SecretName path is empty."
    }

    try {
        $path = [IO.Path]::GetFullPath($ConfiguredPath)
    }
    catch {
        throw "$SecretName path is invalid: $ConfiguredPath"
    }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "$SecretName does not exist: $path"
    }

    try {
        $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        $stream.Dispose()
    }
    catch {
        throw "$SecretName is not readable: $path"
    }
    return $path
}

function Get-ReleasePointer([string]$InstallRoot, [string]$Name) {
    $path = Join-Path $InstallRoot $Name
    $item = Get-Item -Force -LiteralPath $path -ErrorAction SilentlyContinue
    if (-not $item) { return '' }
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Release pointer '$Name' must be a regular non-link file."
    }
    $value = [IO.File]::ReadAllText($item.FullName).Trim()
    if ($value -and $value -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$') { throw "Release pointer '$Name' is unsafe." }
    return $value
}

function Set-ReleasePointer([string]$InstallRoot, [string]$Name, [string]$Value) {
    $path = Join-Path $InstallRoot $Name
    $existing = Get-Item -Force -LiteralPath $path -ErrorAction SilentlyContinue
    if ($existing -and ($existing.PSIsContainer -or
            ($existing.Attributes -band [IO.FileAttributes]::ReparsePoint))) {
        throw "Release pointer '$Name' must be a regular non-link file."
    }
    $temporary = "$path.$([Guid]::NewGuid().ToString('N')).tmp"
    [IO.File]::WriteAllText($temporary, "$Value`n", [Text.UTF8Encoding]::new($false))
    Move-Item -Force -LiteralPath $temporary -Destination $path
}

function Remove-ReleasePointer([string]$InstallRoot, [string]$Name) {
    $path = Join-Path $InstallRoot $Name
    $item = Get-Item -Force -LiteralPath $path -ErrorAction SilentlyContinue
    if (-not $item) { return }
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Release pointer '$Name' must be a regular non-link file."
    }
    Remove-Item -Force -LiteralPath $path
    if (Test-Path -LiteralPath $path) { throw "Release pointer '$Name' could not be removed." }
}

function Set-ReleasePointerPair(
    [string]$InstallRoot,
    [string]$CandidateReleaseId,
    [string]$CurrentReleaseId,
    [string]$PreviousReleaseId
) {
    $previousPath = Join-Path $InstallRoot 'previous-release'
    $previousExisted = [bool](Get-Item -Force -LiteralPath $previousPath -ErrorAction SilentlyContinue)
    $previousChanged = $CurrentReleaseId -and $CurrentReleaseId -cne $CandidateReleaseId
    if ($previousChanged) {
        Set-ReleasePointer $InstallRoot 'previous-release' $CurrentReleaseId
    }
    try {
        Set-ReleasePointer $InstallRoot 'current-release' $CandidateReleaseId
    }
    catch {
        $currentFailure = $_
        if ($previousChanged) {
            try {
                if ($previousExisted) {
                    Set-ReleasePointer $InstallRoot 'previous-release' $PreviousReleaseId
                }
                else {
                    Remove-ReleasePointer $InstallRoot 'previous-release'
                }
            }
            catch {
                throw "Release current pointer update failed: $($currentFailure.Exception.Message) " +
                    "The original previous pointer could not be restored: $($_.Exception.Message)"
            }
        }
        throw $currentFailure
    }
}

function Assert-CurrentOnlyDeletionTree([string]$Root, [string]$Description) {
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Root)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
            $attributes = [IO.File]::GetAttributes($entry)
            if ($attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Current-only retention refused a reparse point or volume mount point inside $Description."
            }
            if ($attributes -band [IO.FileAttributes]::Directory) {
                $pending.Push($entry)
            }
        }
    }
}

function Get-CurrentOnlyRetentionPlan(
    [string]$InstallRoot,
    [string]$CandidateReleaseId,
    [string]$PreviousReleaseId,
    [string]$AdditionalPreviousReleaseId,
    [string]$ExpectedCurrentReleaseId,
    [string]$ExpectedPreviousReleaseId
) {
    if ($CandidateReleaseId -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$') {
        throw 'Current-only retention received an unsafe release ID.'
    }

    $currentRelease = Get-ReleasePointer $InstallRoot 'current-release'
    if ($currentRelease -cne $ExpectedCurrentReleaseId) {
        throw 'Current-only retention found an unexpected current-release pointer.'
    }
    $previousRelease = Get-ReleasePointer $InstallRoot 'previous-release'
    if ($previousRelease -cne $ExpectedPreviousReleaseId) {
        throw 'Current-only retention found an unexpected previous-release pointer.'
    }
    $releasesRoot = [IO.Path]::GetFullPath((Join-Path $InstallRoot 'releases'))
    $releasesItem = Get-Item -Force -LiteralPath $releasesRoot
    if (-not $releasesItem.PSIsContainer -or
        ($releasesItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Current-only retention refused an unsafe releases directory.'
    }
    $candidateRoot = [IO.Path]::GetFullPath((Join-Path $releasesRoot $CandidateReleaseId))
    if ([IO.Path]::GetDirectoryName($candidateRoot) -cne $releasesRoot) {
        throw 'Current-only retention candidate path escapes the releases directory.'
    }
    $candidateRootItem = Get-Item -Force -LiteralPath $candidateRoot
    if (-not $candidateRootItem.PSIsContainer -or
        ($candidateRootItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Current-only retention refused an unsafe candidate release directory.'
    }
    $candidateRollbackRoot = [IO.Path]::GetFullPath((Join-Path $candidateRoot 'rollback'))
    if ([IO.Path]::GetDirectoryName($candidateRollbackRoot) -cne $candidateRoot) {
        throw 'Current-only retention rollback path escapes the candidate release directory.'
    }
    $candidateRollbackItem = Get-Item -Force -LiteralPath $candidateRollbackRoot -ErrorAction SilentlyContinue
    if ($candidateRollbackItem -and (-not $candidateRollbackItem.PSIsContainer -or
            ($candidateRollbackItem.Attributes -band [IO.FileAttributes]::ReparsePoint))) {
        throw 'Current-only retention refused an unsafe candidate rollback directory.'
    }

    $hasPrevious = -not [string]::IsNullOrWhiteSpace($PreviousReleaseId)
    if ($hasPrevious) {
        if ($PreviousReleaseId -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$') {
            throw 'Current-only retention received an unsafe release ID.'
        }
        if ($PreviousReleaseId -ceq $CandidateReleaseId) {
            throw 'Current-only retention refused to delete the current release.'
        }
        $previousPointer = Join-Path $InstallRoot 'previous-release'
        $previousRoot = [IO.Path]::GetFullPath((Join-Path $releasesRoot $PreviousReleaseId))
        if ([IO.Path]::GetDirectoryName($previousRoot) -cne $releasesRoot) {
            throw 'Current-only retention predecessor path escapes the releases directory.'
        }
        $previousRootItem = Get-Item -Force -LiteralPath $previousRoot
        if (-not $previousRootItem.PSIsContainer -or
            ($previousRootItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Current-only retention refused an unsafe predecessor release directory.'
        }
    }

    $hasAdditionalPrevious = -not [string]::IsNullOrWhiteSpace($AdditionalPreviousReleaseId) -and
        $AdditionalPreviousReleaseId -cne $PreviousReleaseId
    if ($hasAdditionalPrevious) {
        if ($AdditionalPreviousReleaseId -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$') {
            throw 'Current-only retention received an unsafe release ID.'
        }
        if ($AdditionalPreviousReleaseId -ceq $CandidateReleaseId) {
            throw 'Current-only retention refused to delete the current release.'
        }
        $additionalPreviousRoot = [IO.Path]::GetFullPath((Join-Path $releasesRoot $AdditionalPreviousReleaseId))
        if ([IO.Path]::GetDirectoryName($additionalPreviousRoot) -cne $releasesRoot) {
            throw 'Current-only retention additional predecessor path escapes the releases directory.'
        }
        $additionalPreviousRootItem = Get-Item -Force -LiteralPath $additionalPreviousRoot
        if (-not $additionalPreviousRootItem.PSIsContainer -or
            ($additionalPreviousRootItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Current-only retention refused an unsafe additional predecessor release directory.'
        }
    }

    if ($candidateRollbackItem) {
        Assert-CurrentOnlyDeletionTree $candidateRollbackRoot 'the candidate rollback directory'
    }
    if ($hasPrevious) {
        Assert-CurrentOnlyDeletionTree $previousRoot 'the predecessor release directory'
    }
    if ($hasAdditionalPrevious) {
        Assert-CurrentOnlyDeletionTree $additionalPreviousRoot 'the additional predecessor release directory'
    }

    return [PSCustomObject]@{
        HasRollback = [bool]$candidateRollbackItem
        RollbackRoot = $candidateRollbackRoot
        HasPrevious = $hasPrevious
        PreviousRoot = if ($hasPrevious) { $previousRoot } else { $null }
        PreviousPointer = if ($hasPrevious) { $previousPointer } else { $null }
        HasAdditionalPrevious = $hasAdditionalPrevious
        AdditionalPreviousRoot = if ($hasAdditionalPrevious) { $additionalPreviousRoot } else { $null }
    }
}

function Test-CurrentOnlyInstalledRelease([string]$ReleaseRoot, [string]$ExpectedReleaseId) {
    $document = Get-ReleaseDocument $ReleaseRoot
    if ([string]$document.releaseId -cne $ExpectedReleaseId) {
        throw "Current-only retention predecessor '$ExpectedReleaseId' has a mismatched bundle release ID."
    }
    Test-InstalledDeployment $ReleaseRoot
    if ($document.hasSignature) {
        Invoke-ReleaseVerification -ReleaseRoot $ReleaseRoot -LoadImages -TrustBundledPublicKey -InstalledCopy
    }
    else {
        Invoke-ReleaseVerification -ReleaseRoot $ReleaseRoot -LoadImages -AllowUnsigned -InstalledCopy
    }
}

function Test-CurrentOnlyArtifactBackup([string]$BackupOwnerRoot, [string]$ExpectedReleaseId) {
    $rollbackRoot = Join-Path $BackupOwnerRoot 'rollback'
    $backupRoot = Join-Path $rollbackRoot 'artifact-data'
    $checksumPath = Join-Path $rollbackRoot 'artifact-data.sha256'
    $predecessorPath = Join-Path $rollbackRoot 'predecessor-release'
    $backupItem = Get-Item -Force -LiteralPath $backupRoot -ErrorAction SilentlyContinue
    $checksumItem = Get-Item -Force -LiteralPath $checksumPath -ErrorAction SilentlyContinue
    $predecessorItem = Get-Item -Force -LiteralPath $predecessorPath -ErrorAction SilentlyContinue
    if (-not $backupItem -and -not $checksumItem -and -not $predecessorItem) { return }
    if (-not $backupItem -or -not $backupItem.PSIsContainer -or
        -not $checksumItem -or $checksumItem.PSIsContainer -or
        -not $predecessorItem -or $predecessorItem.PSIsContainer) {
        throw 'Current-only retention found an incomplete Artifact Store rollback backup.'
    }

    $recorded = [IO.File]::ReadAllText($predecessorItem.FullName).Trim()
    if ($recorded -cne $ExpectedReleaseId) {
        throw 'Artifact backup predecessor does not match the current-only predecessor.'
    }
    $lines = @([IO.File]::ReadAllLines($checksumItem.FullName))
    if ($lines.Count -eq 0) { throw 'Artifact Store backup checksum manifest is empty.' }
    $prefix = [IO.Path]::GetFullPath($backupRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar
    foreach ($line in $lines) {
        if ($line -notmatch '^([0-9a-f]{64})  artifact-data/(.+)$') {
            throw "Invalid Artifact Store backup checksum: $line"
        }
        $relative = $Matches[2]
        $path = [IO.Path]::GetFullPath((Join-Path $backupRoot $relative))
        if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Artifact backup checksum path escapes its root.'
        }
        $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
        if ($actual -cne $Matches[1]) {
            throw "Artifact Store backup checksum mismatch: $relative"
        }
    }
}

function Test-CurrentOnlyRetentionSources(
    [string]$InstallRoot,
    [string]$CandidateReleaseId,
    [string]$PreviousReleaseId,
    [string]$AdditionalPreviousReleaseId,
    [string]$ExpectedCurrentReleaseId,
    [string]$ExpectedPreviousReleaseId
) {
    $plan = Get-CurrentOnlyRetentionPlan `
        $InstallRoot `
        $CandidateReleaseId `
        $PreviousReleaseId `
        $AdditionalPreviousReleaseId `
        $ExpectedCurrentReleaseId `
        $ExpectedPreviousReleaseId
    if ($plan.HasPrevious) {
        Test-CurrentOnlyInstalledRelease $plan.PreviousRoot $PreviousReleaseId
    }
    if ($plan.HasAdditionalPrevious) {
        Test-CurrentOnlyInstalledRelease $plan.AdditionalPreviousRoot $AdditionalPreviousReleaseId
    }
    if ($plan.HasRollback) {
        Test-CurrentOnlyArtifactBackup (Join-Path (Join-Path $InstallRoot 'releases') $CandidateReleaseId) $PreviousReleaseId
    }
    return $plan
}

function Remove-CurrentOnlyPreviousRelease(
    [string]$InstallRoot,
    [string]$CandidateReleaseId,
    [string]$PreviousReleaseId,
    [string]$AdditionalPreviousReleaseId
) {
    $plan = Get-CurrentOnlyRetentionPlan `
        $InstallRoot `
        $CandidateReleaseId `
        $PreviousReleaseId `
        $AdditionalPreviousReleaseId `
        $CandidateReleaseId `
        $PreviousReleaseId

    if ($plan.HasRollback) {
        Remove-Item -Recurse -Force -LiteralPath $plan.RollbackRoot
        if (Test-Path -LiteralPath $plan.RollbackRoot) {
            throw 'Current-only retention could not remove the candidate rollback directory.'
        }
    }
    if ($plan.HasPrevious) {
        Remove-Item -Recurse -Force -LiteralPath $plan.PreviousRoot
        if (Test-Path -LiteralPath $plan.PreviousRoot) {
            throw 'Current-only retention could not remove the predecessor release directory.'
        }
    }
    if ($plan.HasAdditionalPrevious) {
        Remove-Item -Recurse -Force -LiteralPath $plan.AdditionalPreviousRoot
        if (Test-Path -LiteralPath $plan.AdditionalPreviousRoot) {
            throw 'Current-only retention could not remove the additional predecessor release directory.'
        }
    }
    if ($plan.HasPrevious) {
        Remove-Item -Force -LiteralPath $plan.PreviousPointer
        if (Test-Path -LiteralPath $plan.PreviousPointer) {
            throw 'Current-only retention could not remove the previous-release pointer.'
        }
    }
}

function Invoke-ReleaseVerification {
    param(
        [string]$ReleaseRoot,
        [switch]$LoadImages,
        [string]$TrustedPublicKey,
        [string]$TrustedPublicKeySha256,
        [string]$ExpectedSigningKeyId,
        [switch]$TrustBundledPublicKey,
        [switch]$AllowUnsigned,
        [switch]$InstalledCopy
    )
    $arguments = @{}
    if ($LoadImages) { $arguments.LoadImages = $true }
    if ($TrustedPublicKey) { $arguments.TrustedPublicKey = $TrustedPublicKey }
    if ($TrustedPublicKeySha256) { $arguments.TrustedPublicKeySha256 = $TrustedPublicKeySha256 }
    if ($ExpectedSigningKeyId) { $arguments.ExpectedSigningKeyId = $ExpectedSigningKeyId }
    if ($TrustBundledPublicKey) { $arguments.TrustBundledPublicKey = $true }
    if ($AllowUnsigned) { $arguments.AllowUnsigned = $true }
    if ($InstalledCopy) { $arguments.InstalledCopy = $true }
    & (Join-Path $ReleaseRoot 'verify.ps1') @arguments
}

function Install-ReleaseAssets([string]$BundleRoot, [string]$InstallRoot, [string]$ReleaseId) {
    $releasesRoot = Join-Path $InstallRoot 'releases'
    $releaseRoot = Join-Path $releasesRoot $ReleaseId
    New-Item -ItemType Directory -Force -Path $releasesRoot | Out-Null
    if (Test-Path -LiteralPath $releaseRoot) {
        $incomingDigest = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $BundleRoot 'checksums.sha256')).Hash
        $installedDigest = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $releaseRoot 'checksums.sha256')).Hash
        if ($incomingDigest -cne $installedDigest) { throw "Release '$ReleaseId' is already installed with different content." }
        return $releaseRoot
    }

    $bundleFull = [IO.Path]::GetFullPath($BundleRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $releaseFull = [IO.Path]::GetFullPath($releaseRoot)
    if ($releaseFull.StartsWith($bundleFull, [StringComparison]::OrdinalIgnoreCase)) { throw 'Install destination cannot be inside the source bundle.' }
    $staging = Join-Path $releasesRoot ".$ReleaseId.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        New-Item -ItemType Directory -Path $staging | Out-Null
        Get-ChildItem -Force -LiteralPath $BundleRoot | ForEach-Object {
            Copy-Item -Recurse -Force -LiteralPath $_.FullName -Destination $staging
        }
        $deploymentFiles = @(
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
            'security/licenses/moby-profiles-Apache-2.0.txt'
        )
        $digestLines = foreach ($name in $deploymentFiles) {
            $localName = $name.Replace('/', [IO.Path]::DirectorySeparatorChar)
            $digest = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $staging $localName)).Hash.ToLowerInvariant()
            "$digest  $name"
        }
        [IO.File]::WriteAllLines((Join-Path $staging 'deployment.sha256'), $digestLines, [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $staging -Destination $releaseRoot
    }
    finally {
        if (Test-Path -LiteralPath $staging) { Remove-Item -Recurse -Force -LiteralPath $staging }
    }
    return $releaseRoot
}

function Test-InstalledDeployment([string]$ReleaseRoot) {
    $rootPrefix = [IO.Path]::GetFullPath($ReleaseRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    Get-Content -LiteralPath (Join-Path $ReleaseRoot 'deployment.sha256') | ForEach-Object {
        if ($_ -notmatch '^([0-9a-f]{64})  ((?:[A-Za-z0-9._-]+/)*[A-Za-z0-9._-]+)$') { throw "Invalid deployment checksum line: $_" }
        $relative = $Matches[2]
        $path = [IO.Path]::GetFullPath((Join-Path $ReleaseRoot $relative.Replace('/', [IO.Path]::DirectorySeparatorChar)))
        if (-not $path.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Installed deployment path escapes its release: $relative" }
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing installed deployment file: $relative" }
        $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
        if ($actual -cne $Matches[1]) { throw "Installed deployment checksum mismatch: $relative" }
    }
}

function Invoke-ReleaseComposeUp([string]$ReleaseRoot, [string]$ReleaseId) {
    $priorReleaseId = $env:SHARPLABNEXT_RELEASE_ID
    try {
        $env:SHARPLABNEXT_RELEASE_ID = $ReleaseId
        docker compose --project-name sharplabnext -f (Join-Path $ReleaseRoot 'compose.prod.yaml') -f (Join-Path $ReleaseRoot 'compose.generated.yaml') up -d --pull never --no-build --remove-orphans
        if ($LASTEXITCODE -ne 0) { throw "Compose failed to start release '$ReleaseId'." }
    }
    finally {
        $env:SHARPLABNEXT_RELEASE_ID = $priorReleaseId
    }
}

function Invoke-ReleaseComposeDown([string]$ReleaseRoot, [string]$ReleaseId) {
    $priorReleaseId = $env:SHARPLABNEXT_RELEASE_ID
    try {
        $env:SHARPLABNEXT_RELEASE_ID = $ReleaseId
        docker compose --project-name sharplabnext -f (Join-Path $ReleaseRoot 'compose.prod.yaml') -f (Join-Path $ReleaseRoot 'compose.generated.yaml') down --remove-orphans
        if ($LASTEXITCODE -ne 0) { throw "Compose failed to stop release '$ReleaseId'." }
    }
    finally {
        $env:SHARPLABNEXT_RELEASE_ID = $priorReleaseId
    }
}

function Invoke-ReleaseComposeStop([string]$ReleaseRoot, [string]$ReleaseId) {
    $priorReleaseId = $env:SHARPLABNEXT_RELEASE_ID
    try {
        $env:SHARPLABNEXT_RELEASE_ID = $ReleaseId
        docker compose --project-name sharplabnext -f (Join-Path $ReleaseRoot 'compose.prod.yaml') -f (Join-Path $ReleaseRoot 'compose.generated.yaml') stop
        if ($LASTEXITCODE -ne 0) { throw "Compose failed to quiesce release '$ReleaseId'." }
    }
    finally {
        $env:SHARPLABNEXT_RELEASE_ID = $priorReleaseId
    }
}

function Backup-ArtifactStore([string]$ReleaseRoot, [string]$ReleaseId, [string]$BackupOwnerRoot) {
    $rollbackRoot = Join-Path $BackupOwnerRoot 'rollback'
    $backupRoot = Join-Path $rollbackRoot 'artifact-data'
    $predecessorPath = Join-Path $rollbackRoot 'predecessor-release'
    if (Test-Path -LiteralPath $backupRoot) {
        $recorded = if (Test-Path -LiteralPath $predecessorPath) { (Get-Content -Raw -LiteralPath $predecessorPath).Trim() } else { '' }
        if ($recorded -cne $ReleaseId) { throw 'Artifact backup belongs to a different predecessor release.' }
        return
    }

    Invoke-ReleaseComposeStop $ReleaseRoot $ReleaseId
    $priorReleaseId = $env:SHARPLABNEXT_RELEASE_ID
    try {
        $env:SHARPLABNEXT_RELEASE_ID = $ReleaseId
        $containerId = (& docker compose --project-name sharplabnext -f (Join-Path $ReleaseRoot 'compose.prod.yaml') -f (Join-Path $ReleaseRoot 'compose.generated.yaml') ps --all -q artifact-store).Trim()
        if ($LASTEXITCODE -ne 0 -or -not $containerId) { throw 'The Artifact Store container is unavailable for backup.' }
    }
    finally {
        $env:SHARPLABNEXT_RELEASE_ID = $priorReleaseId
    }

    New-Item -ItemType Directory -Force -Path $rollbackRoot | Out-Null
    $staging = Join-Path $rollbackRoot ".artifact-data.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        New-Item -ItemType Directory -Path $staging | Out-Null
        docker cp "${containerId}:/var/lib/sharplabnext/." $staging
        if ($LASTEXITCODE -ne 0) { throw 'Artifact Store backup copy failed.' }
        $lines = Get-ChildItem -File -Recurse -LiteralPath $staging | Sort-Object FullName | ForEach-Object {
            $relative = [IO.Path]::GetRelativePath($staging, $_.FullName).Replace('\', '/')
            $digest = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
            "$digest  artifact-data/$relative"
        }
        if (@($lines).Count -eq 0) { throw 'Artifact Store backup is empty.' }
        Move-Item -LiteralPath $staging -Destination $backupRoot
        [IO.File]::WriteAllLines((Join-Path $rollbackRoot 'artifact-data.sha256'), $lines, [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText($predecessorPath, "$ReleaseId`n", [Text.UTF8Encoding]::new($false))
    }
    finally {
        if (Test-Path -LiteralPath $staging) { Remove-Item -Recurse -Force -LiteralPath $staging }
    }
}

function Restore-ArtifactStoreBackup([string]$BackupOwnerRoot, [string]$TargetReleaseRoot, [string]$ExpectedReleaseId) {
    $rollbackRoot = Join-Path $BackupOwnerRoot 'rollback'
    $backupRoot = Join-Path $rollbackRoot 'artifact-data'
    $predecessor = (Get-Content -Raw -LiteralPath (Join-Path $rollbackRoot 'predecessor-release')).Trim()
    if ($predecessor -cne $ExpectedReleaseId) { throw 'Artifact backup predecessor does not match the rollback target.' }
    Get-Content -LiteralPath (Join-Path $rollbackRoot 'artifact-data.sha256') | ForEach-Object {
        if ($_ -notmatch '^([0-9a-f]{64})  artifact-data/(.+)$') { throw "Invalid Artifact Store backup checksum: $_" }
        $path = [IO.Path]::GetFullPath((Join-Path $backupRoot $Matches[2]))
        $prefix = [IO.Path]::GetFullPath($backupRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Artifact backup checksum path escapes its root.' }
        $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
        if ($actual -cne $Matches[1]) { throw "Artifact Store backup checksum mismatch: $($Matches[2])" }
    }

    $volume = @(& docker volume ls --filter 'label=com.docker.compose.project=sharplabnext' --filter 'label=com.docker.compose.volume=artifact-data' --format '{{.Name}}')
    if ($LASTEXITCODE -ne 0 -or $volume.Count -ne 1) { throw 'Could not resolve the SharpLabNext Artifact Store volume.' }
    $imageLine = Get-Content -LiteralPath (Join-Path $TargetReleaseRoot 'images.expected') | Where-Object { $_ -match '^artifact-store ' }
    if (@($imageLine).Count -ne 1 -or $imageLine -notmatch '^artifact-store (sha256:[0-9a-f]{64})$') { throw 'Target release has no unique Artifact Store image.' }
    $imageId = $Matches[1]
    $containerUser = (& docker image inspect --format '{{.Config.User}}' $imageId).Trim()
    if ($LASTEXITCODE -ne 0 -or $containerUser -notmatch '^[0-9]+:[0-9]+$') { throw 'Artifact Store image has an unsupported runtime user.' }
    if ($backupRoot.Contains(',')) { throw 'Artifact backup path cannot contain a comma.' }
    $command = "find /var/lib/sharplabnext -mindepth 1 -maxdepth 1 -exec rm -rf -- {} + && cp -a /backup/. /var/lib/sharplabnext/ && chown -R $containerUser /var/lib/sharplabnext"
    docker run --rm --pull never --network none --read-only --security-opt no-new-privileges --user 0 --entrypoint /bin/sh --pids-limit 32 --mount "type=volume,source=$($volume[0]),target=/var/lib/sharplabnext" --mount "type=bind,source=$backupRoot,target=/backup,readonly" $imageId -c $command
    if ($LASTEXITCODE -ne 0) { throw 'Artifact Store backup restoration failed.' }
}

function Invoke-ReleaseSmoke([string]$ReleaseRoot, [string]$ReleaseId, [int]$TimeoutSeconds, [string]$BaseAddress) {
    $arguments = @{
        ReleaseRoot = $ReleaseRoot
        ExpectedReleaseId = $ReleaseId
        TimeoutSeconds = $TimeoutSeconds
    }
    if ($BaseAddress) { $arguments.BaseAddress = $BaseAddress }
    & (Join-Path $ReleaseRoot 'smoke.ps1') @arguments
}

function Restore-InstalledRelease([string]$ReleaseRoot, [int]$TimeoutSeconds, [string]$BaseAddress) {
    $document = Get-ReleaseDocument $ReleaseRoot
    Test-InstalledDeployment $ReleaseRoot
    if ($document.hasSignature) {
        Invoke-ReleaseVerification -ReleaseRoot $ReleaseRoot -LoadImages -TrustBundledPublicKey -InstalledCopy
    }
    else {
        Invoke-ReleaseVerification -ReleaseRoot $ReleaseRoot -LoadImages -AllowUnsigned -InstalledCopy
    }
    Invoke-ReleaseComposeUp $ReleaseRoot ([string]$document.releaseId)
    Invoke-ReleaseSmoke $ReleaseRoot ([string]$document.releaseId) $TimeoutSeconds $BaseAddress
}
