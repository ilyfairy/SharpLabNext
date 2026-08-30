using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpLabNext.Catalog;
using SharpLabNext.Contracts;

namespace SharpLabNext.ProfileUpdater;

public sealed record ProfileCandidateVerificationOptions
{
    public required string LockPath { get; init; }
    public required string CatalogPath { get; init; }
    public required string EndpointsPath { get; init; }
    public required string BundlePath { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
}

public sealed record ProfileCandidateVerificationResult(string ReleaseId, string CatalogRevision, int WorkersVerified, int RuntimesVerified);

public sealed class ProfileCandidateDeploymentVerifier(HttpClient httpClient)
{
    // Candidate endpoint/configuration files are versioned canonical files
    // and retain their existing lower-camel shape. Responses fetched from a
    // SharpLabNext service use the strict PascalCase business contract.
    private static readonly JsonSerializerOptions CanonicalJsonOptions =
        ContractJson.CreateCanonicalSerializerOptions();
    private static readonly JsonSerializerOptions WireJsonOptions =
        ContractJson.CreateSerializerOptions();

    public async Task<ProfileCandidateVerificationResult> VerifyAsync(ProfileCandidateVerificationOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "The verification timeout must be positive.");

        var releaseLock = await CatalogLoader.LoadReleaseLockAsync(options.LockPath, cancellationToken);
        var candidateCatalog = await CatalogLoader.LoadCatalogAsync(options.CatalogPath, cancellationToken);
        CandidateReleaseMaterializer.ValidateIdentityClosure(releaseLock, candidateCatalog);
        var expectedReferenceSetDigests = ReferenceSetIdentityResolver.ResolveExpectedDigests(candidateCatalog, releaseLock);
        var endpoints = await ReadJsonAsync<CandidateValidationEndpoints>(options.EndpointsPath, cancellationToken);
        var bundle = await ReadJsonAsync<CandidateBundle>(options.BundlePath, cancellationToken);
        RequireEqual(releaseLock.ReleaseId, bundle.ReleaseId, "bundle.releaseId");

        var images = bundle.Images.ToDictionary(static image => image.Id, StringComparer.Ordinal);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);

        await VerifyGatewayAsync(endpoints.Gateway, releaseLock, candidateCatalog, timeout.Token);

