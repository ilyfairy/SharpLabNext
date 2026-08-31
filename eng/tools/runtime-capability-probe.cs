#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property NuGetLockFilePath=obj/runtime-capability-probe.packages.lock.json
#:property LangVersion=14.0
#:property ManagePackageVersionsCentrally=false
#:property EnableTrimAnalyzer=false
#:property EnableAotAnalyzer=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:project ../../src/ArtifactStore/SharpLabNext.ArtifactStore.Client/SharpLabNext.ArtifactStore.Client.csproj
#:project ../../src/Tools/SharpLabNext.BundleBuilder/SharpLabNext.BundleBuilder.csproj

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.BundleBuilder;
using SharpLabNext.Contracts;

return await RuntimeCapabilityProbeApplication.RunAsync(args);

static class RuntimeCapabilityProbeApplication
{
    private const long MaximumProfileBytes = 1024 * 1024;
    private const string ProbeProject = "tests/Fixtures/SharpLabNext.RuntimeCapabilityProbe/SharpLabNext.RuntimeCapabilityProbe.csproj";
    private static readonly JsonSerializerOptions OutputJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null
    };
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions ContractJsonOptions = ContractJson.CreateSerializerOptions();

    public static async Task<int> RunAsync(string[] args)
    {
        ProbeOptions options;
        try
        {
            options = ProbeOptions.Parse(args);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine(ProbeOptions.Usage);
            return 2;
        }

        if (options.ShowHelp) { Console.WriteLine(ProbeOptions.Usage); return 0; }

        try
        {
            if (options.SelfTest) return RunSelfTest();
            await PublishAsync(options).ConfigureAwait(false);
            return 0;
        }
        catch (ProbeFailureException exception)
        {
            Console.Error.WriteLine($"Runtime capability probe failed: {exception.Message}");
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Runtime capability probe infrastructure failure: {exception.Message}");
            return 2;
        }
    }

    private static async Task PublishAsync(ProbeOptions options)
    {
        var root = FindRepositoryRoot(options.RepositoryRoot);
        var profilePath = ResolveProfilePath(root, options.ProfilePath!);
        var profileBytes = ReadBoundedRegularFile(profilePath, "Runtime Profile", MaximumProfileBytes);
        var candidateProfile = ReadProfile(profileBytes, profilePath, ".json");
        var preflightProfilePath = ResolvePromotionPath(root, options.PreflightProfilePath!, $"profiles/runtime-promotion-plans/{candidateProfile.Id}.profile.json", "immutable preflight Runtime Profile");
        var planPath = ResolvePromotionPath(root, options.PlanPath!, $"profiles/runtime-promotion-plans/{candidateProfile.Id}.json", "runtime promotion plan");
        var preflightProfileBytes = ReadBoundedRegularFile(preflightProfilePath, "immutable preflight Runtime Profile", MaximumProfileBytes);
        var planBytes = ReadBoundedRegularFile(planPath, "runtime promotion plan", MaximumProfileBytes);
        var performancePolicyRelativePath = ReadPerformancePolicyPath(planBytes);
        var performancePolicyPath = ResolvePromotionPath(root, performancePolicyRelativePath, performancePolicyRelativePath, "runtime performance policy");
        var performancePolicyBytes = ReadBoundedRegularFile(performancePolicyPath, "runtime performance policy", MaximumProfileBytes);
        RuntimePromotionPlanContext plan;
        try
        {
            plan = RuntimePromotionPlanWorkflow.CreateContext(profileBytes, preflightProfileBytes, planBytes, performancePolicyBytes);
        }
        catch (BundleValidationException exception)
        {
            throw new ProbeFailureException($"Runtime promotion plan binding is invalid: {exception.Message}", exception);
        }
        if (!StringComparer.Ordinal.Equals(plan.SourceRevision, options.SourceRevision)) throw new ProbeFailureException("Promotion plan source revision does not match --source-revision.");
        var preflightProfile = ReadProfile(preflightProfileBytes, preflightProfilePath, ".profile.json");
        ValidateProfileBinding(candidateProfile, preflightProfile, plan);
        var inputFiles = new[]
        {
            new ProbeInput(profilePath, "Runtime Profile", profileBytes),
            new ProbeInput(preflightProfilePath, "immutable preflight Runtime Profile", preflightProfileBytes),
            new ProbeInput(planPath, "runtime promotion plan", planBytes),
            new ProbeInput(performancePolicyPath, "runtime performance policy", performancePolicyBytes)
        };
        var allowedGeneratedPaths = new HashSet<string>(StringComparer.Ordinal)
        {
            RepositoryRelativePath(root, preflightProfilePath),
            RepositoryRelativePath(root, planPath)
        };
        var source = InspectSource(root, options.SourceRevision!, allowedGeneratedPaths);
        var target = SelectTarget(preflightProfile);
        var requiresExecutionFlow = preflightProfile.Capabilities.Contains("execution-flow", StringComparer.Ordinal);
        if (requiresExecutionFlow != !string.IsNullOrWhiteSpace(options.ArtifactWorkerBaseAddress))
        {
            throw new ProbeFailureException(requiresExecutionFlow ? "--artifact-worker-base-address is required by the immutable preflight Runtime Profile." : "--artifact-worker-base-address is not allowed when the immutable preflight Runtime Profile does not declare execution-flow.");
        }

        await BuildProbeAsync(root, target.TargetFramework, options.Configuration!).ConfigureAwait(false);
        VerifyInputs(inputFiles);
        RequireSameSourceState(source, InspectSource(root, options.SourceRevision!, allowedGeneratedPaths));
        var outputDirectory = Path.Combine(root, "tests", "Fixtures", "SharpLabNext.RuntimeCapabilityProbe", "bin", options.Configuration!, target.TargetFramework);
        var assemblyPath = Path.Combine(outputDirectory, target.EntryAssembly);
        var pdbPath = Path.Combine(outputDirectory, "SharpLabNext.RuntimeCapabilityProbe.pdb");
        var files = new[]
        {
            ReadProbeFile(assemblyPath, "managed-pe"),
            ReadProbeFile(pdbPath, "portable-pdb")
        };
        var preflightProfileSha256 = Sha256(preflightProfileBytes);
        var manifest = CreateManifest(preflightProfile, target, source, plan.PlanSha256, preflightProfileSha256, files);

        var token = ReadToken(options.TokenFile);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = NormalizeBaseAddress(options.ArtifactStoreBaseAddress!), Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds) };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        var store = new ArtifactStoreClient(client);
        var uploads = files.Select(static file => new ArtifactFileUpload(file.Descriptor.Path, new MemoryStream(file.Bytes, writable: false), file.Bytes.LongLength)).ToArray();
        try
        {
            var stored = await store.PutArtifactAsync(manifest, uploads, TimeSpan.FromSeconds(options.TimeToLiveSeconds), CancellationToken.None).ConfigureAwait(false);
            if (stored.ArtifactRef != manifest.ArtifactId) throw new ProbeFailureException("Artifact Store returned a different probe artifact identity.");
        }
        finally
        {
            foreach (var upload in uploads) upload.Content.Dispose();
        }

        ArtifactRef? executionFlowArtifactRef = null;
        if (requiresExecutionFlow)
        {
            using var workerClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = NormalizeBaseAddress(options.ArtifactWorkerBaseAddress!), Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
            workerClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
            executionFlowArtifactRef = await DeriveExecutionFlowArtifactAsync(workerClient, store, manifest.ArtifactId, options.TimeoutSeconds).ConfigureAwait(false);
        }

        VerifyInputs(inputFiles);
        RequireSameSourceState(source, InspectSource(root, options.SourceRevision!, allowedGeneratedPaths));

        var result = new JsonObject
        {
            ["SchemaVersion"] = 1,
            ["ProfileId"] = preflightProfile.Id,
            ["ArtifactRef"] = manifest.ArtifactId.Value,
            ["TargetFramework"] = target.TargetFramework,
            ["EntryAssembly"] = target.EntryAssembly,
            ["MethodFilter"] = SelectMethodFilter(preflightProfile),
            ["SourceRevision"] = source.Revision,
            ["CandidateProfileSha256"] = Sha256(profileBytes),
            ["PlanSha256"] = plan.PlanSha256,
            ["PreflightProfileSha256"] = preflightProfileSha256,
            ["Promotable"] = source.Promotable,
            ["Files"] = new JsonArray(files.Select(static file => (JsonNode)new JsonObject { ["Path"] = file.Descriptor.Path, ["Role"] = file.Descriptor.Role, ["Size"] = file.Descriptor.Size, ["Sha256"] = file.Descriptor.Digest }).ToArray())
        };
        if (executionFlowArtifactRef is { } flowArtifactRef) result["ExecutionFlowArtifactRef"] = flowArtifactRef.Value;
        if (options.OutputPath is { } outputPath) WriteAtomicJson(Path.GetFullPath(outputPath, root), result);
        Console.WriteLine(result.ToJsonString(OutputJson));
    }

    private static async Task<ArtifactRef> DeriveExecutionFlowArtifactAsync(HttpClient client, ArtifactStoreClient store, ArtifactRef sourceArtifactRef, int timeoutSeconds)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var request = new TransformArtifactRequest($"runtime-capability-flow-{nonce}", $"runtime-capability-flow-{nonce}", "runtime-capability-preflight-v1", sourceArtifactRef, RuntimeCapabilityProbeContract.ExecutionFlowProcessorId, RuntimeCapabilityProbeContract.ExecutionFlowTransformId, new TransformArtifactOptions(RewriterProfileId: RuntimeCapabilityProbeContract.ExecutionFlowProfileId), DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds));
        using var startResponse = await client.PostAsJsonAsync("api/v1/artifact-transforms", request, ContractJsonOptions).ConfigureAwait(false);
        await RequireWorkerSuccessAsync(startResponse).ConfigureAwait(false);
        var handle = await startResponse.Content.ReadFromJsonAsync<OperationHandle>(ContractJsonOptions).ConfigureAwait(false) ?? throw new ProbeFailureException("Artifact worker returned an empty operation handle.");
        if (string.IsNullOrWhiteSpace(handle.OperationId) || !StringComparer.Ordinal.Equals(handle.RequestId, request.RequestId))
        {
            throw new ProbeFailureException("Artifact worker returned an invalid operation handle.");
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        long fromSequence = 0;
        TransformArtifactResult? transform = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await client.GetAsync($"api/v1/operations/{Uri.EscapeDataString(handle.OperationId)}/events" + $"?FromSequence={fromSequence}").ConfigureAwait(false);
            await RequireWorkerSuccessAsync(response).ConfigureAwait(false);
            var events = await response.Content.ReadFromJsonAsync<OperationEvent[]>(ContractJsonOptions).ConfigureAwait(false) ?? throw new ProbeFailureException("Artifact worker returned an empty event stream.");
            foreach (var operationEvent in events)
            {
                if (!StringComparer.Ordinal.Equals(operationEvent.OperationId, handle.OperationId) || operationEvent.Sequence <= fromSequence)
                {
                    throw new ProbeFailureException("Artifact worker returned a non-canonical event stream.");
                }
                fromSequence = operationEvent.Sequence;
                if (operationEvent.Payload is TypedResultOperationEventPayload { Result: TransformArtifactResult result }) transform = result;
                if (operationEvent.Payload is FailedOperationEventPayload failed)
                {
                    throw new ProbeFailureException($"Execution Flow transform failed with '{failed.Error.Code}'.");
                }
                if (operationEvent.Payload is CompletedOperationEventPayload completed)
                {
                    if (completed.Status != OperationCompletionStatus.Completed ||
                        transform is not { Outcome: ArtifactJobOutcome.Succeeded, ArtifactRef: { } derivedRef } ||
                        transform.SourceArtifactRef != sourceArtifactRef ||
                        !StringComparer.Ordinal.Equals(transform.ArtifactFormat, "dotnet-managed-pe-v1"))
                    {
                        throw new ProbeFailureException("Execution Flow transform completed without a valid derived artifact.");
                    }
                    var descriptor = await store.GetArtifactAsync(derivedRef).ConfigureAwait(false) ?? throw new ProbeFailureException("Execution Flow derived artifact was not retained by Artifact Store.");
                    var derivation = descriptor.Manifest.Derivation;
                    var metadata = descriptor.Manifest.Metadata;
                    if (descriptor.Manifest.ArtifactId != derivedRef ||
                        derivation is null || derivation.ParentArtifactId != sourceArtifactRef ||
                        !StringComparer.Ordinal.Equals(derivation.ProcessorId, RuntimeCapabilityProbeContract.ExecutionFlowProcessorId) ||
                        !StringComparer.Ordinal.Equals(derivation.ProcessorVersion, RuntimeCapabilityProbeContract.ExecutionFlowProcessorVersion) ||
                        !StringComparer.Ordinal.Equals(derivation.OptionsDigest, RuntimeCapabilityProbeContract.ExecutionFlowOptionsDigest) ||
                        metadata is null ||
                        !metadata.TryGetValue(RuntimeCapabilityProbeContract.InstrumentationTransformKey, out var transformId) ||
                        !StringComparer.Ordinal.Equals(transformId, RuntimeCapabilityProbeContract.ExecutionFlowTransformId) ||
                        !metadata.TryGetValue(RuntimeCapabilityProbeContract.InstrumentationProfileKey, out var profileId) ||
                        !StringComparer.Ordinal.Equals(profileId, RuntimeCapabilityProbeContract.ExecutionFlowProfileId) ||
                        !metadata.TryGetValue(RuntimeCapabilityProbeContract.InstrumentationAppliedKey, out var applied) ||
                        !StringComparer.Ordinal.Equals(applied, "true") ||
                        !metadata.TryGetValue(RuntimeCapabilityProbeContract.InstrumentationPointsKey, out var pointsText) ||
                        !int.TryParse(pointsText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var points) ||
                        points <= 0 ||
                        !StringComparer.Ordinal.Equals(pointsText, points.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                    {
                        throw new ProbeFailureException("Execution Flow derived artifact does not bind the required instrumentation profile.");
                    }
                    return derivedRef;
                }
            }
            await Task.Delay(50).ConfigureAwait(false);
        }
        throw new ProbeFailureException("Execution Flow transform timed out.");
    }

    private static async Task RequireWorkerSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (body.Length > 4096) body = body[..4096];
        throw new ProbeFailureException($"Artifact worker returned HTTP {(int)response.StatusCode}: {body}");
    }

    private static ArtifactManifest CreateManifest(ProbeProfile profile, ProbeTarget target, SourceState source, string planSha256, string preflightProfileSha256, IReadOnlyList<ProbeFile> files)
    {
        var placeholder = new ArtifactRef($"sha256:{new string('0', 64)}");
        return ArtifactIdentity.WithComputedId(new ArtifactManifest(
            1,
            placeholder,
            new ArtifactProducer(RuntimeCapabilityProbeContract.ReleaseId, RuntimeCapabilityProbeContract.LanguageId, RuntimeCapabilityProbeContract.ToolchainId, RuntimeCapabilityProbeContract.CompilerVersion, source.Revision, $"source-revision:{source.Revision}"),
            $"runtime-capability-probe-{target.TargetFramework}-ref",
            target.TargetFramework,
            target.ArtifactFormat,
            new ArtifactRuntimeRequirement(profile.Family, [new FrameworkRequirement(profile.FrameworkName, profile.FrameworkVersion)], "anycpu", []),
            [],
            BuildOutputKind.Console,
            target.EntryAssembly,
            RuntimeCapabilityProbeContract.EntryPoint,
            files.Select(static file => file.Descriptor).ToArray(),
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [RuntimeCapabilityProbeContract.MetadataContractKey] =
                    RuntimeCapabilityProbeContract.MetadataContractValue,
                [RuntimeCapabilityProbeContract.MetadataSourceRevisionKey] = source.Revision,
                [RuntimeCapabilityProbeContract.MetadataPromotionPlanSha256Key] = planSha256,
                [RuntimeCapabilityProbeContract.MetadataPreflightProfileSha256Key] =
                    preflightProfileSha256
            }));
    }

    private static ProbeTarget SelectTarget(ProbeProfile profile)
    {
        if (!profile.Capabilities.Contains("run", StringComparer.Ordinal)) throw new ProbeFailureException($"Runtime Profile '{profile.Id}' does not declare Run.");
        if (profile.AcceptedArtifactFormats.Contains("dotnet-managed-pe-v1", StringComparer.Ordinal) && profile.Family is "coreclr" or "coreclr-wine")
        {
            return new ProbeTarget("netcoreapp2.0", "SharpLabNext.RuntimeCapabilityProbe.dll", "dotnet-managed-pe-v1");
        }
        if (profile.AcceptedArtifactFormats.Contains("dotnet-framework-managed-pe-v1", StringComparer.Ordinal) && profile.Family is "mono" or "netfx-clr-wine")
        {
            return new ProbeTarget("net20", "SharpLabNext.RuntimeCapabilityProbe.exe", "dotnet-framework-managed-pe-v1");
        }
        throw new ProbeFailureException($"Runtime Profile '{profile.Id}' has no supported managed probe artifact contract.");
    }

    private static string SelectMethodFilter(ProbeProfile profile) => profile.Family == "coreclr-wine" ? "SharpLabNext.RuntimeCapabilityProbe.Program.WindowsAbi" : "SharpLabNext.RuntimeCapabilityProbe.Program.MultipleSequencePoints";

    private static ProbeProfile ReadProfile(byte[] bytes, string path, string fileSuffix)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Runtime Profile must be an object.");
            var id = RequiredString(root, "id");
            var family = RequiredString(root, "family");
            var runtimeVersion = RequiredString(root, "runtimeVersion");
            var image = RequiredString(root, "image");
            var runtimeImageId = RequiredString(root, "runtimeImageId");
            var acceptedRuntimeFamilies = RequiredStrings(root, "acceptedRuntimeFamilies");
            var formats = RequiredStrings(root, "acceptedArtifactFormats");
            var capabilities = RequiredStrings(root, "capabilities");
            var frameworks = root.GetProperty("acceptedFrameworks");
            if (frameworks.ValueKind != JsonValueKind.Array || frameworks.GetArrayLength() != 1) throw new InvalidDataException("Probe Runtime Profiles must declare exactly one accepted framework.");
            var framework = frameworks[0];
            var frameworkName = RequiredString(framework, "name");
            // Candidate profiles may intentionally express a patch range (for
            // example .NET 5.0.0..5.0.17). The immutable image has one
            // resolved runtimeVersion; bind the probe artifact to that
            // concrete version instead of treating a valid range as malformed.
            var frameworkVersion = framework.TryGetProperty("exactVersion", out var exactVersion)
                ? RequiredStringValue(exactVersion, "exactVersion") : framework.TryGetProperty("minimumVersion", out var minimumVersion) &&
                  framework.TryGetProperty("maximumVersion", out var maximumVersion)
                    ? runtimeVersion : throw new InvalidDataException("Accepted framework must declare exactVersion or a complete version range.");
            if (!IsStableId(id) || !IsStableId(family) || Path.GetFileName(path) != $"{id}{fileSuffix}" || acceptedRuntimeFamilies.Length == 0 || formats.Length == 0 || capabilities.Length == 0)
            {
                throw new InvalidDataException("Runtime Profile probe identity is not canonical.");
            }
            return new ProbeProfile(id, family, runtimeVersion, image, runtimeImageId, acceptedRuntimeFamilies, formats, capabilities, frameworkName, frameworkVersion);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new ProbeFailureException($"Runtime Profile is invalid: {exception.Message}", exception);
        }
    }

    private static void ValidateProfileBinding(ProbeProfile candidate, ProbeProfile preflight, RuntimePromotionPlanContext plan)
    {
        RequireEqual(preflight.Id, candidate.Id, "preflight profile ID");
        RequireEqual(preflight.Family, candidate.Family, "preflight profile family");
        RequireEqual(preflight.RuntimeVersion, candidate.RuntimeVersion, "preflight runtime version");
        RequireEqual(preflight.FrameworkName, candidate.FrameworkName, "preflight accepted framework name");
        RequireEqual(preflight.FrameworkVersion, candidate.FrameworkVersion, "preflight accepted framework version");
        RequireSetEqual(preflight.AcceptedRuntimeFamilies, candidate.AcceptedRuntimeFamilies, "preflight accepted runtime families");
        RequireSetEqual(preflight.AcceptedArtifactFormats, candidate.AcceptedArtifactFormats, "preflight accepted artifact formats");
        var expectedCandidateCapabilities = preflight.Capabilities.Where(static capability => capability is not ("inspection" or "execution-flow")).ToArray();
        RequireSetEqual(candidate.Capabilities, expectedCandidateCapabilities, "candidate non-instrumentation capabilities");
        RequireSetEqual(preflight.Capabilities, plan.Capabilities, "promotion plan capabilities");
        RequireEqual(plan.ProfileId, candidate.Id, "promotion plan profile ID");
        RequireEqual(plan.ImageReference, preflight.Image, "promotion plan immutable image");
        RequireEqual(plan.ImageId, preflight.RuntimeImageId, "promotion plan immutable image ID");
    }

    private static void RequireEqual(string actual, string expected, string description)
    {
        if (!StringComparer.Ordinal.Equals(actual, expected)) throw new ProbeFailureException($"The {description} does not match the candidate promotion binding.");
    }

    private static void RequireSetEqual(IEnumerable<string> actual, IEnumerable<string> expected, string description)
    {
        if (!actual.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal)) throw new ProbeFailureException($"The {description} does not match the candidate promotion binding.");
    }

    private static string ReadPerformancePolicyPath(byte[] planBytes)
    {
        try
        {
            using var document = JsonDocument.Parse(planBytes);
            var root = document.RootElement;
            var performance = root.GetProperty("performance");
            var relativePath = RequiredString(performance, "policyPath");
            const string prefix = "profiles/runtime-performance-policies/";
            if (!relativePath.StartsWith(prefix, StringComparison.Ordinal) || !relativePath.EndsWith(".json", StringComparison.Ordinal) || relativePath.Contains('\\') || relativePath.Split('/').Any(static segment => segment.Length == 0 || segment is "." or ".."))
            {
                throw new InvalidDataException("Promotion plan performance policy path is not canonical.");
            }
            return relativePath;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new ProbeFailureException($"Runtime promotion plan is invalid: {exception.Message}", exception);
        }
    }

    private static async Task BuildProbeAsync(string root, string framework, string configuration)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("build");
        start.ArgumentList.Add(ProbeProject);
        start.ArgumentList.Add("--configuration");
        start.ArgumentList.Add(configuration);
        start.ArgumentList.Add("--framework");
        start.ArgumentList.Add(framework);
        start.ArgumentList.Add("-p:RestoreLockedMode=true");
        start.ArgumentList.Add("--nologo");
        using var process = Process.Start(start) ?? throw new ProbeFailureException("Could not start the deterministic probe build.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new ProbeFailureException($"Probe build failed with exit code {process.ExitCode}.\n{await stdout}\n{await stderr}");
        }
    }

    private static ProbeFile ReadProbeFile(string path, string role)
    {
        var bytes = ReadBoundedRegularFile(path, role, 64 * 1024 * 1024);
        var name = Path.GetFileName(path);
        return new ProbeFile(bytes, new ArtifactFileDescriptor(role, name, bytes.LongLength, $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}"));
    }

    private static SourceState InspectSource(string root, string expectedRevision, HashSet<string> allowedGeneratedPaths)
    {
        if (!IsCommit(expectedRevision)) throw new ProbeFailureException("Source revision must be a full lowercase Git commit.");
        var revision = RunGit(root, "rev-parse", "HEAD").Trim();
        if (!StringComparer.Ordinal.Equals(revision, expectedRevision)) throw new ProbeFailureException("Source revision does not match the repository HEAD.");
        var status = RunGit(root, "status", "--porcelain=v1", "-z", "--untracked-files=all");
        var unexpected = ParseGitStatus(status).Where(path => !allowedGeneratedPaths.Contains(path)).ToArray();
        if (unexpected.Length > 0)
        {
            throw new ProbeFailureException($"A promotable probe artifact requires an exact source tree; unexpected change " + $"'{unexpected[0]}'.");
        }
        return new SourceState(revision, unexpected.Length == 0);
    }

    private static List<string> ParseGitStatus(string status)
    {
        var paths = new List<string>();
        var records = status.Split('\0');
        for (var index = 0; index < records.Length; index++)
        {
            var record = records[index];
            if (record.Length == 0) continue;
            if (record.Length < 4 || record[2] != ' ' || record[0] is 'R' or 'C' || record[1] is 'R' or 'C')
            {
                throw new ProbeFailureException("Git returned a non-canonical source status for the capability probe.");
            }
            var relativePath = record[3..];
            if (relativePath.Contains('\\') || Path.IsPathRooted(relativePath) || relativePath.Split('/').Any(static segment => segment.Length == 0 || segment is "." or ".."))
            {
                throw new ProbeFailureException("Git returned a non-canonical source path for the capability probe.");
            }
            paths.Add(relativePath);
        }
        return paths;
    }

    private static string RunGit(string root, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new ProbeFailureException("Could not inspect the Git source state.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new ProbeFailureException($"Git source inspection failed: {stderr.Trim()}");
        return stdout;
    }

    private static string ResolveProfilePath(string root, string value)
    {
        var expectedRoot = Path.GetFullPath(Path.Combine(root, "profiles", "runtimes", "candidates")) +
            Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(value, root);
        if (!path.StartsWith(expectedRoot, PathComparison) || !StringComparer.OrdinalIgnoreCase.Equals(Path.GetExtension(path), ".json"))
        {
            throw new ProbeFailureException("--profile must name a candidate Runtime Profile.");
        }
        EnsureNoReparsePoints(root, path);
        return path;
    }

    private static string ResolvePromotionPath(string root, string value, string expectedRelativePath, string description)
    {
        if (!StringComparer.Ordinal.Equals(value, expectedRelativePath) || value.Contains('\\') || Path.IsPathRooted(value))
        {
            throw new ProbeFailureException($"The {description} path must be '{expectedRelativePath}'.");
        }
        var path = Path.GetFullPath(value, root);
        var relativePath = RepositoryRelativePath(root, path);
        if (!StringComparer.Ordinal.Equals(relativePath, expectedRelativePath))
        {
            throw new ProbeFailureException($"The {description} path is not canonical.");
        }
        EnsureNoReparsePoints(root, path);
        return path;
    }

    private static string RepositoryRelativePath(string root, string path)
    {
        var relativePath = Path.GetRelativePath(root, path).Replace('\\', '/');
        if (relativePath.Length == 0 || relativePath == ".." || relativePath.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
        {
            throw new ProbeFailureException("Capability probe input path escapes the repository.");
        }
        return relativePath;
    }

    private static void EnsureNoReparsePoints(string root, string path)
    {
        var relativePath = Path.GetRelativePath(root, path);
        var segments = relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = Path.GetFullPath(root);
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new ProbeFailureException($"Capability probe input path contains a reparse point: '{current}'.");
            }
        }
    }

    private static byte[] ReadBoundedRegularFile(string path, string description, long maximumBytes)
    {
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length is < 1 || info.Length > maximumBytes || info.Length > int.MaxValue)
        {
            throw new ProbeFailureException($"The {description} must be a bounded, non-link regular file.");
        }
        var bytes = File.ReadAllBytes(path);
        if (bytes.LongLength != info.Length) throw new ProbeFailureException($"The {description} changed while it was read.");
        return bytes;
    }

    private static void VerifyInputs(IEnumerable<ProbeInput> inputs)
    {
        foreach (var input in inputs)
        {
            var actual = ReadBoundedRegularFile(input.Path, input.Description, MaximumProfileBytes);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(actual), SHA256.HashData(input.Bytes)))
            {
                throw new ProbeFailureException($"The {input.Description} changed while the capability probe was produced.");
            }
        }
    }

    private static void RequireSameSourceState(SourceState expected, SourceState actual)
    {
        if (expected != actual) throw new ProbeFailureException("Git source state changed while the capability probe was produced.");
    }

    private static string Sha256(byte[] bytes) => $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";

    private static Uri NormalizeBaseAddress(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/" || !IPAddress.TryParse(uri.Host, out var address) || !IPAddress.IsLoopback(address))
        {
            throw new ProbeFailureException("Artifact Store base address must be an absolute HTTP URL on an IP loopback address.");
        }
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + '/', UriKind.Absolute);
    }

    private static string ReadToken(string? tokenFile)
    {
        var token = tokenFile is null
            ? Environment.GetEnvironmentVariable("SHARPLABNEXT_INTERNAL_SERVICE_TOKEN") : Encoding.UTF8.GetString(ReadBoundedRegularFile(tokenFile, "token file", 8192));
        token = token?.TrimEnd('\r', '\n');
        if (token is null || token.Length is < 32 or > 8192 || token.Any(static character => character is <= ' ' or >= '\u007f'))
        {
            throw new ProbeFailureException("A valid internal-service token is required.");
        }
        return token;
    }

    private static void WriteAtomicJson(string path, JsonObject document)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new ProbeFailureException("Probe output has no parent directory.");
        Directory.CreateDirectory(directory);
        var bytes = SerializeJsonUtf8Lf(document, OutputJson);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static byte[] SerializeJsonUtf8Lf(JsonObject document, JsonSerializerOptions options)
    {
        var bytes = Utf8NoBom.GetBytes(document.ToJsonString(options).ReplaceLineEndings("\n") + "\n");
        AssertUtf8NoBomLf(bytes, "serialized probe JSON");
        return bytes;
    }

    private static void AssertUtf8NoBomLf(ReadOnlySpan<byte> bytes, string description)
    {
        var text = Utf8NoBom.GetString(bytes);
        if (text.Length == 0 || text[0] == '\uFEFF' || text[^1] != '\n' || bytes.IndexOf((byte)'\r') >= 0)
        {
            throw new InvalidOperationException($"{description} must be UTF-8 without a BOM and use LF line endings.");
        }
    }

    private static int RunSelfTest()
    {
        var core = new ProbeProfile("dotnet-10-linux-x64", "coreclr", "10.0.10", "sharplabnext/runtime-dotnet-10-linux-x64:candidate", "sharplabnext/runtime-dotnet-10-linux-x64:candidate", ["coreclr"], ["dotnet-managed-pe-v1"], ["run", "jit-asm"], "Microsoft.NETCore.App", "10.0.10");
        var framework = new ProbeProfile("wine-netfx48-linux-x64", "netfx-clr-wine", "4.8", "sharplabnext/runtime-wine-netfx48-linux-x64:candidate", "sharplabnext/runtime-wine-netfx48-linux-x64:candidate", ["netfx-clr-wine"], ["dotnet-framework-managed-pe-v1", "dotnet-framework-mixed-pe-v1"], ["run"], ".NETFramework", "4.8");
        var wineCore = core with { Id = "wine-dotnet-10-linux-x64", Family = "coreclr-wine" };
        if (SelectTarget(core).TargetFramework != "netcoreapp2.0" || SelectTarget(framework).TargetFramework != "net20" || !SelectMethodFilter(core).EndsWith(".MultipleSequencePoints", StringComparison.Ordinal) || !SelectMethodFilter(wineCore).EndsWith(".WindowsAbi", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Probe target selection self-test failed.");
        }
        var json = JsonSerializer.Serialize(new { Probe = "ok" }, ContractJsonOptions);
        if (!StringComparer.Ordinal.Equals(json, "{\"Probe\":\"ok\"}")) throw new InvalidOperationException("Probe JSON serialization self-test failed.");
        RunJsonOutputSelfTest();
        ExpectArgumentFailure([]);
        ExpectArgumentFailure(["--unknown"]);
        ExpectArgumentFailure(["--timeout-seconds", "4"]);
        ExpectEndpointFailure("http://localhost:8081");
        ExpectEndpointFailure("http://127.0.0.1:8081/private");
        ExpectEndpointFailure("http://192.0.2.1:8081");
        Console.WriteLine("Runtime capability probe self-test passed.");
        return 0;
    }

    private static void RunJsonOutputSelfTest()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sharplabnext-probe-cli-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            var output = Path.Combine(directory, "probe.json");
            WriteAtomicJson(output, new JsonObject { ["probe"] = "ok" });
            AssertUtf8NoBomLf(File.ReadAllBytes(output), "written probe JSON");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static void ExpectArgumentFailure(string[] args)
    {
        try
        {
            _ = ProbeOptions.Parse(args);
        }
        catch (ArgumentException)
        {
            return;
        }
        throw new InvalidOperationException($"Invalid probe arguments were accepted: {string.Join(' ', args)}");
    }

    private static void ExpectEndpointFailure(string value)
    {
        try
        {
            _ = NormalizeBaseAddress(value);
        }
        catch (ProbeFailureException)
        {
            return;
        }
        throw new InvalidOperationException($"Unsafe artifact endpoint was accepted: {value}");
    }

    private static string FindRepositoryRoot(string? configured)
    {
        if (configured is not null)
        {
            var root = Path.GetFullPath(configured);
            if (File.Exists(Path.Combine(root, "SharpLabNext.slnx"))) return root;
            throw new ProbeFailureException("--repository-root does not contain SharpLabNext.slnx.");
        }
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SharpLabNext.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new ProbeFailureException("Could not locate the repository root.");
    }

    private static string RequiredString(JsonElement parent, string name)
    {
        var value = parent.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) throw new InvalidDataException($"Runtime Profile property '{name}' is invalid.");
        return value.GetString()!;
    }

    private static string RequiredStringValue(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) throw new InvalidDataException($"Runtime Profile property '{name}' is invalid.");
        return value.GetString()!;
    }

    private static string[] RequiredStrings(JsonElement parent, string name)
    {
        var value = parent.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"Runtime Profile property '{name}' is invalid.");
        var result = value.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray();
        if (result.Any(string.IsNullOrWhiteSpace) || result.Distinct(StringComparer.Ordinal).Count() != result.Length)
        {
            throw new InvalidDataException($"Runtime Profile property '{name}' is not canonical.");
        }
        return result;
    }

    private static bool IsStableId(string value) => value.Length is > 0 and <= 128 &&
        value.All(static character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '-' or '_' or '.');

    private static bool IsCommit(string value) => value.Length is 40 or 64 &&
        value.All(static character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

sealed class ProbeOptions
{
    public const string Usage = """
        Usage:
          dotnet run eng/tools/runtime-capability-probe.cs -- [options]

        Required live options:
          --profile <profiles/runtimes/candidates/<id>.json>
          --preflight-profile <profiles/runtime-promotion-plans/<id>.profile.json>
          --plan <profiles/runtime-promotion-plans/<id>.json>
          --artifact-store-base-address <url>
          --source-revision <full-lowercase-git-commit>

        Execution Flow:
          --artifact-worker-base-address <url>
              Required exactly when the immutable preflight Runtime Profile declares execution-flow.

        Authentication and output:
          --token-file <path>        Otherwise uses SHARPLABNEXT_INTERNAL_SERVICE_TOKEN.
          --output <path>            Optional atomic JSON identity record.
          --time-to-live-seconds <60..86400>  Default: 3600.
          --timeout-seconds <5..300>           Default: 60.

        Build and development:
          --repository-root <path>
          --configuration <Debug|Release>      Default: Release.
          --self-test
          --help
        """;

    public bool ShowHelp { get; private set; }
    public bool SelfTest { get; private set; }
    public string? RepositoryRoot { get; private set; }
    public string? ProfilePath { get; private set; }
    public string? PreflightProfilePath { get; private set; }
    public string? PlanPath { get; private set; }
    public string? ArtifactStoreBaseAddress { get; private set; }
    public string? ArtifactWorkerBaseAddress { get; private set; }
    public string? SourceRevision { get; private set; }
    public string? TokenFile { get; private set; }
    public string? OutputPath { get; private set; }
    public string? Configuration { get; private set; } = "Release";
    public int TimeToLiveSeconds { get; private set; } = 3600;
    public int TimeoutSeconds { get; private set; } = 60;

    public static ProbeOptions Parse(string[] args)
    {
        var options = new ProbeOptions();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var name = args[index];
            if (!seen.Add(name)) throw new ArgumentException($"Duplicate option '{name}'.");
            string Value() => index + 1 < args.Length ? args[++index] : throw new ArgumentException($"Missing value for {name}.");
            switch (name)
            {
                case "--help" or "-h": options.ShowHelp = true; break;
                case "--self-test": options.SelfTest = true; break;
                case "--repository-root": options.RepositoryRoot = Value(); break;
                case "--profile": options.ProfilePath = Value(); break;
                case "--preflight-profile": options.PreflightProfilePath = Value(); break;
                case "--plan": options.PlanPath = Value(); break;
                case "--artifact-store-base-address": options.ArtifactStoreBaseAddress = Value(); break;
                case "--artifact-worker-base-address": options.ArtifactWorkerBaseAddress = Value(); break;
                case "--source-revision": options.SourceRevision = Value(); break;
                case "--token-file": options.TokenFile = Value(); break;
                case "--output": options.OutputPath = Value(); break;
                case "--configuration":
                    options.Configuration = Value();
                    if (options.Configuration is not ("Debug" or "Release")) throw new ArgumentException("--configuration must be Debug or Release.");
                    break;
                case "--time-to-live-seconds":
                    if (!int.TryParse(Value(), out var ttl) || ttl is < 60 or > 86400) throw new ArgumentException("--time-to-live-seconds must be between 60 and 86400.");
                    options.TimeToLiveSeconds = ttl;
                    break;
                case "--timeout-seconds":
                    if (!int.TryParse(Value(), out var timeout) || timeout is < 5 or > 300) throw new ArgumentException("--timeout-seconds must be between 5 and 300.");
                    options.TimeoutSeconds = timeout;
                    break;
                default: throw new ArgumentException($"Unknown option '{name}'.");
            }
        }
        if (options.ShowHelp || options.SelfTest) return options;
        foreach (var (value, name) in new[]
                 {
                     (options.ProfilePath, "--profile"),
                     (options.PreflightProfilePath, "--preflight-profile"),
                     (options.PlanPath, "--plan"),
                     (options.ArtifactStoreBaseAddress, "--artifact-store-base-address"),
                     (options.SourceRevision, "--source-revision")
                 })
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} is required.");
        }
        return options;
    }
}

sealed record ProbeProfile(
    string Id,
    string Family,
    string RuntimeVersion,
    string Image,
    string RuntimeImageId,
    IReadOnlyList<string> AcceptedRuntimeFamilies,
    IReadOnlyList<string> AcceptedArtifactFormats,
    IReadOnlyList<string> Capabilities,
    string FrameworkName,
    string FrameworkVersion);

sealed record ProbeTarget(string TargetFramework, string EntryAssembly, string ArtifactFormat);
sealed record SourceState(string Revision, bool Promotable);
sealed record ProbeFile(byte[] Bytes, ArtifactFileDescriptor Descriptor);
sealed record ProbeInput(string Path, string Description, byte[] Bytes);

sealed class ProbeFailureException : Exception
{
    public ProbeFailureException(string message) : base(message) { }
    public ProbeFailureException(string message, Exception innerException) : base(message, innerException) { }
}
