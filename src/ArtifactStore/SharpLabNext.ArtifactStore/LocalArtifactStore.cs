using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.Contracts;

namespace SharpLabNext.ArtifactStore;

internal sealed class LocalArtifactStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = ContractJson.CreateCanonicalSerializerOptions();
    private static readonly Action<ILogger, string, Exception?> AbandonedStagingCleanupFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1001, nameof(AbandonedStagingCleanupFailed)), "Could not remove abandoned Artifact Store staging directory {Directory}");
    private static readonly Action<ILogger, string, Exception?> ArtifactDeleteFailed =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(1002, nameof(ArtifactDeleteFailed)), "Could not delete collected artifact directory {Directory}");
    private static readonly Action<ILogger, string, Exception?> ContentDeleteFailed =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(1003, nameof(ContentDeleteFailed)), "Could not delete collected content file {File}");
    private readonly ArtifactStoreOptions _options;
    private readonly ILogger<LocalArtifactStore> _logger;
    private readonly string _root;
    private readonly string _contentsRoot;
    private readonly string _artifactsRoot;
    private readonly string _temporaryRoot;
    private readonly ArtifactStoreDatabase _database;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private bool _initialized;

    public LocalArtifactStore(IOptions<ArtifactStoreOptions> options, IWebHostEnvironment environment, ILogger<LocalArtifactStore> logger)
    {
        _options = options.Value;
        _options.Validate();
        _logger = logger;
        _root = StorageSafety.ResolveRoot(environment.ContentRootPath, _options.RootPath);
        _contentsRoot = Path.Combine(_root, "contents", ArtifactStoreProtocol.DigestAlgorithm);
        _artifactsRoot = Path.Combine(_root, "artifacts", ArtifactStoreProtocol.DigestAlgorithm);
        _temporaryRoot = Path.Combine(_root, "tmp");
        var metadataRoot = Path.Combine(_root, "metadata");
        CreateOwnedDirectory(_contentsRoot);
        CreateOwnedDirectory(_artifactsRoot);
        CreateOwnedDirectory(_temporaryRoot);
        CreateOwnedDirectory(metadataRoot);
        _database = new ArtifactStoreDatabase(Path.Combine(metadataRoot, "artifacts.db"));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
            RemoveAbandonedTemporaryDirectories();
            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public async Task<PutContentResponse> PutContentAsync(ContentRef expectedContentRef, Stream content, long? declaredSize, TimeSpan? timeToLive, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        _ = ArtifactStoreProtocol.GetDigest(expectedContentRef);
        if (declaredSize is < 0)
        {
            throw new ArtifactValidationException("Declared content size cannot be negative.");
        }

        if (declaredSize > _options.MaxContentBytes)
        {
            throw new ArtifactLimitExceededException("Content exceeds the configured size limit.");
        }

        var now = DateTimeOffset.UtcNow;
        var expiresAt = ResolveExpiry(now, timeToLive);
        var stagingDirectory = CreateStagingDirectory();
        try
        {
            var staged = await StageContentAsync(content, expectedContentRef, declaredSize, _options.MaxContentBytes, Path.Combine(stagingDirectory, "content.tmp"), cancellationToken).ConfigureAwait(false);

            await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var published = await PublishContentAsync(staged, cancellationToken).ConfigureAwait(false);
                await _database.UpsertContentAsync(published, now, expiresAt, cancellationToken).ConfigureAwait(false);
                return new PutContentResponse(expectedContentRef, staged.Size, expiresAt, published.AlreadyExisted);
            }
            finally
            {
                _mutationGate.Release();
            }
        }
        finally
        {
            DeleteDirectoryIfPresent(stagingDirectory);
        }
    }

    public async Task<PutArtifactResponse> PutArtifactAsync(ArtifactRef expectedArtifactRef, ArtifactManifest manifest, IReadOnlyList<ArtifactUploadSource> uploads, TimeSpan? timeToLive, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        ValidateManifest(expectedArtifactRef, manifest);
        if (uploads.Count > _options.MaxArtifactFiles || manifest.Files.Count > _options.MaxArtifactFiles)
        {
            throw new ArtifactLimitExceededException("Artifact contains too many files.");
        }

        Dictionary<string, ArtifactUploadSource> uploadByPath;
        try
        {
            uploadByPath = uploads.ToDictionary(upload => ArtifactPath.Normalize(upload.Path), StringComparer.Ordinal);
        }
        catch (ArgumentException exception)
        {
            throw new ArtifactValidationException("Artifact uploads contain an invalid or duplicate path.", exception);
        }

        var manifestFiles = new Dictionary<string, ArtifactFileDescriptor>(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            string normalizedPath;
            try
            {
                normalizedPath = ArtifactPath.Normalize(file.Path);
                _ = ArtifactStoreProtocol.ParseContentRef(file.Digest);
            }
            catch (ArgumentException exception)
            {
                throw new ArtifactValidationException("Artifact manifest contains an invalid file path or digest.", exception);
            }

            if (!manifestFiles.TryAdd(normalizedPath, file))
            {
                throw new ArtifactValidationException($"Artifact path '{normalizedPath}' is duplicated.");
            }
        }

        if (manifestFiles.Count != uploadByPath.Count || manifestFiles.Keys.Any(path => !uploadByPath.ContainsKey(path)))
        {
            throw new ArtifactValidationException("Uploads must contain exactly one stream for every manifest file.");
        }

        var now = DateTimeOffset.UtcNow;
        var expiresAt = ResolveExpiry(now, timeToLive);
        var stagingDirectory = CreateStagingDirectory();
        var stagedContents = new List<(ArtifactFileDescriptor File, string Path, StagedContent Content)>();
        long totalSize = 0;
        try
        {
            var fileIndex = 0;
            foreach (var (path, file) in manifestFiles.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (file.Size < 0 || file.Size > _options.MaxContentBytes)
                {
                    throw new ArtifactLimitExceededException($"Artifact file '{path}' exceeds the configured size limit.");
                }

                Stream stream;
                try
                {
                    stream = uploadByPath[path].OpenReadStream();
                }
                catch (Exception exception) when (exception is not ArtifactStoreException)
                {
                    throw new ArtifactValidationException($"Artifact file '{path}' could not be opened.", exception);
                }

                await using (stream.ConfigureAwait(false)) {
                    var expectedContentRef = ArtifactStoreProtocol.ParseContentRef(file.Digest);
                    var staged = await StageContentAsync(stream, expectedContentRef, file.Size, _options.MaxContentBytes, Path.Combine(stagingDirectory, $"content-{fileIndex++}.tmp"), cancellationToken).ConfigureAwait(false);
                    stagedContents.Add((file, path, staged));
                    try
                    {
                        totalSize = checked(totalSize + staged.Size);
                    }
                    catch (OverflowException exception)
                    {
                        throw new ArtifactLimitExceededException("Artifact size overflowed the supported range.", exception);
                    }

                    if (totalSize > _options.MaxArtifactBytes)
                    {
                        throw new ArtifactLimitExceededException("Artifact exceeds the configured total size limit.");
                    }
                }
            }

            await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var publishedContents = new List<PublishedContent>(stagedContents.Count);
                foreach (var staged in stagedContents)
                    publishedContents.Add(await PublishContentAsync(staged.Content, cancellationToken).ConfigureAwait(false));

                var entries = stagedContents.Select(item => new ArtifactBundleEntry(item.Path, item.Content.Size, item.File.Digest, item.File.Role, item.Content.ContentRef)).ToArray();
                var descriptor = new ArtifactBundleDescriptor(manifest, entries);
                var artifactRelativePath = await PublishArtifactDirectoryAsync(descriptor, stagingDirectory, cancellationToken).ConfigureAwait(false);
                var inserted = await _database.CommitArtifactAsync(descriptor, artifactRelativePath, publishedContents, totalSize, now, expiresAt, cancellationToken).ConfigureAwait(false);
                var stored = await _database.GetArtifactAsync(expectedArtifactRef, now, cancellationToken).ConfigureAwait(false) ?? throw new ArtifactCorruptedException("Artifact metadata disappeared immediately after commit.");
                return new PutArtifactResponse(expectedArtifactRef, totalSize, stored.ExpiresAt, !inserted);
            }
            finally
            {
                _mutationGate.Release();
            }
        }
        finally
        {
            DeleteDirectoryIfPresent(stagingDirectory);
        }
    }

    public async Task<ArtifactBundleDescriptor?> GetArtifactAsync(ArtifactRef artifactRef, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        _ = ArtifactStoreProtocol.GetDigest(artifactRef);
        var metadata = await _database.GetArtifactAsync(artifactRef, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            return null;
        }

        await VerifyArtifactMetadataAsync(metadata, cancellationToken).ConfigureAwait(false);
        return metadata.Descriptor;
    }

    public async Task<VerifiedContent> OpenContentReadAsync(ContentRef contentRef, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        _ = ArtifactStoreProtocol.GetDigest(contentRef);
        var metadata = await _database.GetContentAsync(contentRef, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false) ?? throw new ArtifactNotFoundException($"Content '{contentRef}' was not found.");
        return await OpenAndVerifyContentAsync(metadata.ContentRef, metadata.Size, metadata.RelativePath, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VerifiedContent> OpenArtifactFileReadAsync(ArtifactRef artifactRef, string path, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        _ = ArtifactStoreProtocol.GetDigest(artifactRef);
        string normalizedPath;
        try
        {
            normalizedPath = ArtifactPath.Normalize(path);
        }
        catch (ArgumentException exception)
        {
            throw new ArtifactValidationException("Artifact file path is invalid.", exception);
        }

        var metadata = await _database.GetArtifactEntryAsync(artifactRef, normalizedPath, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false) ?? throw new ArtifactNotFoundException($"Artifact file '{normalizedPath}' was not found.");
        return await OpenAndVerifyContentAsync(metadata.ContentRef, metadata.Size, metadata.RelativePath, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LeaseMetadata> AcquireLeaseAsync(ArtifactRef artifactRef, string owner, TimeSpan duration, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        _ = ArtifactStoreProtocol.GetDigest(artifactRef);
        ValidateLeaseOwner(owner);
        var effectiveDuration = ValidateLeaseDuration(duration);
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(effectiveDuration);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var token = CreateLeaseToken();
            var created = await _database.CreateLeaseAsync(artifactRef, HashLeaseToken(token), owner, now, expiresAt, cancellationToken).ConfigureAwait(false);
            if (created)
            {
                return new LeaseMetadata(token, artifactRef, owner, expiresAt);
            }
        }

        throw new ArtifactNotFoundException($"Artifact '{artifactRef}' was not found or has expired.");
    }

    public async Task<LeaseMetadata> RenewLeaseAsync(string leaseToken, TimeSpan duration, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        ValidateLeaseToken(leaseToken);
        var effectiveDuration = ValidateLeaseDuration(duration);
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(effectiveDuration);
        var lease = await _database.RenewLeaseAsync(HashLeaseToken(leaseToken), now, expiresAt, cancellationToken).ConfigureAwait(false) ?? throw new ArtifactNotFoundException("The lease was not found or has expired.");
        return new LeaseMetadata(leaseToken, lease.ArtifactRef, lease.Owner, expiresAt);
    }

    public async Task ReleaseLeaseAsync(string leaseToken, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        ValidateLeaseToken(leaseToken);
        await _database.ReleaseLeaseAsync(HashLeaseToken(leaseToken), cancellationToken).ConfigureAwait(false);
    }

    public async Task<GarbageCollectionResponse> CollectGarbageAsync(int maxArtifacts, int maxContents, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (maxArtifacts is <= 0 or > 100_000 || maxContents is <= 0 or > 500_000)
        {
            throw new ArtifactValidationException("Garbage collection batch limits are invalid.");
        }

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var plan = await _database.CollectGarbageAsync(DateTimeOffset.UtcNow, maxArtifacts, maxContents, cancellationToken).ConfigureAwait(false);
            foreach (var relativePath in plan.ArtifactRelativePaths)
                DeletePublishedDirectory(relativePath);

            foreach (var content in plan.Contents)
                DeletePublishedFile(content.RelativePath);

            return new GarbageCollectionResponse(plan.ExpiredLeasesDeleted, plan.ArtifactRelativePaths.Count, plan.Contents.Count, plan.Contents.Sum(content => content.Size));
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private static async Task<StagedContent> StageContentAsync(Stream source, ContentRef expectedContentRef, long? expectedSize, long maximumSize, string temporaryPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var expectedDigest = ArtifactStoreProtocol.GetDigest(expectedContentRef);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, bufferSize: 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            long total = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                try
                {
                    total = checked(total + read);
                }
                catch (OverflowException exception)
                {
                    throw new ArtifactLimitExceededException("Content size overflowed the supported range.", exception);
                }

                if (total > maximumSize)
                {
                    throw new ArtifactLimitExceededException("Content exceeds the configured size limit.");
                }

                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            destination.Flush(flushToDisk: true);

            if (expectedSize is not null && total != expectedSize)
            {
                throw new ArtifactValidationException($"Content size mismatch. Expected {expectedSize}, received {total}.");
            }

            var digest = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(digest, expectedDigest, StringComparison.Ordinal))
            {
                throw new ArtifactValidationException($"Content digest mismatch. Expected sha256:{expectedDigest}.");
            }

            return new StagedContent(expectedContentRef, digest, temporaryPath, total);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<PublishedContent> PublishContentAsync(StagedContent staged, CancellationToken cancellationToken)
    {
        var destinationPath = GetContentPath(staged.Digest);
        var destinationDirectory = Path.GetDirectoryName(destinationPath) ?? throw new ArtifactCorruptedException("Content path did not have a parent directory.");
        CreateOwnedDirectory(destinationDirectory);
        var alreadyExisted = false;
        try
        {
            File.Move(staged.TemporaryPath, destinationPath);
        }
        catch (IOException) when (File.Exists(destinationPath))
        {
            alreadyExisted = true;
        }

        if (alreadyExisted)
        {
            await VerifyExistingFileAsync(destinationPath, staged.ContentRef, staged.Size, cancellationToken).ConfigureAwait(false);
            File.Delete(staged.TemporaryPath);
        }
        else
        {
            StorageSafety.EnsureNotLink(new FileInfo(destinationPath));
        }

        return new PublishedContent(staged.ContentRef, staged.Digest, StorageSafety.ToDatabaseRelativePath(_root, destinationPath), staged.Size, alreadyExisted);
    }

    private async Task<string> PublishArtifactDirectoryAsync(ArtifactBundleDescriptor descriptor, string stagingRoot, CancellationToken cancellationToken)
    {
        var digest = ArtifactStoreProtocol.GetDigest(descriptor.Manifest.ArtifactId);
        var stage = Path.Combine(stagingRoot, "artifact");
        Directory.CreateDirectory(stage);
        var manifestJson = JsonSerializer.Serialize(descriptor.Manifest, JsonOptions);
        var descriptorJson = JsonSerializer.Serialize(descriptor, JsonOptions);
        await WriteDurableTextAsync(Path.Combine(stage, "manifest.json"), manifestJson, cancellationToken).ConfigureAwait(false);
        await WriteDurableTextAsync(Path.Combine(stage, "descriptor.json"), descriptorJson, cancellationToken).ConfigureAwait(false);

        var destination = GetArtifactPath(digest);
        var destinationParent = Path.GetDirectoryName(destination) ?? throw new ArtifactCorruptedException("Artifact path did not have a parent directory.");
        CreateOwnedDirectory(destinationParent);
        try
        {
            Directory.Move(stage, destination);
        }
        catch (IOException) when (Directory.Exists(destination))
        {
            StorageSafety.EnsureNotLink(new DirectoryInfo(destination));
            var existingDescriptorPath = Path.Combine(destination, "descriptor.json");
            if (!File.Exists(existingDescriptorPath))
            {
                throw new ArtifactCorruptedException($"Artifact directory for '{descriptor.Manifest.ArtifactId}' is incomplete.");
            }

            StorageSafety.EnsureNotLink(new FileInfo(existingDescriptorPath));
            var existingDescriptor = await File.ReadAllTextAsync(existingDescriptorPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(existingDescriptor, descriptorJson, StringComparison.Ordinal))
            {
                throw new ArtifactCorruptedException($"Artifact directory for '{descriptor.Manifest.ArtifactId}' has conflicting data.");
            }

            DeleteDirectoryIfPresent(stage);
        }

        return StorageSafety.ToDatabaseRelativePath(_root, destination);
    }

    private async Task<VerifiedContent> OpenAndVerifyContentAsync(ContentRef contentRef, long expectedSize, string relativePath, CancellationToken cancellationToken)
    {
        var fullPath = StorageSafety.FromDatabaseRelativePath(_root, relativePath);
        if (!File.Exists(fullPath))
        {
            throw new ArtifactCorruptedException($"Content '{contentRef}' is missing from the CAS.");
        }

        StorageSafety.EnsureDirectoryTreeHasNoLinks(Path.GetDirectoryName(fullPath)!);
        StorageSafety.EnsureNotLink(new FileInfo(fullPath));
        var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var (actualSize, actualDigest) = await StorageSafety.HashFileAsync(stream, cancellationToken).ConfigureAwait(false);
            if (actualSize != expectedSize || !string.Equals(actualDigest, ArtifactStoreProtocol.GetDigest(contentRef), StringComparison.Ordinal))
            {
                throw new ArtifactCorruptedException($"Content '{contentRef}' failed size or checksum verification.");
            }

            return new VerifiedContent(stream, actualSize, contentRef);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task VerifyExistingFileAsync(string path, ContentRef contentRef, long expectedSize, CancellationToken cancellationToken)
    {
        var relativePath = StorageSafety.ToDatabaseRelativePath(_root, path);
        await using var verified = await OpenAndVerifyContentAsync(contentRef, expectedSize, relativePath, cancellationToken).ConfigureAwait(false);
    }

    private async Task VerifyArtifactMetadataAsync(StoredArtifactMetadata metadata, CancellationToken cancellationToken)
    {
        try
        {
            ArtifactIdentity.Validate(metadata.Descriptor.Manifest);
        }
        catch (ArgumentException exception)
        {
            throw new ArtifactCorruptedException("Stored artifact manifest failed identity validation.", exception);
        }

        var artifactPath = StorageSafety.FromDatabaseRelativePath(_root, metadata.RelativePath);
        if (!Directory.Exists(artifactPath))
        {
            throw new ArtifactCorruptedException($"Artifact '{metadata.Descriptor.Manifest.ArtifactId}' is missing from the CAS.");
        }

        StorageSafety.EnsureDirectoryTreeHasNoLinks(artifactPath);
        var descriptorPath = Path.Combine(artifactPath, "descriptor.json");
        if (!File.Exists(descriptorPath))
        {
            throw new ArtifactCorruptedException($"Artifact '{metadata.Descriptor.Manifest.ArtifactId}' has no descriptor.");
        }

        StorageSafety.EnsureNotLink(new FileInfo(descriptorPath));
        var diskJson = await File.ReadAllTextAsync(descriptorPath, cancellationToken).ConfigureAwait(false);
        var metadataJson = JsonSerializer.Serialize(metadata.Descriptor, JsonOptions);
        if (!string.Equals(diskJson, metadataJson, StringComparison.Ordinal))
        {
            throw new ArtifactCorruptedException($"Artifact '{metadata.Descriptor.Manifest.ArtifactId}' descriptor is corrupted.");
        }
    }

    private TimeSpan ValidateLeaseDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || duration > _options.MaximumLeaseDuration)
        {
            throw new ArtifactValidationException("Lease duration is outside the configured range.");
        }

        return duration;
    }

    private static void ValidateLeaseOwner(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner) || owner.Length > 128 || owner.Any(char.IsControl))
        {
            throw new ArtifactValidationException("Lease owner must contain 1-128 printable characters.");
        }
    }

    private static void ValidateLeaseToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 20 or > 128 || !token.StartsWith("lease_", StringComparison.Ordinal) || token.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
        {
            throw new ArtifactValidationException("Lease token is malformed.");
        }
    }

    private DateTimeOffset ResolveExpiry(DateTimeOffset now, TimeSpan? requestedTimeToLive)
    {
        var ttl = requestedTimeToLive ?? _options.DefaultTimeToLive;
        if (ttl <= TimeSpan.Zero || ttl > _options.MaximumTimeToLive)
        {
            throw new ArtifactValidationException("TTL is outside the configured range.");
        }

        return now.Add(ttl);
    }

    private static void ValidateManifest(ArtifactRef expectedArtifactRef, ArtifactManifest manifest)
    {
        if (manifest.ManifestVersion != ArtifactStoreProtocol.ArtifactManifestVersion)
        {
            throw new ArtifactValidationException($"Artifact manifest version {manifest.ManifestVersion} is not supported.");
        }

        try
        {
            _ = ArtifactStoreProtocol.GetDigest(expectedArtifactRef);
            ArtifactIdentity.Validate(manifest);
        }
        catch (ArgumentException exception)
        {
            throw new ArtifactValidationException("Artifact manifest is invalid.", exception);
        }

        if (manifest.ArtifactId != expectedArtifactRef)
        {
            throw new ArtifactValidationException("Route artifact ID does not match the manifest artifact ID.");
        }
    }

    private string CreateStagingDirectory()
    {
        var path = Path.Combine(_temporaryRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        StorageSafety.EnsureNotLink(new DirectoryInfo(path));
        return path;
    }

    private string GetContentPath(string digest) => Path.Combine(_contentsRoot, digest[..2], digest);

    private string GetArtifactPath(string digest) => Path.Combine(_artifactsRoot, digest[..2], digest);

    private static string CreateLeaseToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return "lease_" + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string HashLeaseToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static async Task WriteDurableTextAsync(string path, string text, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = Encoding.UTF8.GetBytes(text);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static void CreateOwnedDirectory(string path)
    {
        StorageSafety.EnsureDirectoryTreeHasNoLinks(path);
        Directory.CreateDirectory(path);
        StorageSafety.EnsureDirectoryTreeHasNoLinks(path);
    }

    private void RemoveAbandonedTemporaryDirectories()
    {
        foreach (var directory in Directory.EnumerateDirectories(_temporaryRoot))
        {
            try
            {
                StorageSafety.EnsureNotLink(new DirectoryInfo(directory));
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArtifactStoreException)
            {
                AbandonedStagingCleanupFailed(_logger, directory, exception);
            }
        }
    }

    private void DeletePublishedDirectory(string relativePath)
    {
        var path = StorageSafety.FromDatabaseRelativePath(_root, relativePath);
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            StorageSafety.EnsureNotLink(new DirectoryInfo(path));
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArtifactStoreException)
        {
            ArtifactDeleteFailed(_logger, path, exception);
        }
    }

    private void DeletePublishedFile(string relativePath)
    {
        var path = StorageSafety.FromDatabaseRelativePath(_root, relativePath);
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            StorageSafety.EnsureNotLink(new FileInfo(path));
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArtifactStoreException)
        {
            ContentDeleteFailed(_logger, path, exception);
        }
    }

    private static void DeleteDirectoryIfPresent(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            StorageSafety.EnsureNotLink(new DirectoryInfo(path));
            Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _initializationGate.Dispose();
        _mutationGate.Dispose();
    }
}