        var workersVerified = 0;
        var verifiedReferenceSetContents = new Dictionary<string, string>(StringComparer.Ordinal);
        workersVerified += await VerifyWorkerAsync(
            endpoints,
            images,
            releaseLock,
            "roslyn-stable",
            "worker-roslyn-stable",
            new Dictionary<string, ExpectedIdentity>(StringComparer.Ordinal)
            {
                ["compilerVersion"] = new(Component(releaseLock, "roslyn-stable").ResolvedVersion)
            },
            candidateCatalog,
            expectedReferenceSetDigests,
            verifiedReferenceSetContents,
            timeout.Token);
        workersVerified += await VerifyWorkerAsync(
            endpoints,
            images,
            releaseLock,
            "roslyn-stable-netfx48",
            "worker-roslyn-netfx48",
            new Dictionary<string, ExpectedIdentity>(StringComparer.Ordinal)
            {
                ["compilerVersion"] = new(Component(releaseLock, "roslyn-stable").ResolvedVersion)
            },
            candidateCatalog,
            expectedReferenceSetDigests,
            verifiedReferenceSetContents,
            timeout.Token);
        workersVerified += await VerifyWorkerAsync(
            endpoints,
            images,
            releaseLock,
            "peachpie-stable",
            "worker-peachpie",
            new Dictionary<string, ExpectedIdentity>(StringComparer.Ordinal)
            {
                ["compilerVersion"] = new(Component(releaseLock, "peachpie-stable").ResolvedVersion),
                ["compilerCommit"] = new(Required(Component(releaseLock, "peachpie-stable").Commit, "peachpie-stable.commit"), true)
            },
            candidateCatalog,
            expectedReferenceSetDigests,
            verifiedReferenceSetContents,
            timeout.Token);
        workersVerified += await VerifyWorkerAsync(endpoints, images, releaseLock, "msvc-cppcli-netfx48", "worker-cppcli", new Dictionary<string, ExpectedIdentity>(StringComparer.Ordinal), candidateCatalog, expectedReferenceSetDigests, verifiedReferenceSetContents, timeout.Token);
        workersVerified += await VerifyWorkerAsync(
            endpoints,
            images,
            releaseLock,
            "vjc-jsharp20",
            "worker-jsharp",
            new Dictionary<string, ExpectedIdentity>(StringComparer.Ordinal)
            {
                ["compilerVersion"] = new(Component(releaseLock, "vjc-jsharp20").ResolvedVersion)
            },
            candidateCatalog,
            expectedReferenceSetDigests,
            verifiedReferenceSetContents,
            timeout.Token);
        workersVerified += await VerifyWorkerAsync(
            endpoints,
            images,
            releaseLock,
            "roslyn-main",
            "worker-roslyn-main",
            new Dictionary<string, ExpectedIdentity>(StringComparer.Ordinal)
            {
                ["compilerVersion"] = new(Component(releaseLock, "roslyn-main").ResolvedVersion),
                ["compilerCommit"] = new(Required(Component(releaseLock, "roslyn-main").Commit, "roslyn-main.commit"), true)
            },
            candidateCatalog,
            expectedReferenceSetDigests,
            verifiedReferenceSetContents,
            timeout.Token);
        workersVerified += await VerifyWorkerAsync(
            endpoints,
            images,
            releaseLock,
            "roslyn-const-generics",
            "worker-roslyn-const-generics",
            new Dictionary<string, ExpectedIdentity>(StringComparer.Ordinal)
            {
                ["compilerCommit"] = new(Required(Component(releaseLock, "roslyn-const-generics").Commit, "roslyn-const-generics.commit"), true)
            },
            candidateCatalog,
            expectedReferenceSetDigests,
            verifiedReferenceSetContents,
            timeout.Token);
        workersVerified += await VerifyWorkerAsync(
            endpoints,
            images,
            releaseLock,
            "fsharp-stable",
            "worker-fsharp",
            new Dictionary<string, ExpectedIdentity>(StringComparer.Ordinal)
            {
                ["compilerVersion"] = new(Component(releaseLock, "fsharp-stable").ResolvedVersion),
                ["fsharpCoreVersion"] = new(Component(releaseLock, "fsharp-core").ResolvedVersion)
            },
            candidateCatalog,
            expectedReferenceSetDigests,
            verifiedReferenceSetContents,
            timeout.Token);
        workersVerified += await VerifyWorkerAsync(endpoints, images, releaseLock, "gsharp-stable", "worker-gsharp", new Dictionary<string, ExpectedIdentity>(StringComparer.Ordinal), candidateCatalog, expectedReferenceSetDigests, verifiedReferenceSetContents, timeout.Token);
        workersVerified += await VerifyWorkerAsync(
            endpoints,
            images,
            releaseLock,
            "mobius-ilasm-stable",
            "worker-il",
            new Dictionary<string, ExpectedIdentity>(StringComparer.Ordinal)
            {
                ["compilerVersion"] = new(Component(releaseLock, "mobius-ilasm-stable").ResolvedVersion)
            },
            candidateCatalog,
            expectedReferenceSetDigests,
            verifiedReferenceSetContents,
            timeout.Token);
        workersVerified += await VerifyWorkerAsync(endpoints, images, releaseLock, "minilang-stable", "worker-minilang", new Dictionary<string, ExpectedIdentity>(StringComparer.Ordinal), candidateCatalog, expectedReferenceSetDigests, verifiedReferenceSetContents, timeout.Token);
        workersVerified += await VerifyWorkerAsync(
            endpoints,
            images,
            releaseLock,
            "artifacts-default",
            "worker-artifacts-default",
            new Dictionary<string, ExpectedIdentity>(StringComparer.Ordinal)
            {
                ["ilspyVersion"] = new(Component(releaseLock, "ilspy").ResolvedVersion),
                ["ilVerificationVersion"] = new(Component(releaseLock, "dotnet-ilverify").ResolvedVersion)
            },
            candidateCatalog,
            expectedReferenceSetDigests,
            verifiedReferenceSetContents,
            timeout.Token);
        workersVerified += await VerifyWorkerAsync(endpoints, images, releaseLock, "artifacts-const-generics", "worker-artifacts-const-generics", new Dictionary<string, ExpectedIdentity>(StringComparer.Ordinal), candidateCatalog, expectedReferenceSetDigests, verifiedReferenceSetContents, timeout.Token);
        workersVerified += await VerifyWorkerAsync(endpoints, images, releaseLock, "il-assembler", "worker-artifacts-il-assembler", new Dictionary<string, ExpectedIdentity>(StringComparer.Ordinal), candidateCatalog, expectedReferenceSetDigests, verifiedReferenceSetContents, timeout.Token);

