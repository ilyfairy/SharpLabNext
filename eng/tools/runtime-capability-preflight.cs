#:sdk Microsoft.NET.Sdk
#:project ../../src/Tools/SharpLabNext.BundleBuilder/SharpLabNext.BundleBuilder.csproj
#:property TargetFramework=net10.0
#:property NuGetLockFilePath=obj/runtime-capability-preflight.packages.lock.json
#:property LangVersion=14.0
#:property ManagePackageVersionsCentrally=false
#:property EnableTrimAnalyzer=false
#:property EnableAotAnalyzer=false

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.BundleBuilder;
using SharpLabNext.Contracts;

return await RuntimeCapabilityPreflightApplication.RunAsync(args);

static class RuntimeCapabilityPreflightApplication
{
    private const long MaximumPromotionDocumentBytes = 1024L * 1024;
    private const long MaximumResponseBytes = 64L * 1024 * 1024;
    private static readonly TimeSpan PromotionPlanVerificationTimeout = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions PascalJson = new(ContractJson.CreateSerializerOptions())
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    private static readonly JsonSerializerOptions EvidenceJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static async Task<int> RunAsync(string[] args)
    {
        CapabilityPreflightOptions options;
        try
        {
            options = CapabilityPreflightOptions.Parse(args);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine(CapabilityPreflightOptions.Usage);
            return 2;
        }

        if (options.ShowHelp) { Console.WriteLine(CapabilityPreflightOptions.Usage); return 0; }

        try
        {
            if (options.SelfTest) return await RunSelfTestAsync().ConfigureAwait(false);
            await RunLiveAsync(options).ConfigureAwait(false);
            return 0;
        }
        catch (CapabilityGateException exception)
        {
            Console.Error.WriteLine($"Runtime capability gate failed: {exception.Message}");
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Runtime capability preflight was cancelled or exceeded its overall timeout.");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Runtime capability preflight infrastructure failure: {exception.Message}");
            return 2;
        }
    }

    private static async Task RunLiveAsync(CapabilityPreflightOptions options)
    {
        var repositoryRoot = ResolveRepositoryRoot(options.RepositoryRoot!);
        var profilePath = ResolveConfinedInput(repositoryRoot, options.ProfilePath!, "Runtime Profile");
        var preflightProfilePath = ResolveConfinedInput(repositoryRoot, options.PreflightProfilePath!, "immutable preflight Runtime Profile");
        var planPath = ResolveConfinedInput(repositoryRoot, options.PlanPath!, "runtime promotion plan");
        var performancePolicyPath = ResolveConfinedInput(repositoryRoot, options.PerformancePolicyPath!, "runtime performance policy");
        var performanceEvidencePath = ResolveConfinedInput(repositoryRoot, options.PerformanceEvidencePath!, "runtime performance evidence");
        var profileBytes = ReadBoundedRegularFile(profilePath, "Runtime Profile", MaximumPromotionDocumentBytes);
        var preflightProfileBytes = ReadBoundedRegularFile(preflightProfilePath, "immutable preflight Runtime Profile", MaximumPromotionDocumentBytes);
        var planBytes = ReadBoundedRegularFile(planPath, "runtime promotion plan", MaximumPromotionDocumentBytes);
        var performancePolicyBytes = ReadBoundedRegularFile(performancePolicyPath, "runtime performance policy", MaximumPromotionDocumentBytes);
        var performanceEvidenceBytes = ReadBoundedRegularFile(performanceEvidencePath, "runtime performance evidence", MaximumPromotionDocumentBytes);
        RuntimePromotionPlanContext context;
        try
        {
            context = RuntimePromotionPlanWorkflow.CreateContext(profileBytes, preflightProfileBytes, planBytes, performancePolicyBytes);
        }
        catch (BundleValidationException exception)
        {
            throw new CapabilityGateException(exception.Message, exception);
        }

        RequireBoundInputPath(repositoryRoot, profilePath, $"profiles/runtimes/candidates/{context.ProfileId}.json", "Runtime Profile");
        RequireBoundInputPath(repositoryRoot, preflightProfilePath, $"profiles/runtime-promotion-plans/{context.ProfileId}.profile.json", "immutable preflight Runtime Profile");
        RequireBoundInputPath(repositoryRoot, planPath, $"profiles/runtime-promotion-plans/{context.ProfileId}.json", "promotion plan");
        RequireBoundInputPath(repositoryRoot, performancePolicyPath, context.PerformancePolicyPath, "performance policy");
        RequireBoundInputPath(repositoryRoot, performanceEvidencePath, context.PerformanceEvidencePath, "performance evidence");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.OverallTimeoutSeconds));
        using var profileLock = await RuntimePromotionProfileLock.AcquireAsync(repositoryRoot, context.ProfileId, TimeSpan.FromSeconds(options.OverallTimeoutSeconds), timeout.Token).ConfigureAwait(false);
        VerifyUnchangedInputs(
            repositoryRoot,
            [
                new PromotionInput(profilePath, "Runtime Profile", profileBytes),
                new PromotionInput(preflightProfilePath, "immutable preflight Runtime Profile", preflightProfileBytes),
                new PromotionInput(planPath, "runtime promotion plan", planBytes),
                new PromotionInput(performancePolicyPath, "runtime performance policy", performancePolicyBytes),
                new PromotionInput(performanceEvidencePath, "runtime performance evidence", performanceEvidenceBytes)
            ]);
        var outputRoot = ResolveCanonicalOutputRoot(repositoryRoot, options.OutputRoot!);
        var receiptOutputPath = ResolveCanonicalReceiptOutput(repositoryRoot, options.ReceiptOutputPath!, context.ProfileId);
        ValidateCapabilityInputs(context, options);
        await VerifyPromotionPlanObservationsAsync(repositoryRoot, context, options.CandidateTarget!, timeout.Token).ConfigureAwait(false);

        var token = ReadToken(options.TokenFile);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = NormalizeBaseAddress(options.SupervisorBaseAddress!), Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var requestBinding = new RuntimeCapabilityRequestBinding(options.ProbeArtifactRef!, options.ExecutionFlowArtifactRef, options.MethodFilter);
        using var request = new HttpRequestMessage(HttpMethod.Post, "internal/v1/capabilities/preflight")
        {
            Content = JsonContent.Create(new CapabilityPreflightRequest(context.ProfileId, context.SecurityPolicyId, context.SourceRevision, context.PlanSha256, context.PreflightProfileSha256, options.ProbeArtifactRef!, options.ExecutionFlowArtifactRef, options.MethodFilter, context.JitLibraryPath), options: PascalJson)
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        var responseBytes = await ReadBoundedResponseAsync(response, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = Encoding.UTF8.GetString(responseBytes.AsSpan(0, Math.Min(responseBytes.Length, 4096)));
            if (response.StatusCode == HttpStatusCode.UnprocessableEntity) throw new CapabilityGateException($"Supervisor rejected the capability probe: {body}");
            throw new HttpRequestException($"Supervisor capability preflight returned HTTP {(int)response.StatusCode}: {body}");
        }

        CapabilityPreflightResponse envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<CapabilityPreflightResponse>(responseBytes, PascalJson) ?? throw new JsonException("The response is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Supervisor returned an invalid capability preflight envelope: {exception.Message}", exception);
        }

        var capabilities = ValidateResponse(context, envelope, repositoryRoot, outputRoot, requestBinding);
        RuntimePromotionFinalizationResult finalization;
        try
        {
            finalization = RuntimePromotionPlanWorkflow.Finalize(profileBytes, preflightProfileBytes, planBytes, performancePolicyBytes, capabilities.Evidence, performanceEvidenceBytes, requestBinding);
        }
        catch (BundleValidationException exception)
        {
            throw new CapabilityGateException(exception.Message, exception);
        }
        if (!StringComparer.Ordinal.Equals(finalization.ProfileId, context.ProfileId)) throw new CapabilityGateException("Runtime promotion finalization changed the bound profile ID.");
        if (finalization.ReceiptBytes.LongLength is < 1 or > MaximumPromotionDocumentBytes)
        {
            throw new CapabilityGateException("The finalized promotion receipt exceeds the downstream trust-boundary size limit.");
        }

        var receiptBytes = NormalizeJsonUtf8Lf(finalization.ReceiptBytes, "finalized promotion receipt");
        var outputs = new Dictionary<string, byte[]>(capabilities.Outputs, PathComparer)
        {
            [receiptOutputPath] = receiptBytes
        };
        async Task VerifyInputsAsync()
        {
            VerifyUnchangedInputs(
                repositoryRoot,
                [
                    new PromotionInput(profilePath, "Runtime Profile", profileBytes),
                    new PromotionInput(preflightProfilePath, "immutable preflight Runtime Profile", preflightProfileBytes),
                    new PromotionInput(planPath, "runtime promotion plan", planBytes),
                    new PromotionInput(performancePolicyPath, "runtime performance policy", performancePolicyBytes),
                    new PromotionInput(performanceEvidencePath, "runtime performance evidence", performanceEvidenceBytes)
                ]);
            await VerifyPromotionPlanObservationsAsync(repositoryRoot, context, options.CandidateTarget!, timeout.Token).ConfigureAwait(false);
        }
        await WriteAtomicPromotionSetAsync(repositoryRoot, outputRoot, receiptOutputPath, context.ProfileId, context.Capabilities, outputs, VerifyInputsAsync, timeout.Token).ConfigureAwait(false);
        VerifyWrittenOutputs(outputs);
        Console.WriteLine($"Runtime capability evidence and promotion receipt written for {context.ProfileId}: {Path.GetFullPath(receiptOutputPath)}");
    }

    private static ValidatedCapabilitySet ValidateResponse(RuntimePromotionPlanContext context, CapabilityPreflightResponse envelope, string repositoryRoot, string outputRoot, RuntimeCapabilityRequestBinding requestBinding)
    {
        if (envelope.Documents is not { Count: >= 1 and <= 4 } ||
            envelope.Documents.Any(static document => document is null))
        {
            throw new CapabilityGateException("Supervisor did not return one non-null document for every declared capability.");
        }

        var evidence = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var outputs = new Dictionary<string, byte[]>(PathComparer);
        var retainedImageFiles = new Dictionary<string, RuntimeCapabilityEvidenceImageFile>(StringComparer.Ordinal);
        foreach (var document in envelope.Documents.Select(static document => document!))
        {
            var bytes = SerializeJsonUtf8Lf(document);
            if (bytes.LongLength is < 1 or > MaximumPromotionDocumentBytes)
            {
                throw new CapabilityGateException("A capability evidence document exceeds the downstream trust-boundary size limit.");
            }

            RuntimeCapabilityEvidencePreflightValidationResult validated;
            try
            {
                validated = context.ValidateDocument(bytes, requestBinding);
            }
            catch (BundleValidationException exception)
            {
                throw new CapabilityGateException(exception.Message, exception);
            }
            if (!evidence.TryAdd(validated.Capability, bytes)) throw new CapabilityGateException($"Supervisor returned duplicate {validated.Capability} evidence.");
            var targetPath = ResolveEvidenceTarget(repositoryRoot, outputRoot, context.ProfileId, validated.Capability, validated.EvidencePath);
            if (!outputs.TryAdd(targetPath, bytes)) throw new CapabilityGateException($"Duplicate evidence output path '{validated.EvidencePath}'.");
            MergeRetainedImageFiles(retainedImageFiles, validated.ImageFiles);
        }

        if (evidence.Count != context.Capabilities.Count || context.Capabilities.Any(capability => !evidence.ContainsKey(capability)))
        {
            throw new CapabilityGateException($"Supervisor capability set differs from the bound promotion plan; expected [{string.Join(", ", context.Capabilities)}], observed [{string.Join(", ", evidence.Keys.Order(StringComparer.Ordinal))}].");
        }
        return new ValidatedCapabilitySet(evidence, outputs);
    }

    private static void MergeRetainedImageFiles(Dictionary<string, RuntimeCapabilityEvidenceImageFile> retained, IReadOnlyList<RuntimeCapabilityEvidenceImageFile> observed)
    {
        foreach (var file in observed)
        {
            if (retained.TryGetValue(file.Path, out var existing) && existing != file)
            {
                throw new CapabilityGateException($"Capability documents conflict on retained image file '{file.Path}'.");
            }
            retained.TryAdd(file.Path, file);
        }
    }

    private static void ValidateCapabilityInputs(RuntimePromotionPlanContext context, CapabilityPreflightOptions options)
    {
        if (context.RequiresJit != !string.IsNullOrWhiteSpace(options.MethodFilter))
        {
            throw new InvalidDataException(context.RequiresJit ? "--method-filter is required by the promotion plan's jit-asm capability." : "--method-filter is not allowed when the promotion plan has no jit-asm capability.");
        }
        if (context.RequiresExecutionFlow != !string.IsNullOrWhiteSpace(options.ExecutionFlowArtifactRef))
        {
            throw new InvalidDataException(context.RequiresExecutionFlow ? "--execution-flow-artifact-ref is required by the promotion plan." : "--execution-flow-artifact-ref is not allowed by the promotion plan.");
        }
    }

    private static async Task<byte[]> ReadBoundedResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes) throw new InvalidDataException("Supervisor capability response exceeds the maximum size.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > MaximumResponseBytes) throw new InvalidDataException("Supervisor capability response exceeds the maximum size.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static string ResolveRepositoryRoot(string value)
    {
        var root = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(root) || !File.Exists(Path.Combine(root, "SharpLabNext.slnx"))) throw new DirectoryNotFoundException("--repository-root must name the SharpLabNext source root.");
        EnsureNoReparsePoints(root, root, includeLeaf: true);
        return root;
    }

    private static string ResolveConfinedInput(string root, string value, string description)
    {
        var path = Path.GetFullPath(value, root);
        EnsureContained(root, path, description);
        EnsureNoReparsePoints(root, path, includeLeaf: true);
        return path;
    }

    private static string ResolveCanonicalOutputRoot(string repositoryRoot, string value)
    {
        var outputRoot = Path.GetFullPath(value, repositoryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var expected = Path.GetFullPath(Path.Combine(repositoryRoot, "profiles", "runtime-promotion-evidence"));
        if (!PathComparer.Equals(outputRoot, expected)) throw new InvalidDataException("--output-root must be the repository's profiles/runtime-promotion-evidence directory.");
        EnsureContained(repositoryRoot, outputRoot, "output root");
        EnsureNoReparsePoints(repositoryRoot, outputRoot, includeLeaf: false);
        Directory.CreateDirectory(outputRoot);
        EnsureNoReparsePoints(repositoryRoot, outputRoot, includeLeaf: true);
        return outputRoot;
    }

    private static void RequireBoundInputPath(string repositoryRoot, string actualPath, string expectedRelativePath, string description)
    {
        var expected = Path.GetFullPath(Path.Combine(repositoryRoot, expectedRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        EnsureContained(repositoryRoot, expected, description);
        if (!PathComparer.Equals(actualPath, expected)) throw new InvalidDataException($"The {description} must use the promotion plan's canonical repository path '{expectedRelativePath}'.");
    }

    private static string ResolveCanonicalReceiptOutput(string repositoryRoot, string value, string profileId)
    {
        var receiptPath = Path.GetFullPath(value, repositoryRoot);
        var expected = Path.GetFullPath(Path.Combine(repositoryRoot, "profiles", "runtime-promotion-receipts", $"{profileId}.json"));
        if (!PathComparer.Equals(receiptPath, expected)) throw new InvalidDataException($"--receipt-output must use canonical repository path 'profiles/runtime-promotion-receipts/{profileId}.json'.");
        EnsureContained(repositoryRoot, receiptPath, "promotion receipt output");
        EnsureNoReparsePoints(repositoryRoot, receiptPath, includeLeaf: false);
        if (File.Exists(receiptPath) || Directory.Exists(receiptPath)) EnsureNoReparsePoints(repositoryRoot, receiptPath, includeLeaf: true);
        return receiptPath;
    }

    private static string ResolveEvidenceTarget(string repositoryRoot, string outputRoot, string profileId, string capability, string relativePath)
    {
        var expectedRelative = $"profiles/runtime-promotion-evidence/{profileId}/{capability}.json";
        if (!StringComparer.Ordinal.Equals(relativePath, expectedRelative) || relativePath.Contains('\\')) throw new CapabilityGateException("A promotion plan evidence path is not canonical.");
        var target = Path.GetFullPath(Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        EnsureContained(outputRoot, target, "evidence output");
        EnsureNoReparsePoints(repositoryRoot, Path.GetDirectoryName(target)!, includeLeaf: false);
        return target;
    }

    private static void EnsureContained(string root, string path, string description)
    {
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The {description} escapes its allowed root.");
        }
    }

    private static void EnsureNoReparsePoints(string root, string path, bool includeLeaf)
    {
        EnsureContained(root, path, "path");
        var relative = Path.GetRelativePath(root, path);
        var current = root;
        var segments = relative == "."
            ? Array.Empty<string>() : relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("The repository root cannot be a reparse point.");
        var count = includeLeaf ? segments.Length : Math.Max(0, segments.Length - 1);
        for (var index = 0; index < count; index++)
        {
            current = Path.Combine(current, segments[index]);
            if (!File.Exists(current) && !Directory.Exists(current)) continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException($"Confined path contains reparse point '{current}'.");
        }
    }

    private static byte[] ReadBoundedRegularFile(string path, string description, long maximumBytes)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length is < 1 || info.Length > maximumBytes)
        {
            throw new InvalidDataException($"The {description} must be a 1..{maximumBytes} byte regular file.");
        }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        var bytes = new byte[checked((int)info.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1 || stream.Length != info.Length) throw new IOException($"The {description} changed while it was being read.");
        return bytes;
    }

    private static string ReadToken(string? tokenFile)
    {
        var token = tokenFile is null
            ? Environment.GetEnvironmentVariable("SHARPLABNEXT_INTERNAL_SERVICE_TOKEN") : Encoding.UTF8.GetString(ReadBoundedRegularFile(Path.GetFullPath(tokenFile), "internal service token", 8192));
        token = token?.TrimEnd('\r', '\n');
        if (token is null || token.Length is < 32 or > 8192 || token.Any(static character => character is <= ' ' or >= '\u007f'))
        {
            throw new InvalidDataException("Set --token-file or SHARPLABNEXT_INTERNAL_SERVICE_TOKEN to a valid internal service token.");
        }
        return token;
    }

    private static Uri NormalizeBaseAddress(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/" || !IPAddress.TryParse(uri.Host, out var address) || !IPAddress.IsLoopback(address))
        {
            throw new ArgumentException("--supervisor-base-address must be an absolute HTTP URL on an IP loopback address.");
        }
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + '/', UriKind.Absolute);
    }

    private static async Task VerifyPromotionPlanObservationsAsync(string repositoryRoot, RuntimePromotionPlanContext context, string candidateTarget, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo { FileName = "node", WorkingDirectory = repositoryRoot, UseShellExecute = false };
        foreach (var argument in new[]
                 {
                     "eng/release/runtime-promotion-plan.mjs",
                     candidateTarget,
                     "--profile", $"profiles/runtimes/candidates/{context.ProfileId}.json",
                     "--pinned-reference", context.ImageReference,
                     "--performance-policy", context.PerformancePolicyPath,
                     "--check"
                 })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the runtime promotion plan verifier.");
        await WaitForPromotionPlanVerifierExitAsync(process, cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0) throw new CapabilityGateException($"Runtime promotion plan verification failed with exit code {process.ExitCode}.");
    }

    private static async Task WaitForPromotionPlanVerifierExitAsync(Process process, CancellationToken cancellationToken)
    {
        using var localTimeout = new CancellationTokenSource(PromotionPlanVerificationTimeout);
        using var linkedTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, localTimeout.Token);
        try
        {
            await process.WaitForExitAsync(linkedTimeout.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            await TerminateProcessTreeAsync(process).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new CapabilityGateException("Runtime promotion plan verification timed out.");
        }
    }

    private static async Task TerminateProcessTreeAsync(Process process)
    {
        Exception? terminationFailure = null;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception exception)
        {
            terminationFailure = exception;
        }
        OperationCanceledException? cleanupTimeoutFailure = null;
        try
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(cleanupTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            cleanupTimeoutFailure = exception;
        }
        catch (InvalidOperationException) { }
        if (!process.HasExited)
        {
            const string message = "Runtime promotion plan verifier could not be terminated within 30 seconds.";
            var failure = terminationFailure ?? cleanupTimeoutFailure;
            throw failure is null
                ? new CapabilityGateException(message) : new CapabilityGateException(message, failure);
        }
    }

    private static async Task WriteAtomicPromotionSetAsync(string repositoryRoot, string evidenceRoot, string receiptTarget, string profileId, IReadOnlyList<string> capabilities, IReadOnlyDictionary<string, byte[]> outputs, Func<Task> verifyInputs, CancellationToken cancellationToken)
    {
        ValidatePromotionTargets(repositoryRoot, evidenceRoot, receiptTarget, profileId, capabilities, outputs.Keys);
        if (outputs.Count == 0 || outputs.Any(static output => output.Value.LongLength is < 1 or > MaximumPromotionDocumentBytes))
        {
            throw new InvalidDataException("Every promotion output must fit the downstream trust-boundary size limit.");
        }
        var snapshots = outputs.Keys.ToDictionary(static path => path, path => CaptureTargetSnapshot(repositoryRoot, path), PathComparer);
        var temporaryFiles = new Dictionary<string, string>(PathComparer);
        var commits = new List<PromotionCommitState>();
        var committed = false;
        try
        {
            foreach (var (target, bytes) in outputs)
            {
                var directory = Path.GetDirectoryName(target) ?? throw new InvalidDataException("An evidence target has no parent directory.");
                Directory.CreateDirectory(directory);
                EnsureNoReparsePoints(repositoryRoot, directory, includeLeaf: true);
                var temporary = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                EnsureNoReparsePoints(repositoryRoot, temporary, includeLeaf: true);
                VerifyFileBytes(temporary, bytes, "staged promotion output", MaximumPromotionDocumentBytes);
                temporaryFiles.Add(target, temporary);
            }

            await verifyInputs().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var target in outputs.Keys) RequireUnchangedTarget(repositoryRoot, target, snapshots[target]);

            var commitOrder = outputs.Keys.Where(path => !PathComparer.Equals(path, receiptTarget)).OrderBy(static path => path, PathComparer).Append(receiptTarget);
            foreach (var target in commitOrder)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RequireUnchangedTarget(repositoryRoot, target, snapshots[target]);
                var temporary = temporaryFiles[target];
                var directory = Path.GetDirectoryName(target)!;
                EnsureNoReparsePoints(repositoryRoot, directory, includeLeaf: true);
                var backup = snapshots[target].Exists
                    ? Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.bak") : null;
                var state = new PromotionCommitState(target, backup, outputs[target]);
                commits.Add(state);
                if (backup is not null)
                {
                    File.Move(target, backup);
                    EnsureNoReparsePoints(repositoryRoot, backup, includeLeaf: true);
                }
                File.Move(temporary, target);
                state.Installed = true;
                EnsureNoReparsePoints(repositoryRoot, target, includeLeaf: true);
                VerifyFileBytes(target, outputs[target], "committed promotion output", MaximumPromotionDocumentBytes);
            }
            await verifyInputs().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            committed = true;
        }
        catch (Exception commitException)
        {
            var rollbackFailures = RollBackPromotionSet(repositoryRoot, commits);
            if (rollbackFailures.Count > 0)
            {
                throw new IOException("Runtime promotion output commit failed and could not be fully rolled back.", new AggregateException([commitException, ..rollbackFailures]));
            }
            throw;
        }
        finally
        {
            foreach (var temporary in temporaryFiles.Values)
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            if (committed)
            {
                foreach (var backup in commits.Select(static state => state.BackupPath).Where(static path => path is not null))
                {
                    File.Delete(backup!);
                }
            }
        }
    }

    private static void ValidatePromotionTargets(string repositoryRoot, string evidenceRoot, string receiptTarget, string profileId, IReadOnlyList<string> capabilities, IEnumerable<string> targets)
    {
        var expectedReceipt = Path.GetFullPath(Path.Combine(repositoryRoot, "profiles", "runtime-promotion-receipts", $"{profileId}.json"));
        if (!PathComparer.Equals(receiptTarget, expectedReceipt)) throw new InvalidDataException("The promotion receipt target is not canonical.");

        var expected = new HashSet<string>(PathComparer) { expectedReceipt };
        foreach (var capability in capabilities)
        {
            expected.Add(Path.GetFullPath(Path.Combine(evidenceRoot, profileId, $"{capability}.json")));
        }
        var actual = targets.ToHashSet(PathComparer);
        if (!expected.SetEquals(actual)) throw new InvalidDataException("The promotion transaction target set does not exactly match the bound capabilities and receipt.");
        foreach (var target in actual) EnsureContained(repositoryRoot, target, "promotion output");
    }

    private static EvidenceTargetSnapshot CaptureTargetSnapshot(string repositoryRoot, string target)
    {
        EnsureContained(repositoryRoot, target, "promotion output");
        var directory = Path.GetDirectoryName(target) ?? throw new InvalidDataException("A promotion target has no parent directory.");
        Directory.CreateDirectory(directory);
        EnsureNoReparsePoints(repositoryRoot, directory, includeLeaf: true);
        if (Directory.Exists(target)) throw new InvalidDataException($"Evidence target '{target}' is a directory.");
        if (!File.Exists(target)) return new EvidenceTargetSnapshot(false, 0, []);
        EnsureNoReparsePoints(repositoryRoot, target, includeLeaf: true);
        var bytes = ReadBoundedRegularFile(target, "existing promotion output", MaximumPromotionDocumentBytes);
        return new EvidenceTargetSnapshot(true, bytes.LongLength, SHA256.HashData(bytes));
    }

    private static void RequireUnchangedTarget(string repositoryRoot, string target, EvidenceTargetSnapshot expected)
    {
        var actual = CaptureTargetSnapshot(repositoryRoot, target);
        if (actual.Exists != expected.Exists || actual.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(actual.Sha256, expected.Sha256))
        {
            throw new IOException($"Promotion output target changed before commit: '{target}'.");
        }
    }

    private static List<Exception> RollBackPromotionSet(string repositoryRoot, IReadOnlyList<PromotionCommitState> commits)
    {
        var failures = new List<Exception>();
        foreach (var state in commits.Reverse())
        {
            try
            {
                if (state.Installed && File.Exists(state.TargetPath))
                {
                    EnsureNoReparsePoints(repositoryRoot, state.TargetPath, includeLeaf: true);
                    VerifyFileBytes(state.TargetPath, state.ExpectedBytes, "rollback promotion output", MaximumPromotionDocumentBytes);
                    File.Delete(state.TargetPath);
                }
                if (state.BackupPath is not null)
                {
                    EnsureNoReparsePoints(repositoryRoot, state.BackupPath, includeLeaf: true);
                    File.Move(state.BackupPath, state.TargetPath);
                }
            }
            catch (Exception exception)
            {
                failures.Add(new IOException($"Could not roll back promotion output target '{state.TargetPath}'.", exception));
            }
        }
        return failures;
    }

    private static void VerifyFileBytes(string path, byte[] expected, string description, long maximumBytes)
    {
        var actual = ReadBoundedRegularFile(path, description, maximumBytes);
        if (actual.LongLength != expected.LongLength || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(actual), SHA256.HashData(expected)))
        {
            throw new IOException($"The {description} changed unexpectedly: '{path}'.");
        }
    }

    private static void VerifyUnchangedInputs(string repositoryRoot, IReadOnlyList<PromotionInput> inputs)
    {
        foreach (var input in inputs)
        {
            EnsureContained(repositoryRoot, input.Path, input.Description);
            EnsureNoReparsePoints(repositoryRoot, input.Path, includeLeaf: true);
            VerifyFileBytes(input.Path, input.Bytes, input.Description, MaximumPromotionDocumentBytes);
        }
    }

    private static void VerifyWrittenOutputs(IReadOnlyDictionary<string, byte[]> outputs)
    {
        foreach (var (path, expected) in outputs)
        {
            var actual = ReadBoundedRegularFile(path, "written promotion output", MaximumPromotionDocumentBytes);
            AssertUtf8NoBomLf(actual, "written promotion output");
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(actual), SHA256.HashData(expected)))
            {
                throw new IOException($"Promotion output changed after atomic write: '{path}'.");
            }
        }
    }

    private static byte[] SerializeJsonUtf8Lf(JsonObject document)
    {
        var bytes = Utf8NoBom.GetBytes(document.ToJsonString(EvidenceJson).ReplaceLineEndings("\n") + "\n");
        AssertUtf8NoBomLf(bytes, "serialized capability evidence");
        return bytes;
    }

    private static byte[] NormalizeJsonUtf8Lf(ReadOnlySpan<byte> bytes, string description)
    {
        var text = Utf8NoBom.GetString(bytes);
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
        if (!text.EndsWith('\n')) text += "\n";
        var normalized = Utf8NoBom.GetBytes(text.ReplaceLineEndings("\n"));
        AssertUtf8NoBomLf(normalized, description);
        return normalized;
    }

    private static void AssertUtf8NoBomLf(ReadOnlySpan<byte> bytes, string description)
    {
        var text = Utf8NoBom.GetString(bytes);
        if (text.Length == 0 || text[0] == '\uFEFF' || text[^1] != '\n' || bytes.IndexOf((byte)'\r') >= 0)
        {
            throw new InvalidOperationException($"{description} must be UTF-8 without a BOM and use LF line endings.");
        }
    }

    private static async Task<int> RunSelfTestAsync()
    {
        if (!CapabilityPreflightOptions.IsSha256(RuntimeCapabilityProbeContract.ExecutionFlowOptionsDigest))
        {
            throw new InvalidOperationException("Runtime capability execution-flow options digest self-test failed.");
        }
        var valid = CapabilityPreflightOptions.Parse(
        [
            "--supervisor-base-address", "http://127.0.0.1:8082",
            "--repository-root", ".",
            "--candidate-target", "runtime-dotnet-matrix-candidate",
            "--profile", "profiles/runtimes/candidates/example.json",
            "--preflight-profile", "profiles/runtime-promotion-plans/example.profile.json",
            "--plan", "profiles/runtime-promotion-plans/example.json",
            "--performance-policy", "profiles/runtime-performance-policies/runtime-image-linux-x64-v1.json",
            "--performance-evidence", "profiles/runtime-promotion-evidence/example/performance.json",
            "--receipt-output", "profiles/runtime-promotion-receipts/example.json",
            "--output-root", "profiles/runtime-promotion-evidence",
            "--probe-artifact-ref", $"sha256:{new string('1', 64)}"
        ]);
        if (valid.OverallTimeoutSeconds != 1800) throw new InvalidOperationException("Default timeout self-test failed.");
        ExpectArgumentFailure([]);
        ExpectArgumentFailure(["--unknown-option"]);
        ExpectArgumentFailure(["--probe-artifact-ref", "sha256:invalid"]);
        ExpectArgumentFailure(["--jit-library-path", "/usr/share/dotnet/shared/libclrjit.so"]);
        ExpectEndpointFailure("http://localhost:8082");
        ExpectEndpointFailure("http://127.0.0.1:8082/private");
        ExpectEndpointFailure("http://192.0.2.1:8082");
        try
        {
            _ = RuntimePromotionPlanWorkflow.CreateContext("{}"u8.ToArray(), "{}"u8.ToArray(), "{}"u8.ToArray(), "{}"u8.ToArray());
            throw new InvalidOperationException("Strict shared-validator self-test did not fail.");
        }
        catch (BundleValidationException) { }
        await RunProcessCancellationSelfTestAsync().ConfigureAwait(false);
        await RunFileSystemSelfTestAsync().ConfigureAwait(false);
        Console.WriteLine("Runtime capability preflight self-test passed.");
        return 0;
    }

    private static async Task RunProcessCancellationSelfTestAsync()
    {
        using var process = new Process { StartInfo = new ProcessStartInfo { FileName = "node", UseShellExecute = false } };
        process.StartInfo.ArgumentList.Add("-e");
        process.StartInfo.ArgumentList.Add("setTimeout(() => {}, 30000)");
        if (!process.Start()) throw new InvalidOperationException("Could not start timeout cancellation self-test child.");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        try
        {
            await WaitForPromotionPlanVerifierExitAsync(process, cancellation.Token).ConfigureAwait(false);
            throw new InvalidOperationException("Process cancellation self-test did not cancel.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        if (!process.HasExited) throw new InvalidOperationException("Process cancellation self-test left its child running.");
    }

    private static async Task RunFileSystemSelfTestAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sharplabnext-capability-cli-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "SharpLabNext.slnx"), string.Empty);
            var resolvedRoot = ResolveRepositoryRoot(root);
            var outputRoot = ResolveCanonicalOutputRoot(resolvedRoot, Path.Combine("profiles", "runtime-promotion-evidence"));
            var profileRoot = Path.Combine(outputRoot, "self-test");
            var first = Path.Combine(profileRoot, "run.json");
            var second = Path.Combine(profileRoot, "inspection.json");
            var receipt = ResolveCanonicalReceiptOutput(resolvedRoot, Path.Combine("profiles", "runtime-promotion-receipts", "self-test.json"), "self-test");
            var capabilities = new[] { "inspection", "run" };
            var outputs = new Dictionary<string, byte[]>(PathComparer)
            {
                [first] = SerializeJsonUtf8Lf(new JsonObject { ["pass"] = 1 }),
                [second] = SerializeJsonUtf8Lf(new JsonObject { ["pass"] = 2 }),
                [receipt] = SerializeJsonUtf8Lf(new JsonObject { ["receipt"] = 1 })
            };

            var verifierCalls = 0;
            await WriteAtomicPromotionSetAsync(resolvedRoot, outputRoot, receipt, "self-test", capabilities, outputs, () =>
            {
                verifierCalls++;
                return Task.CompletedTask;
            }, CancellationToken.None).ConfigureAwait(false);
            if (verifierCalls != 2) throw new InvalidOperationException("Promotion inputs were not verified around commit.");
            VerifyWrittenOutputs(outputs);
            var committedOutputs = outputs.ToDictionary(static item => item.Key, static item => item.Value.ToArray(), PathComparer);
            var oversizedOutputs = committedOutputs.ToDictionary(static item => item.Key, static item => item.Value.ToArray(), PathComparer);
            oversizedOutputs[first] = new byte[checked((int)MaximumPromotionDocumentBytes + 1)];
            try
            {
                await WriteAtomicPromotionSetAsync(resolvedRoot, outputRoot, receipt, "self-test", capabilities, oversizedOutputs, () => throw new InvalidOperationException("Oversized output reached the input verifier."), CancellationToken.None).ConfigureAwait(false);
                throw new InvalidOperationException("Oversized promotion output self-test did not fail.");
            }
            catch (InvalidDataException exception) when (exception.Message.Contains("trust-boundary size limit", StringComparison.Ordinal)) { }
            VerifyWrittenOutputs(committedOutputs);
            outputs[first] = SerializeJsonUtf8Lf(new JsonObject { ["pass"] = 3 });
            outputs[second] = SerializeJsonUtf8Lf(new JsonObject { ["pass"] = 4 });
            outputs[receipt] = SerializeJsonUtf8Lf(new JsonObject { ["receipt"] = 2 });
            var cancellationVerifierCalls = 0;
            using (var cancellation = new CancellationTokenSource())
            {
                try
                {
                    await WriteAtomicPromotionSetAsync(resolvedRoot, outputRoot, receipt, "self-test", capabilities, outputs, () =>
                    {
                        cancellationVerifierCalls++;
                        if (cancellationVerifierCalls == 2) cancellation.Cancel();
                        return Task.CompletedTask;
                    }, cancellation.Token).ConfigureAwait(false);
                    throw new InvalidOperationException("Atomic promotion cancellation self-test did not fail.");
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            }
            VerifyWrittenOutputs(committedOutputs);
            var rollbackVerifierCalls = 0;
            try
            {
                await WriteAtomicPromotionSetAsync(resolvedRoot, outputRoot, receipt, "self-test", capabilities, outputs, () =>
                {
                    rollbackVerifierCalls++;
                    if (rollbackVerifierCalls == 2) throw new IOException("Simulated input drift after installation.");
                    return Task.CompletedTask;
                }, CancellationToken.None).ConfigureAwait(false);
                throw new InvalidOperationException("Atomic promotion rollback self-test did not fail.");
            }
            catch (IOException exception) when (exception.Message == "Simulated input drift after installation.") { }
            VerifyWrittenOutputs(committedOutputs);
            var receiptRoot = Path.GetDirectoryName(receipt)!;
            if (Directory.EnumerateFiles(profileRoot, ".*.tmp").Any() || Directory.EnumerateFiles(profileRoot, ".*.bak").Any() || Directory.EnumerateFiles(receiptRoot, ".*.tmp").Any() || Directory.EnumerateFiles(receiptRoot, ".*.bak").Any())
            {
                throw new InvalidOperationException("Atomic promotion self-test left temporary files behind.");
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void ExpectArgumentFailure(string[] args)
    {
        try
        {
            _ = CapabilityPreflightOptions.Parse(args);
        }
        catch (ArgumentException)
        {
            return;
        }
        throw new InvalidOperationException($"Invalid capability preflight arguments were accepted: {string.Join(' ', args)}");
    }

    private static void ExpectEndpointFailure(string value)
    {
        try
        {
            _ = NormalizeBaseAddress(value);
        }
        catch (ArgumentException)
        {
            return;
        }
        throw new InvalidOperationException($"Unsafe Supervisor endpoint was accepted: {value}");
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed record PromotionInput(string Path, string Description, byte[] Bytes);

    private sealed record ValidatedCapabilitySet(IReadOnlyDictionary<string, byte[]> Evidence, IReadOnlyDictionary<string, byte[]> Outputs);

    private sealed record EvidenceTargetSnapshot(bool Exists, long Length, byte[] Sha256);

    private sealed class PromotionCommitState(string targetPath, string? backupPath, byte[] expectedBytes)
    {
        public string TargetPath { get; } = targetPath;
        public string? BackupPath { get; } = backupPath;
        public byte[] ExpectedBytes { get; } = expectedBytes;
        public bool Installed { get; set; }
    }
}

sealed class CapabilityPreflightOptions
{
    public const string Usage = """
        Usage:
          dotnet run eng/tools/runtime-capability-preflight.cs -- [options]

        Required live options:
          --supervisor-base-address <url>
          --repository-root <path>
          --candidate-target <runtime Bake candidate target>
          --profile <repository-confined-runtime-profile.json>
          --preflight-profile <profiles/runtime-promotion-plans/<profile>.profile.json>
          --plan <repository-confined-runtime-promotion-plan.json>
          --performance-policy <profiles/runtime-performance-policies/<policy>.json>
          --performance-evidence <profiles/runtime-promotion-evidence/<profile>/performance.json>
          --receipt-output <profiles/runtime-promotion-receipts/<profile>.json>
          --output-root <profiles/runtime-promotion-evidence>
          --probe-artifact-ref <sha256:...>

        Capability-specific options:
          --method-filter <method>                 Required exactly when plan declares jit-asm.
          --execution-flow-artifact-ref <sha256:...>
                                                   Required exactly when plan declares execution-flow.

        Authentication and control:
          --token-file <path>                      Otherwise uses SHARPLABNEXT_INTERNAL_SERVICE_TOKEN.
          --overall-timeout-seconds <60..7200>     Default: 1800.
          --self-test
          --help
        """;

    public bool ShowHelp { get; private set; }
    public bool SelfTest { get; private set; }
    public string? SupervisorBaseAddress { get; private set; }
    public string? RepositoryRoot { get; private set; }
    public string? CandidateTarget { get; private set; }
    public string? ProfilePath { get; private set; }
    public string? PreflightProfilePath { get; private set; }
    public string? PlanPath { get; private set; }
    public string? PerformancePolicyPath { get; private set; }
    public string? PerformanceEvidencePath { get; private set; }
    public string? ReceiptOutputPath { get; private set; }
    public string? OutputRoot { get; private set; }
    public string? ProbeArtifactRef { get; private set; }
    public string? ExecutionFlowArtifactRef { get; private set; }
    public string? MethodFilter { get; private set; }
    public string? TokenFile { get; private set; }
    public int OverallTimeoutSeconds { get; private set; } = 1800;

    public static CapabilityPreflightOptions Parse(string[] args)
    {
        var options = new CapabilityPreflightOptions();
        for (var index = 0; index < args.Length; index++)
        {
            string Value() => index + 1 < args.Length ? args[++index] : throw new ArgumentException($"Missing value for {args[index]}.");
            switch (args[index])
            {
                case "--help" or "-h": options.ShowHelp = true; break;
                case "--self-test": options.SelfTest = true; break;
                case "--supervisor-base-address": options.SupervisorBaseAddress = Value(); break;
                case "--repository-root": options.RepositoryRoot = Value(); break;
                case "--candidate-target": options.CandidateTarget = Value(); break;
                case "--profile": options.ProfilePath = Value(); break;
                case "--preflight-profile": options.PreflightProfilePath = Value(); break;
                case "--plan": options.PlanPath = Value(); break;
                case "--performance-policy": options.PerformancePolicyPath = Value(); break;
                case "--performance-evidence": options.PerformanceEvidencePath = Value(); break;
                case "--receipt-output": options.ReceiptOutputPath = Value(); break;
                case "--output-root": options.OutputRoot = Value(); break;
                case "--probe-artifact-ref": options.ProbeArtifactRef = Value(); break;
                case "--execution-flow-artifact-ref": options.ExecutionFlowArtifactRef = Value(); break;
                case "--method-filter": options.MethodFilter = Value(); break;
                case "--token-file": options.TokenFile = Value(); break;
                case "--overall-timeout-seconds":
                    if (!int.TryParse(Value(), CultureInfo.InvariantCulture, out var timeout) || timeout is < 60 or > 7200) throw new ArgumentException("--overall-timeout-seconds must be between 60 and 7200.");
                    options.OverallTimeoutSeconds = timeout;
                    break;
                default: throw new ArgumentException($"Unknown option '{args[index]}'.");
            }
        }
        if (options.ShowHelp || options.SelfTest) return options;
        foreach (var (value, name) in new[]
                 {
                     (options.SupervisorBaseAddress, "--supervisor-base-address"),
                     (options.RepositoryRoot, "--repository-root"),
                     (options.CandidateTarget, "--candidate-target"),
                     (options.ProfilePath, "--profile"),
                     (options.PreflightProfilePath, "--preflight-profile"),
                     (options.PlanPath, "--plan"),
                     (options.PerformancePolicyPath, "--performance-policy"),
                     (options.PerformanceEvidencePath, "--performance-evidence"),
                     (options.ReceiptOutputPath, "--receipt-output"),
                     (options.OutputRoot, "--output-root"),
                     (options.ProbeArtifactRef, "--probe-artifact-ref")
                 })
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} is required.");
        }
        if (!IsSha256(options.ProbeArtifactRef!)) throw new ArgumentException("--probe-artifact-ref must be a canonical SHA-256 artifact reference.");
        if (options.ExecutionFlowArtifactRef is { } flow && !IsSha256(flow))
        {
            throw new ArgumentException("--execution-flow-artifact-ref must be a canonical SHA-256 artifact reference.");
        }
        if (options.MethodFilter is { Length: > 256 } ||
            options.MethodFilter?.Any(static character => character is '\0' or '\r' or '\n') == true)
        {
            throw new ArgumentException("--method-filter is invalid.");
        }
        if (!IsStableId(options.CandidateTarget!)) throw new ArgumentException("--candidate-target must be a canonical target ID.");
        return options;
    }

    private static bool IsStableId(string value) => value.Length is > 0 and <= 128 && value.All(static character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '-' or '_' or '.');

    internal static bool IsSha256(string value)
    {
        if (value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal)) return false;
        foreach (var character in value.AsSpan(7))
        {
            if (!char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f')) return false;
        }
        return true;
    }
}

sealed class CapabilityGateException : Exception
{
    public CapabilityGateException(string message) : base(message) { }

    public CapabilityGateException(string message, Exception innerException) : base(message, innerException) { }
}

sealed record CapabilityPreflightRequest(
    string RuntimeProfileId,
    string SecurityPolicyId,
    string SourceRevision,
    string PlanSha256,
    string PreflightProfileSha256,
    string ProbeArtifactRef,
    string? ExecutionFlowArtifactRef,
    string? MethodFilter,
    string? JitLibraryPath);

sealed class CapabilityPreflightResponse
{
    public required List<JsonObject?> Documents { get; init; }
}