        var runtimesVerified = await VerifyRuntimesAsync(endpoints, images, releaseLock, candidateCatalog, timeout.Token);
        return new ProfileCandidateVerificationResult(releaseLock.ReleaseId, candidateCatalog.Revision, workersVerified, runtimesVerified);
    }

    private async Task VerifyGatewayAsync(string gatewayEndpoint, ReleaseLockDocument releaseLock, CatalogDocument candidateCatalog, CancellationToken cancellationToken)
    {
        using var system = await GetJsonAsync(Endpoint(gatewayEndpoint, "/api/v1/system"), cancellationToken);
        RequireEqual("gateway", RequiredString(system.RootElement, "Id"), "gateway.system.Id");
        RequireEqual(releaseLock.ReleaseId, RequiredString(system.RootElement, "ReleaseId"), "gateway.system.ReleaseId");

        var remoteCatalog = await GetAsync<CatalogDocument>(Endpoint(gatewayEndpoint, "/api/v1/catalog"), cancellationToken);
        RequireEqual(candidateCatalog.Revision, remoteCatalog.Revision, "gateway.catalog.revision");
        CandidateReleaseMaterializer.ValidateIdentityClosure(releaseLock, remoteCatalog);
    }

    private async Task<int> VerifyWorkerAsync(CandidateValidationEndpoints endpoints, IReadOnlyDictionary<string, CandidateBundleImage> images, ReleaseLockDocument releaseLock, string profileId, string imageId, IReadOnlyDictionary<string, ExpectedIdentity> expectedIdentity, CatalogDocument catalog, IReadOnlyDictionary<string, string> expectedReferenceSetDigests, IDictionary<string, string> verifiedReferenceSetContents, CancellationToken cancellationToken)
    {
        if (!endpoints.Services.TryGetValue(profileId, out var endpoint))
            throw Failure($"Candidate endpoints do not contain service '{profileId}'.");
        if (!images.TryGetValue(imageId, out var image))
            throw Failure($"Candidate bundle does not contain image '{imageId}'.");

        var descriptor = await GetAsync<WorkerDescriptor>(Endpoint(endpoint, "/api/v1/worker/describe"), cancellationToken);
        RequireEqual(profileId, descriptor.Service.Id, $"worker[{profileId}].service.id");
        RequireEqual(releaseLock.ReleaseId, descriptor.Service.ReleaseId, $"worker[{profileId}].service.releaseId");
        RequireEqual("ready", descriptor.Service.Status, $"worker[{profileId}].service.status");
        RequireEqual(image.ImageId, descriptor.WorkerImageId, $"worker[{profileId}].workerImageId");
        if (!descriptor.ProfileIds.Contains(profileId, StringComparer.Ordinal))
            throw Failure($"Worker '{profileId}' does not advertise its candidate profile ID.");
        if (expectedIdentity.Count > 0 && descriptor.Identity is null)
            throw Failure($"Worker '{profileId}' did not report implementation identity.");

        foreach (var pair in expectedIdentity)
        {
            if (!descriptor.Identity!.TryGetValue(pair.Key, out var actual))
                throw Failure($"Worker '{profileId}' identity is missing '{pair.Key}'.");
            RequireEqual(pair.Value.Value, actual, $"worker[{profileId}].identity.{pair.Key}", pair.Value.IgnoreCase);
        }

        if (descriptor.WorkerKind == WorkerKind.Toolchain)
        {
            ValidateReferenceSetAttestations(profileId, descriptor.ReferenceSets, releaseLock, catalog, expectedReferenceSetDigests, verifiedReferenceSetContents);
        }

        return 1;
    }

    private static void ValidateReferenceSetAttestations(string profileId, IReadOnlyList<ReferenceSetAttestation>? referenceSets, ReleaseLockDocument releaseLock, CatalogDocument catalog, IReadOnlyDictionary<string, string> expectedReferenceSetDigests, IDictionary<string, string> verifiedReferenceSetContents)
    {
        var toolchain = catalog.Toolchains.SingleOrDefault(item => string.Equals(item.Id, profileId, StringComparison.Ordinal));
        if (toolchain is null)
            throw Failure($"Candidate catalog does not contain toolchain '{profileId}'.");
        if (referenceSets is null)
            throw Failure($"Worker '{profileId}' omitted reference-set attestations.");

        var attestations = new Dictionary<string, ReferenceSetAttestation>(StringComparer.Ordinal);
        foreach (var attestation in referenceSets)
        {
            if (string.IsNullOrWhiteSpace(attestation.Id) || !attestations.TryAdd(attestation.Id, attestation) || !IsSha256(attestation.ContentDigest) || string.IsNullOrWhiteSpace(attestation.Provenance?.Kind) || string.IsNullOrWhiteSpace(attestation.Provenance.ResolvedVersion))
            {
                throw Failure($"Worker '{profileId}' reported an invalid reference-set attestation.");
            }
        }

        foreach (var referenceSetId in toolchain.AllowedReferenceSetIds)
        {
            if (!attestations.TryGetValue(referenceSetId, out var attestation))
                throw Failure($"Worker '{profileId}' omitted reference set '{referenceSetId}'.");
            var manifest = catalog.ReferenceSets.Single(item => string.Equals(item.Id, referenceSetId, StringComparison.Ordinal));
            var component = Component(releaseLock, referenceSetId);
            var expectedDigest = expectedReferenceSetDigests.TryGetValue(referenceSetId, out var selectableDigest)
                ? selectableDigest : ReferenceSetIdentityResolver.ResolveLockedDigest(component, referenceSetId);
            var isOperatorImage = component.SourceUri?.StartsWith("docker://", StringComparison.Ordinal) == true;
            RequireEqual(expectedDigest, attestation.Digest, $"worker[{profileId}].referenceSets[{referenceSetId}].digest");
            if (verifiedReferenceSetContents.TryGetValue(referenceSetId, out var verifiedContentDigest))
            {
                RequireEqual(verifiedContentDigest, attestation.ContentDigest, $"worker[{profileId}].referenceSets[{referenceSetId}].contentDigest");
            }
            else if (isOperatorImage)
            {
                RequireEqual("operator-image", attestation.Provenance.Kind, $"worker[{profileId}].referenceSets[{referenceSetId}].provenance.kind");
                RequireEqual(Required(component.SourceUri, $"{referenceSetId}.sourceUri"), attestation.Provenance.SourceUri ?? string.Empty, $"worker[{profileId}].referenceSets[{referenceSetId}].provenance.sourceUri");
                RequireEqual(expectedDigest, attestation.Provenance.SourceArchiveDigest ?? string.Empty, $"worker[{profileId}].referenceSets[{referenceSetId}].provenance.sourceArchiveDigest");
            }
            else
            {
                verifiedReferenceSetContents.Add(referenceSetId, attestation.ContentDigest);
            }
            RequireEqual(manifest.TargetFramework, attestation.TargetFramework, $"worker[{profileId}].referenceSets[{referenceSetId}].targetFramework");
            RequireEqual(component.ResolvedVersion, attestation.Provenance.ResolvedVersion, $"worker[{profileId}].referenceSets[{referenceSetId}].provenance.resolvedVersion");

            if (!string.IsNullOrWhiteSpace(component.Package))
            {
                RequireEqual("nuget-package", attestation.Provenance.Kind, $"worker[{profileId}].referenceSets[{referenceSetId}].provenance.kind");
                RequireEqual(component.Package, attestation.Provenance.Package ?? string.Empty, $"worker[{profileId}].referenceSets[{referenceSetId}].provenance.package");
                RequireEqual(Required(component.SourceUri, $"{referenceSetId}.sourceUri"), attestation.Provenance.SourceUri ?? string.Empty, $"worker[{profileId}].referenceSets[{referenceSetId}].provenance.sourceUri");
                RequireEqual(
                    $"sha512:{Required(component.Sha512, $"{referenceSetId}.sha512")}",
                    attestation.Provenance.SourceArchiveDigest ?? string.Empty,
                    $"worker[{profileId}].referenceSets[{referenceSetId}].provenance.sourceArchiveDigest");
            }
            else if (string.Equals(attestation.Provenance.Kind, "nuget-package-composition", StringComparison.Ordinal))
            {
                ValidateCompositeReferenceSet(referenceSetId, component, attestation);
            }
            else if (!isOperatorImage)
            {
                RequireEqual("source-build", attestation.Provenance.Kind, $"worker[{profileId}].referenceSets[{referenceSetId}].provenance.kind");
                RequireEqual(Required(component.Commit, $"{referenceSetId}.commit"), attestation.Provenance.Commit ?? string.Empty, $"worker[{profileId}].referenceSets[{referenceSetId}].provenance.commit", ignoreCase: true);
                RequireEqual(Required(component.SourceUri, $"{referenceSetId}.sourceUri"), attestation.Provenance.SourceUri ?? string.Empty, $"worker[{profileId}].referenceSets[{referenceSetId}].provenance.sourceUri");
                RequireEqual(expectedDigest, attestation.Provenance.SourceArchiveDigest ?? string.Empty, $"worker[{profileId}].referenceSets[{referenceSetId}].provenance.sourceArchiveDigest");
            }
        }
    }

    private static void ValidateCompositeReferenceSet(string referenceSetId, LockedComponent component, ReferenceSetAttestation attestation)
    {
        if (!string.Equals(referenceSetId, "netfx30-managed-ref", StringComparison.Ordinal) ||
            !string.Equals(attestation.TargetFramework, "net30", StringComparison.Ordinal) ||
            !string.Equals(component.ResolvedVersion, "net30-union-v1", StringComparison.Ordinal) ||
            !string.Equals(attestation.Provenance.ResolvedVersion, "net30-union-v1", StringComparison.Ordinal) ||
            component.Digest is null ||
            !IsSha256(component.Digest) ||
            attestation.Provenance.Package is not null ||
            attestation.Provenance.SourceUri is not null ||
            attestation.Provenance.Commit is not null ||
            attestation.Provenance.SourceArchiveDigest is not null)
        {
            throw Failure($"Reference set '{referenceSetId}' has invalid composite release identity.");
        }

        var sources = attestation.Provenance.Sources;
        if (sources is null || sources.Count != 2 || !ValidCompositeSource(sources[0], "base", "all") || !ValidCompositeSource(sources[1], "extension", "assembly-version:3.0.0.0"))
        {
            throw Failure($"Reference set '{referenceSetId}' has invalid composite source provenance.");
        }

        var canonical = new StringBuilder().Append("referenceSet=").Append(referenceSetId).Append('\n').Append("targetFramework=").Append(attestation.TargetFramework).Append('\n').Append("kind=").Append(attestation.Provenance.Kind).Append('\n').Append("resolvedVersion=").Append(attestation.Provenance.ResolvedVersion).Append('\n');
        foreach (var source in sources)
        {
            canonical.Append("source=").Append(source.Role).Append('\t').Append(source.Selection).Append('\t').Append(source.Package).Append('\t').Append(source.ResolvedVersion).Append('\t').Append(source.SourceUri).Append('\t').Append(source.SourceArchiveDigest).Append('\t').Append(source.PackageContentHash).Append('\n');
        }
        var actualDigest = $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant()}";
        RequireEqual(component.Digest, actualDigest, $"referenceSets[{referenceSetId}].compositeSourceIdentity");
    }

    private static bool ValidCompositeSource(ReferenceSetProvenanceSource source, string role, string selection) =>
        string.Equals(source.Role, role, StringComparison.Ordinal) &&
        string.Equals(source.Selection, selection, StringComparison.Ordinal) &&
        IsCanonicalValue(source.Package) &&
        IsCanonicalValue(source.ResolvedVersion) &&
        IsCanonicalValue(source.SourceUri) &&
        IsCanonicalValue(source.SourceArchiveDigest) &&
        IsCanonicalValue(source.PackageContentHash) &&
        Uri.TryCreate(source.SourceUri, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment) &&
        IsLowerHexDigest(source.SourceArchiveDigest, "sha512:", 128) &&
        IsPackageContentHash(source.PackageContentHash);

    private static bool IsCanonicalValue(string? value) => !string.IsNullOrWhiteSpace(value) && value.IndexOfAny(['\t', '\r', '\n']) < 0;

    private static bool IsLowerHexDigest(string? value, string prefix, int hexLength)
    {
        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal) || value.Length != prefix.Length + hexLength)
        {
            return false;
        }
        foreach (var character in value.AsSpan(prefix.Length))
        {
            if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                return false;
        }
        return true;
    }

    private static bool IsPackageContentHash(string? value)
    {
        if (value is null || !value.StartsWith("sha512-", StringComparison.Ordinal))
            return false;
        try
        {
            return Convert.FromBase64String(value["sha512-".Length..]).Length == 64;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private async Task<int> VerifyRuntimesAsync(CandidateValidationEndpoints endpoints, IReadOnlyDictionary<string, CandidateBundleImage> images, ReleaseLockDocument releaseLock, CatalogDocument catalog, CancellationToken cancellationToken)
    {
        if (!endpoints.Services.TryGetValue("runtime-supervisor", out var endpoint))
            throw Failure("Candidate endpoints do not contain the Runtime Supervisor service.");
        if (!images.TryGetValue("runtime-supervisor", out var supervisorImage))
            throw Failure("Candidate bundle does not contain the Runtime Supervisor image.");

        using var status = await GetJsonAsync(Endpoint(endpoint, "/api/v1/runtime/status"), cancellationToken);
        var service = RequiredProperty(status.RootElement, "Service");
        RequireEqual("runtime-supervisor", RequiredString(service, "Id"), "runtime.status.service.Id");
        RequireEqual(releaseLock.ReleaseId, RequiredString(service, "ReleaseId"), "runtime.status.service.ReleaseId");
        if (!IsSha256(supervisorImage.ImageId))
            throw Failure("Candidate Runtime Supervisor image ID is not immutable.");

        var profiles = RequiredProperty(status.RootElement, "Profiles");
        if (profiles.ValueKind != JsonValueKind.Array)
            throw Failure("Runtime status profiles must be an array.");
        var byId = profiles.EnumerateArray().ToDictionary(static profile => RequiredString(profile, "Id"), static profile => profile.Clone(), StringComparer.Ordinal);

        var count = 0;
        foreach (var runtime in catalog.Runtimes.Where(static item => item.Availability.IsSelectable))
        {
            if (!byId.TryGetValue(runtime.Id, out var actual))
                throw Failure($"Runtime Supervisor does not report candidate profile '{runtime.Id}'.");
            if (!images.TryGetValue(runtime.Id, out var image))
                throw Failure($"Candidate bundle does not contain runtime image '{runtime.Id}'.");
            var component = Component(releaseLock, runtime.Id);
            RequireEqual(component.ResolvedVersion, runtime.ResolvedVersion, $"catalog.runtime[{runtime.Id}].resolvedVersion");
            RequireEqual(runtime.ResolvedVersion, RequiredString(actual, "RuntimeVersion"), $"runtime[{runtime.Id}].RuntimeVersion");
            RequireEqual(image.ImageId, RequiredString(actual, "RuntimeImageId"), $"runtime[{runtime.Id}].RuntimeImageId");
            RequireEqual(image.ImageId, RequiredString(actual, "Image"), $"runtime[{runtime.Id}].Image");
            RequireEqual(runtime.Rid, RequiredString(actual, "Rid"), $"runtime[{runtime.Id}].Rid");
            RequireEqual(runtime.Architecture, RequiredString(actual, "Architecture"), $"runtime[{runtime.Id}].Architecture");
            if (!RequiresCommitIdentity(runtime))
            {
                if (component.Digest is null || !IsSha256(component.Digest))
                    throw Failure($"Runtime component '{runtime.Id}' has no exact locked digest.");
                if (image.RuntimeCommit is not null || image.JitCommit is not null)
                {
                    throw Failure($"Candidate bundle must not claim CoreCLR commit identities for runtime '{runtime.Id}'.");
                }
                RequireEqual("not-applicable", RequiredString(actual, "RuntimeCommit"), $"runtime[{runtime.Id}].RuntimeCommit");
                RequireEqual("not-applicable", RequiredString(actual, "JitVersion"), $"runtime[{runtime.Id}].JitVersion");
                RequireEqual("not-applicable", RequiredString(actual, "JitCommit"), $"runtime[{runtime.Id}].JitCommit");
            }
            else
            {
                RequireEqual(runtime.ResolvedVersion, RequiredString(actual, "JitVersion"), $"runtime[{runtime.Id}].JitVersion");
                var expectedRuntimeCommit = Required(component.Commit, $"{runtime.Id}.commit");
                var expectedJitCommit = Required(component.JitCommit, $"{runtime.Id}.jitCommit");
                RequireEqual(expectedRuntimeCommit, Required(image.RuntimeCommit, $"bundle.images[{runtime.Id}].runtimeCommit"), $"bundle.images[{runtime.Id}].runtimeCommit", true);
                RequireEqual(expectedJitCommit, Required(image.JitCommit, $"bundle.images[{runtime.Id}].jitCommit"), $"bundle.images[{runtime.Id}].jitCommit", true);
                RequireEqual(expectedRuntimeCommit, RequiredString(actual, "RuntimeCommit"), $"runtime[{runtime.Id}].RuntimeCommit", true);
                RequireEqual(expectedJitCommit, RequiredString(actual, "JitCommit"), $"runtime[{runtime.Id}].JitCommit", true);
            }
            count++;
        }

        return count;
    }

    private async Task<T> GetAsync<T>(Uri uri, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(uri, cancellationToken);
        return document.Deserialize<T>(WireJsonOptions) ?? throw Failure($"Endpoint '{uri}' returned an empty JSON document.");
    }

    private async Task<JsonDocument> GetJsonAsync(Uri uri, CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var response = await httpClient.GetAsync(uri, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                }

                lastFailure = new HttpRequestException($"Endpoint returned {(int)response.StatusCode} ({response.StatusCode}).", null, response.StatusCode);
                if (response.StatusCode is not (HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout))
                    throw lastFailure;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException)
            {
                lastFailure = exception;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
            catch (OperationCanceledException) when (lastFailure is not null)
            {
                throw Failure($"Candidate endpoint '{uri}' did not become ready: {lastFailure.Message}");
            }
        }
    }

    private static async Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, CanonicalJsonOptions, cancellationToken) ?? throw Failure($"JSON document '{path}' is empty.");
    }

    private static LockedComponent Component(ReleaseLockDocument releaseLock, string id) => releaseLock.Components.TryGetValue(id, out var component) ? component : throw Failure($"Candidate lock is missing component '{id}'.");

    private static JsonElement RequiredProperty(JsonElement element, string propertyName) => element.TryGetProperty(propertyName, out var value) ? value : throw Failure($"JSON response is missing '{propertyName}'.");

    private static string RequiredString(JsonElement element, string propertyName)
    {
        var value = RequiredProperty(element, propertyName);
        return value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw Failure($"JSON response field '{propertyName}' must be a non-empty string.");
    }

    private static Uri Endpoint(string baseAddress, string path)
    {
        if (!Uri.TryCreate(baseAddress, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https"))
        {
            throw Failure($"Candidate endpoint '{baseAddress}' is not an absolute HTTP URI.");
        }
        return new Uri(endpoint, path);
    }

    private static string Required(string? value, string field) => !string.IsNullOrWhiteSpace(value) ? value : throw Failure($"Candidate lock field '{field}' is required.");

    private static void RequireEqual(string expected, string actual, string field, bool ignoreCase = false)
    {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(expected, actual, comparison))
            throw Failure($"Candidate identity mismatch for {field}: expected '{expected}', actual '{actual}'.");
    }

    private static bool IsSha256(string value)
    {
        if (value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal))
            return false;
        foreach (var character in value.AsSpan(7))
        {
            if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                return false;
        }
        return true;
    }

    private static bool RequiresCommitIdentity(RuntimeManifest runtime) =>
        !string.Equals(runtime.Family, "netfx-clr-wine", StringComparison.Ordinal) &&
        !string.Equals(runtime.Family, "mono", StringComparison.Ordinal);

    private static ProfileUpdateValidationException Failure(string message) => new(message);

    private sealed record ExpectedIdentity(string Value, bool IgnoreCase = false);

    private sealed record CandidateBundle
    {
        public required string ReleaseId { get; init; }
        public required CandidateBundleSource Source { get; init; }
        public required IReadOnlyList<CandidateBundleImage> Images { get; init; }
    }

    private sealed record CandidateBundleSource
    {
        public required string Revision { get; init; }
    }

    private sealed record CandidateBundleImage
    {
        public required string Id { get; init; }
        public required string ImageId { get; init; }
        public string? RuntimeCommit { get; init; }
        public string? JitCommit { get; init; }
    }
}
