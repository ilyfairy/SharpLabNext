#:sdk Microsoft.NET.Sdk
#:project ../../src/Tools/SharpLabNext.BundleBuilder/SharpLabNext.BundleBuilder.csproj
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property ManagePackageVersionsCentrally=false
#:property EnableTrimAnalyzer=false
#:property EnableAotAnalyzer=false

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
using SharpLabNext.BundleBuilder;

return await RuntimePerformancePreflightApplication.RunAsync(args);

static class RuntimePerformancePreflightApplication
{
    private const long MaximumInputBytes = 1024 * 1024;
    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly JsonSerializerOptions PolicyJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    private static readonly JsonSerializerOptions PascalJson = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = null,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public static async Task<int> RunAsync(string[] args)
    {
        PreflightOptions options;
        try
        {
            options = PreflightOptions.Parse(args);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine(PreflightOptions.Usage);
            return 2;
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(PreflightOptions.Usage);
            return 0;
        }

        try
        {
            if (options.SelfTest)
                return RunSelfTest();
            await RunLiveAsync(options).ConfigureAwait(false);
            return 0;
        }
        catch (PerformanceGateException exception)
        {
            Console.Error.WriteLine($"Runtime performance gate failed: {exception.Message}");
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Runtime performance preflight was cancelled or exceeded its overall timeout.");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Runtime performance preflight infrastructure failure: {exception.Message}");
            return 2;
        }
    }

    private static async Task RunLiveAsync(PreflightOptions options)
    {
        var repositoryRoot = ResolveRepositoryRoot(options.RepositoryRoot!);
        var profilePath = ResolveConfinedInput(repositoryRoot, options.ProfilePath!, "Runtime Profile");
        var preflightProfilePath = ResolveConfinedInput(
            repositoryRoot,
            options.PreflightProfilePath!,
            "immutable preflight Runtime Profile");
        var planPath = ResolveConfinedInput(
            repositoryRoot,
            options.PlanPath!,
            "runtime promotion plan");
        var policyPath = ResolveConfinedInput(
            repositoryRoot,
            options.PolicyPath!,
            "performance policy");
        var profileBytes = ReadBoundedRegularFile(profilePath, "Runtime Profile");
        var preflightProfileBytes = ReadBoundedRegularFile(
            preflightProfilePath,
            "immutable preflight Runtime Profile");
        var planBytes = ReadBoundedRegularFile(planPath, "runtime promotion plan");
        var policyBytes = ReadBoundedRegularFile(policyPath, "performance policy");
        RuntimePromotionPlanContext context;
        try
        {
            context = RuntimePromotionPlanWorkflow.CreateContext(
                profileBytes,
                preflightProfileBytes,
                planBytes,
                policyBytes);
        }
        catch (BundleValidationException exception)
        {
            throw new PerformanceGateException(exception.Message, exception);
        }
        RequireBoundInputPath(
            repositoryRoot,
            profilePath,
            $"profiles/runtimes/candidates/{context.ProfileId}.json",
            "Runtime Profile");
        RequireBoundInputPath(
            repositoryRoot,
            preflightProfilePath,
            $"profiles/runtime-promotion-plans/{context.ProfileId}.profile.json",
            "immutable preflight Runtime Profile");
        RequireBoundInputPath(
            repositoryRoot,
            planPath,
            $"profiles/runtime-promotion-plans/{context.ProfileId}.json",
            "runtime promotion plan");
        RequireBoundInputPath(
            repositoryRoot,
            policyPath,
            context.PerformancePolicyPath,
            "performance policy");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.OverallTimeoutSeconds));
        using var profileLock = await RuntimePromotionProfileLock.AcquireAsync(
            repositoryRoot,
            context.ProfileId,
            TimeSpan.FromSeconds(options.OverallTimeoutSeconds),
            timeout.Token).ConfigureAwait(false);
        VerifyUnchangedInputs(
            repositoryRoot,
            [
                new PromotionInput(profilePath, "Runtime Profile", profileBytes),
                new PromotionInput(
                    preflightProfilePath,
                    "immutable preflight Runtime Profile",
                    preflightProfileBytes),
                new PromotionInput(planPath, "runtime promotion plan", planBytes),
                new PromotionInput(policyPath, "performance policy", policyBytes)
            ]);
        var receiptPath = Path.Combine(
            repositoryRoot,
            "profiles",
            "runtime-promotion-receipts",
            $"{context.ProfileId}.json");
        EnsureNoReparsePoints(repositoryRoot, receiptPath, includeLeaf: true);
        if (File.Exists(receiptPath) || Directory.Exists(receiptPath))
        {
            throw new PerformanceGateException(
                $"Runtime '{context.ProfileId}' already has a promotion receipt; " +
                "rerun capability preflight to bind a new performance evidence set.");
        }
        var outputPath = ResolveCanonicalOutput(
            repositoryRoot,
            options.OutputPath!,
            context.PerformanceEvidencePath);
        var outputSnapshot = CaptureOutputSnapshot(repositoryRoot, outputPath);
        var policy = JsonSerializer.Deserialize<PerformancePolicy>(policyBytes, PolicyJson)
            ?? throw new InvalidDataException("The performance policy is empty.");
        ValidatePolicy(policy);
        if (!StringComparer.Ordinal.Equals(policy.Id, context.PerformancePolicyId))
            throw new InvalidDataException("The performance policy ID does not match the promotion plan.");
        if (context.RequiresJit != !string.IsNullOrWhiteSpace(options.MethodFilter))
        {
            throw new InvalidDataException(context.RequiresJit
                ? "--method-filter is required by the promotion plan's jit-asm capability."
                : "--method-filter is not allowed when the promotion plan has no jit-asm capability.");
        }
        var policySha256 = Sha256(policyBytes);
        var token = ReadToken(options.TokenFile);
        using var client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false
        })
        {
            BaseAddress = NormalizeBaseAddress(options.SupervisorBaseAddress!),
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var collector = new SampleCollector(
            client,
            policy,
            context,
            options.ArtifactRef!,
            options.MethodFilter,
            timeout.Token);
        await collector.CollectAsync().ConfigureAwait(false);
        var evidence = collector.BuildEvidence(policySha256);
        void VerifyInputs() => VerifyUnchangedInputs(
            repositoryRoot,
            [
                new PromotionInput(profilePath, "Runtime Profile", profileBytes),
                new PromotionInput(
                    preflightProfilePath,
                    "immutable preflight Runtime Profile",
                    preflightProfileBytes),
                new PromotionInput(planPath, "runtime promotion plan", planBytes),
                new PromotionInput(policyPath, "performance policy", policyBytes)
            ]);
        WriteAtomicJson(
            repositoryRoot,
            outputPath,
            evidence,
            outputSnapshot,
            VerifyInputs);
        Console.WriteLine(
            $"Runtime performance evidence written for {context.ProfileId}: {outputPath}");
    }

    private static int RunSelfTest()
    {
        var parsed = PreflightOptions.Parse(
        [
            "--supervisor-base-address", "http://127.0.0.1:8082",
            "--repository-root", ".",
            "--profile", "profiles/runtimes/candidates/example.json",
            "--preflight-profile", "profiles/runtime-promotion-plans/example.profile.json",
            "--plan", "profiles/runtime-promotion-plans/example.json",
            "--artifact-ref", $"sha256:{new string('1', 64)}",
            "--policy", "profiles/runtime-performance-policies/runtime-image-linux-x64-v1.json",
            "--output", "profiles/runtime-promotion-evidence/example/performance.json"
        ]);
        if (parsed.OverallTimeoutSeconds != 1800)
            throw new InvalidOperationException("Default timeout self-test failed.");
        var policy = new PerformancePolicy(
            1,
            "runtime-image-linux-x64-v1",
            new SampleCounts(3, 10),
            new ResourceLimits(1_000_000_000, [268_435_456]),
            new ImageBudget(8_589_934_592),
            new ScenarioPolicies(
                BudgetPair(30_000, 45_000),
                BudgetPair(45_000, 60_000),
                BudgetPair(60_000, 90_000)));
        ValidatePolicy(policy);
        var policyJson = JsonSerializer.Serialize(policy, PolicyJson);
        var policyRoundTrip = JsonSerializer.Deserialize<PerformancePolicy>(policyJson, PolicyJson);
        if (!StringComparer.Ordinal.Equals(policyRoundTrip?.Id, policy.Id))
            throw new InvalidOperationException("Performance policy JSON self-test failed.");
        var requestJson = JsonSerializer.Serialize(
            new SampleRequest(
                "example",
                $"sha256:{new string('2', 64)}",
                $"sha256:{new string('1', 64)}",
                "runtime-job-default",
                "run",
                null),
            PascalJson);
        if (!requestJson.Contains("\"RuntimeProfileId\":\"example\"", StringComparison.Ordinal))
            throw new InvalidOperationException("Performance request JSON self-test failed.");
        var values = Enumerable.Range(1, 10).Select(static value => (double)value).ToArray();
        if (NearestRank(values, 0.95) != 10)
            throw new InvalidOperationException("Nearest-rank P95 self-test failed.");
        ValidateSamples(
            "run.warm",
            values.Select((latency, index) => new SampleValue(
                latency,
                1024,
                $"op_{index + 1:x32}",
                1,
                DateTimeOffset.UtcNow)).ToArray(),
            10,
            policy.Scenarios.Run.Warm,
            policy.ResourceLimits.AllowedMemoryBytes[0]);
        try
        {
            ValidateSamples(
                "run.cold",
                [
                    new SampleValue(45_001, 1024, "op_00000000000000000000000000000001", 1, DateTimeOffset.UtcNow),
                    new SampleValue(1, 1024, "op_00000000000000000000000000000002", 1, DateTimeOffset.UtcNow),
                    new SampleValue(1, 1024, "op_00000000000000000000000000000003", 1, DateTimeOffset.UtcNow)
                ],
                3,
                policy.Scenarios.Run.Cold,
                policy.ResourceLimits.AllowedMemoryBytes[0]);
            throw new InvalidOperationException("Single-sample failure self-test did not fail.");
        }
        catch (PerformanceGateException)
        {
        }
        ExpectArgumentFailure([]);
        ExpectArgumentFailure(["--unknown-option"]);
        ExpectArgumentFailure(["--overall-timeout-seconds", "59"]);
        ExpectEndpointFailure("http://localhost:8082");
        ExpectEndpointFailure("http://127.0.0.1:8082/private");
        ExpectEndpointFailure("http://192.0.2.1:8082");
        RunAtomicOutputSelfTest();
        Console.WriteLine("Runtime performance preflight self-test passed.");
        return 0;
    }

    private static void RunAtomicOutputSelfTest()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sharplabnext-performance-cli-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, ".git"), "gitdir: ../worktrees/self-test\n");
            var output = Path.Combine(
                root,
                "profiles",
                "runtime-promotion-evidence",
                "self-test",
                "performance.json");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var original = "{\"original\":true}\n"u8.ToArray();
            File.WriteAllBytes(output, original);
            var snapshot = CaptureOutputSnapshot(root, output);
            var calls = 0;
            try
            {
                WriteAtomicJson(
                    root,
                    output,
                    new JsonObject { ["replacement"] = true },
                    snapshot,
                    () =>
                    {
                        calls++;
                        if (calls == 2)
                            throw new IOException("simulated input drift");
                    });
                throw new InvalidOperationException("Atomic rollback self-test did not fail.");
            }
            catch (IOException exception) when (exception.Message == "simulated input drift")
            {
            }
            VerifyExactFile(output, original, "rolled back performance evidence");
            var directory = Path.GetDirectoryName(output)!;
            if (Directory.EnumerateFiles(directory, ".*.tmp").Any() ||
                Directory.EnumerateFiles(directory, ".*.bak").Any())
            {
                throw new InvalidOperationException("Atomic output self-test left temporary files behind.");
            }
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void ExpectArgumentFailure(string[] args)
    {
        try
        {
            _ = PreflightOptions.Parse(args);
        }
        catch (ArgumentException)
        {
            return;
        }
        throw new InvalidOperationException(
            $"Invalid preflight arguments were accepted: {string.Join(' ', args)}");
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

    private static ScenarioPolicy BudgetPair(double p95, double sample) => new(
        new SampleBudget(p95, sample, 268_435_456),
        new SampleBudget(p95, sample, 268_435_456));

    internal static void ValidatePolicy(PerformancePolicy policy)
    {
        if (policy.SchemaVersion != 1 || !IsId(policy.Id))
            throw new InvalidDataException("The performance policy identity is invalid.");
        if (policy.SampleCounts.Cold is < 3 or > 20 || policy.SampleCounts.Warm is < 5 or > 50)
            throw new InvalidDataException("The performance sample counts are outside the absolute contract.");
        if (policy.ResourceLimits.NanoCpus is < 250_000_000 or > 4_000_000_000 ||
            policy.ResourceLimits.AllowedMemoryBytes is not { Count: >= 1 and <= 8 } ||
            policy.ResourceLimits.AllowedMemoryBytes.Distinct().Count() !=
            policy.ResourceLimits.AllowedMemoryBytes.Count ||
            policy.ResourceLimits.AllowedMemoryBytes.Any(static value =>
                value is < 134_217_728 or > 2_147_483_648))
        {
            throw new InvalidDataException("The performance resource limits are outside the absolute contract.");
        }
        if (policy.Image.MaximumSizeBytes is < 1 or > 17_179_869_184)
            throw new InvalidDataException("The performance image-size limit is outside the absolute contract.");
        ValidateBudget("run", policy.Scenarios.Run);
        ValidateBudget("jit", policy.Scenarios.Jit);
        ValidateBudget("mapping", policy.Scenarios.Mapping);
    }

    private static void ValidateBudget(string scenario, ScenarioPolicy policy)
    {
        ValidateBudget($"{scenario}.cold", policy.Cold);
        ValidateBudget($"{scenario}.warm", policy.Warm);
    }

    private static void ValidateBudget(string name, SampleBudget budget)
    {
        if (!double.IsFinite(budget.MaximumP95LatencyMilliseconds) ||
            budget.MaximumP95LatencyMilliseconds <= 0 ||
            budget.MaximumP95LatencyMilliseconds > 60_000 ||
            !double.IsFinite(budget.MaximumSampleLatencyMilliseconds) ||
            budget.MaximumSampleLatencyMilliseconds < budget.MaximumP95LatencyMilliseconds ||
            budget.MaximumSampleLatencyMilliseconds > 120_000 ||
            budget.MaximumPeakMemoryBytes is < 1 or > 2_147_483_648)
        {
            throw new InvalidDataException($"The performance budget '{name}' is outside the absolute contract.");
        }
    }

    internal static void ValidateSamples(
        string name,
        IReadOnlyList<SampleValue> samples,
        int expectedCount,
        SampleBudget budget,
        long memoryLimitBytes)
    {
        if (samples.Count != expectedCount)
            throw new PerformanceGateException($"{name} produced {samples.Count} samples; expected {expectedCount}.");
        foreach (var (sample, index) in samples.Select(static (value, index) => (value, index)))
        {
            if (!double.IsFinite(sample.LatencyMilliseconds) || sample.LatencyMilliseconds <= 0 ||
                sample.LatencyMilliseconds > budget.MaximumSampleLatencyMilliseconds)
            {
                throw new PerformanceGateException(
                    $"{name}[{index}] latency {sample.LatencyMilliseconds} ms exceeds the sample budget.");
            }
            if (sample.PeakMemoryBytes <= 0 || sample.PeakMemoryBytes > memoryLimitBytes ||
                sample.PeakMemoryBytes > budget.MaximumPeakMemoryBytes)
            {
                throw new PerformanceGateException(
                    $"{name}[{index}] peak memory {sample.PeakMemoryBytes} bytes exceeds the budget.");
            }
        }
        var p95 = NearestRank(samples.Select(static sample => sample.LatencyMilliseconds), 0.95);
        if (p95 > budget.MaximumP95LatencyMilliseconds)
        {
            throw new PerformanceGateException(
                $"{name} P95 latency {p95} ms exceeds {budget.MaximumP95LatencyMilliseconds} ms.");
        }
    }

    private static double NearestRank(IEnumerable<double> values, double percentile)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
            throw new ArgumentException("A percentile requires at least one value.", nameof(values));
        return sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * percentile) - 1)];
    }

    private static string ResolveRepositoryRoot(string value)
    {
        var root = Path.GetFullPath(value)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var gitMarker = Path.Combine(root, ".git");
        if (!Directory.Exists(root) ||
            (!Directory.Exists(gitMarker) && !File.Exists(gitMarker)))
        {
            throw new DirectoryNotFoundException(
                "--repository-root must name the SharpLabNext Git worktree root.");
        }
        EnsureNoReparsePoints(root, root, includeLeaf: true);
        if ((File.GetAttributes(gitMarker) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The repository .git marker cannot be a reparse point.");
        return root;
    }

    private static string ResolveConfinedInput(string root, string value, string description)
    {
        var path = Path.GetFullPath(value, root);
        EnsureContained(root, path, description);
        EnsureNoReparsePoints(root, path, includeLeaf: true);
        return path;
    }

    private static void RequireBoundInputPath(
        string root,
        string actualPath,
        string expectedRelativePath,
        string description)
    {
        var expected = Path.GetFullPath(Path.Combine(
            root,
            expectedRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!PathComparer.Equals(actualPath, expected))
        {
            throw new InvalidDataException(
                $"The {description} must use the promotion plan's canonical path " +
                $"'{expectedRelativePath}'.");
        }
    }

    private static string ResolveCanonicalOutput(
        string root,
        string value,
        string expectedRelativePath)
    {
        var output = Path.GetFullPath(value, root);
        var expected = Path.GetFullPath(Path.Combine(
            root,
            expectedRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!PathComparer.Equals(output, expected))
        {
            throw new InvalidDataException(
                $"--output must use the promotion plan's canonical path '{expectedRelativePath}'.");
        }
        EnsureContained(root, output, "performance evidence output");
        var directory = Path.GetDirectoryName(output)
            ?? throw new InvalidDataException("The performance evidence output has no parent directory.");
        EnsureNoReparsePoints(root, directory, includeLeaf: false);
        Directory.CreateDirectory(directory);
        EnsureNoReparsePoints(root, directory, includeLeaf: true);
        if (File.Exists(output) || Directory.Exists(output))
            EnsureNoReparsePoints(root, output, includeLeaf: true);
        if (Directory.Exists(output))
            throw new InvalidDataException("The performance evidence output cannot be a directory.");
        return output;
    }

    private static void EnsureContained(string root, string path, string description)
    {
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The {description} escapes the repository root.");
        }
    }

    private static void EnsureNoReparsePoints(string root, string path, bool includeLeaf)
    {
        EnsureContained(root, path, "confined path");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The repository root cannot be a reparse point.");
        var relative = Path.GetRelativePath(root, path);
        var segments = relative == "."
            ? Array.Empty<string>()
            : relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        var count = includeLeaf ? segments.Length : Math.Max(0, segments.Length - 1);
        for (var index = 0; index < count; index++)
        {
            current = Path.Combine(current, segments[index]);
            if (!File.Exists(current) && !Directory.Exists(current))
                continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Confined path contains reparse point '{current}'.");
        }
    }

    private static OutputSnapshot CaptureOutputSnapshot(string root, string path)
    {
        EnsureNoReparsePoints(root, path, includeLeaf: File.Exists(path));
        if (!File.Exists(path))
            return new OutputSnapshot(false, 0, []);
        var bytes = ReadBoundedRegularFile(path, "existing performance evidence");
        return new OutputSnapshot(true, bytes.LongLength, SHA256.HashData(bytes));
    }

    private static void RequireUnchangedOutput(string root, string path, OutputSnapshot expected)
    {
        var actual = CaptureOutputSnapshot(root, path);
        if (actual.Exists != expected.Exists || actual.Length != expected.Length ||
            !CryptographicOperations.FixedTimeEquals(actual.Sha256, expected.Sha256))
        {
            throw new IOException("The performance evidence target changed during sampling.");
        }
    }

    private static void VerifyUnchangedInputs(string root, IReadOnlyList<PromotionInput> inputs)
    {
        foreach (var input in inputs)
        {
            EnsureContained(root, input.Path, input.Description);
            EnsureNoReparsePoints(root, input.Path, includeLeaf: true);
            VerifyExactFile(input.Path, input.Bytes, input.Description);
        }
    }

    private static void VerifyExactFile(string path, byte[] expected, string description)
    {
        var actual = ReadBoundedRegularFile(path, description);
        if (actual.LongLength != expected.LongLength ||
            !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(actual),
                SHA256.HashData(expected)))
        {
            throw new IOException($"The {description} changed unexpectedly.");
        }
    }

    private static byte[] ReadBoundedRegularFile(string path, string description)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.LinkTarget is not null ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
            info.Length is < 1 or > MaximumInputBytes)
        {
            throw new InvalidDataException($"The {description} must be a 1..{MaximumInputBytes} byte regular file.");
        }
        using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        var bytes = new byte[checked((int)info.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1 || stream.Length != info.Length)
            throw new IOException($"The {description} changed while it was being read.");
        return bytes;
    }

    private static string ReadToken(string? tokenFile)
    {
        var token = tokenFile is null
            ? Environment.GetEnvironmentVariable("SHARPLABNEXT_INTERNAL_SERVICE_TOKEN")
            : Encoding.UTF8.GetString(ReadBoundedRegularFile(tokenFile, "internal service token"));
        token = token?.TrimEnd('\r', '\n');
        if (token is null || token.Length is < 32 or > 8192 ||
            token.Any(static character => character is <= ' ' or >= '\u007f'))
        {
            throw new InvalidDataException(
                "Set --token-file or SHARPLABNEXT_INTERNAL_SERVICE_TOKEN to a valid internal service token.");
        }
        return token;
    }

    private static Uri NormalizeBaseAddress(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/" ||
            !IPAddress.TryParse(uri.Host, out var address) || !IPAddress.IsLoopback(address))
        {
            throw new ArgumentException(
                "--supervisor-base-address must be an absolute HTTP URL on an IP loopback address.");
        }
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + '/', UriKind.Absolute);
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";

    private static bool IsId(string value) =>
        value.Length is > 0 and <= 128 && value.All(static character =>
            char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '-' or '_' or '.');

    private static void WriteAtomicJson(
        string repositoryRoot,
        string path,
        JsonObject document,
        OutputSnapshot snapshot,
        Action verifyInputs)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new ArgumentException("The output path has no parent directory.");
        Directory.CreateDirectory(directory);
        EnsureNoReparsePoints(repositoryRoot, directory, includeLeaf: true);
        var bytes = Encoding.UTF8.GetBytes(document.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true
        }) + "\n");
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        string? backup = null;
        var installed = false;
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            verifyInputs();
            RequireUnchangedOutput(repositoryRoot, path, snapshot);
            if (snapshot.Exists)
            {
                backup = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.bak");
                File.Move(path, backup);
            }
            File.Move(temporary, path);
            installed = true;
            verifyInputs();
            VerifyExactFile(path, bytes, "written performance evidence");
            if (backup is not null)
            {
                File.Delete(backup);
                backup = null;
            }
        }
        catch (Exception failure)
        {
            try
            {
                if (installed && File.Exists(path))
                    File.Delete(path);
                if (backup is not null && File.Exists(backup) && !File.Exists(path))
                {
                    File.Move(backup, path);
                    backup = null;
                }
            }
            catch (Exception rollbackFailure)
            {
                throw new AggregateException(
                    "Performance evidence installation failed and could not be rolled back.",
                    failure,
                    rollbackFailure);
            }
            throw;
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private sealed class SampleCollector(
        HttpClient client,
        PerformancePolicy policy,
        RuntimePromotionPlanContext context,
        string artifactRef,
        string? methodFilter,
        CancellationToken cancellationToken)
    {
        private readonly Dictionary<string, CollectedScenario> _scenarios = new(StringComparer.Ordinal);
        private readonly HashSet<string> _operationIds = new(StringComparer.Ordinal);
        private readonly DateTimeOffset _collectionStartedAtUtc = DateTimeOffset.UtcNow;
        private SampleResponse? _identity;

        public async Task CollectAsync()
        {
            await CollectScenarioAsync("run", methodFilter: null, policy.Scenarios.Run).ConfigureAwait(false);
            var identity = _identity!;
            if (identity.Capabilities.Contains("jit-asm", StringComparer.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(methodFilter))
                    throw new InvalidDataException("--method-filter is required for a JIT-capable Runtime Profile.");
                await CollectScenarioAsync("jit", methodFilter, policy.Scenarios.Jit).ConfigureAwait(false);
                if (identity.SourceMappingKind is not ("none" or "not-applicable"))
                    await CollectScenarioAsync("mapping", methodFilter, policy.Scenarios.Mapping).ConfigureAwait(false);
            }
        }

        public JsonObject BuildEvidence(string policySha256)
        {
            var identity = _identity ?? throw new InvalidOperationException("No performance samples were collected.");
            var scenarios = new JsonObject();
            foreach (var (name, value) in _scenarios.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                scenarios[name] = new JsonObject
                {
                    ["cold"] = SamplesJson(value.Cold),
                    ["warm"] = SamplesJson(value.Warm)
                };
            }
            return new JsonObject
            {
                ["schemaVersion"] = 1,
                ["profileId"] = context.ProfileId,
                ["planSha256"] = context.PlanSha256,
                ["image"] = new JsonObject
                {
                    ["reference"] = identity.Image.Reference,
                    ["imageId"] = identity.Image.ImageId,
                    ["sizeBytes"] = identity.Image.SizeBytes
                },
                ["sourceRevision"] = context.SourceRevision,
                ["policy"] = new JsonObject
                {
                    ["id"] = policy.Id,
                    ["sha256"] = policySha256
                },
                ["capabilities"] = new JsonArray(identity.Capabilities
                    .Select(static capability => (JsonNode?)JsonValue.Create(capability))
                    .ToArray()),
                ["sourceMappingKind"] = identity.SourceMappingKind,
                ["environment"] = new JsonObject
                {
                    ["runnerId"] = identity.Environment.RunnerId,
                    ["operatingSystem"] = identity.Environment.OperatingSystem,
                    ["architecture"] = identity.Environment.Architecture,
                    ["nanoCpus"] = identity.Environment.NanoCpus,
                    ["memoryLimitBytes"] = identity.Environment.MemoryLimitBytes
                },
                ["completedAtUtc"] = DateTimeOffset.UtcNow.ToString(
                    "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                    CultureInfo.InvariantCulture),
                ["result"] = "passed",
                ["scenarios"] = scenarios
            };
        }

        private async Task CollectScenarioAsync(
            string scenario,
            string? methodFilter,
            ScenarioPolicy scenarioPolicy)
        {
            Console.WriteLine($"Collecting {scenario} cold samples...");
            var cold = new List<SampleValue>(policy.SampleCounts.Cold);
            for (var index = 0; index < policy.SampleCounts.Cold; index++)
                cold.Add((await MeasureAsync(scenario, methodFilter).ConfigureAwait(false)).Sample);
            Console.WriteLine($"Warming {scenario} immutable image (unmeasured)...");
            _ = await MeasureAsync(scenario, methodFilter).ConfigureAwait(false);
            Console.WriteLine($"Collecting {scenario} warm samples...");
            var warm = new List<SampleValue>(policy.SampleCounts.Warm);
            for (var index = 0; index < policy.SampleCounts.Warm; index++)
                warm.Add((await MeasureAsync(scenario, methodFilter).ConfigureAwait(false)).Sample);
            ValidateSamples(
                $"{scenario}.cold",
                cold,
                policy.SampleCounts.Cold,
                scenarioPolicy.Cold,
                _identity!.Environment.MemoryLimitBytes);
            ValidateSamples(
                $"{scenario}.warm",
                warm,
                policy.SampleCounts.Warm,
                scenarioPolicy.Warm,
                _identity.Environment.MemoryLimitBytes);
            _scenarios.Add(scenario, new CollectedScenario(cold, warm));
        }

        private async Task<SampleResponse> MeasureAsync(string scenario, string? methodFilter)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "internal/v1/performance/samples")
            {
                Content = JsonContent.Create(
                    new SampleRequest(
                        context.ProfileId,
                        context.PlanSha256,
                        artifactRef,
                        context.SecurityPolicyId,
                        scenario,
                        methodFilter),
                    options: PascalJson)
            };
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (body.Length > 4096)
                    body = body[..4096];
                throw new HttpRequestException(
                    $"Supervisor sample returned HTTP {(int)response.StatusCode}: {body}");
            }
            var sample = await response.Content.ReadFromJsonAsync<SampleResponse>(
                PascalJson,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Supervisor returned an empty performance sample.");
            ValidateResponse(sample, scenario);
            return sample with
            {
                Sample = sample.Sample with
                {
                    OperationId = sample.OperationId,
                    ResourceSampleCount = sample.ResourceSampleCount,
                    CompletedAtUtc = sample.CompletedAtUtc
                }
            };
        }

        private void ValidateResponse(SampleResponse response, string scenario)
        {
            var now = DateTimeOffset.UtcNow;
            if (response.ProfileId != context.ProfileId || response.Scenario != scenario ||
                !IsOperationId(response.OperationId) ||
                !_operationIds.Add(response.OperationId) ||
                response.ResourceSampleCount < 1 ||
                response.Sample.LatencyMilliseconds is <= 0 or > 120_000 ||
                response.Sample.PeakMemoryBytes <= 0 ||
                response.CompletedAtUtc.Offset != TimeSpan.Zero ||
                response.CompletedAtUtc < _collectionStartedAtUtc.AddMinutes(-1) ||
                response.CompletedAtUtc > now.AddMinutes(1))
            {
                throw new InvalidDataException(
                    "Supervisor returned an invalid or replayed performance sample contract.");
            }
            if (scenario == "mapping" && response.DistinctSequencePointRangeCount < 2)
                throw new PerformanceGateException("The mapping sample lacks distinct sequence-point ranges.");
            var canonicalCapabilities = response.Capabilities.Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray();
            if (!response.Capabilities.SequenceEqual(canonicalCapabilities, StringComparer.Ordinal) ||
                !response.Capabilities.Contains("run", StringComparer.Ordinal) ||
                response.Capabilities.Any(static value => value is not (
                    "run" or "jit-asm" or "inspection" or "execution-flow")) ||
                response.SourceMappingKind is not (
                    "not-applicable" or "none" or "linux-profiler" or "checked-jit-debug-info") ||
                response.Environment.RunnerId != "runtime-preflight-linux-x64-v1" ||
                response.Environment.OperatingSystem != "linux" ||
                response.Environment.Architecture != "x64" ||
                response.Environment.NanoCpus != policy.ResourceLimits.NanoCpus ||
                !policy.ResourceLimits.AllowedMemoryBytes.Contains(response.Environment.MemoryLimitBytes) ||
                response.Sample.PeakMemoryBytes > response.Environment.MemoryLimitBytes ||
                response.Image.Reference != context.ImageReference ||
                response.Image.ImageId != context.ImageId ||
                response.Image.SizeBytes != context.ImageSizeBytes ||
                !response.Capabilities.SequenceEqual(context.Capabilities, StringComparer.Ordinal) ||
                response.SourceMappingKind != context.SourceMappingKind ||
                !IsImmutableReference(response.Image.Reference) ||
                !IsSha256(response.Image.ImageId) || response.Image.SizeBytes <= 0 ||
                response.Image.SizeBytes > policy.Image.MaximumSizeBytes)
            {
                throw new InvalidDataException("Supervisor sample identity or environment is invalid.");
            }
            if (_identity is null)
            {
                _identity = response;
                return;
            }
            if (_identity.Image != response.Image ||
                !_identity.Capabilities.SequenceEqual(response.Capabilities, StringComparer.Ordinal) ||
                _identity.SourceMappingKind != response.SourceMappingKind ||
                _identity.Environment != response.Environment)
            {
                throw new PerformanceGateException("Runtime image identity or environment drifted between samples.");
            }
        }

        private static JsonArray SamplesJson(IEnumerable<SampleValue> samples) => new(
            samples.Select(static sample => (JsonNode)new JsonObject
            {
                ["latencyMilliseconds"] = sample.LatencyMilliseconds,
                ["peakMemoryBytes"] = sample.PeakMemoryBytes,
                ["operationId"] = sample.OperationId,
                ["resourceSampleCount"] = sample.ResourceSampleCount,
                ["completedAtUtc"] = sample.CompletedAtUtc.ToUniversalTime().ToString(
                    "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                    CultureInfo.InvariantCulture)
            }).ToArray());

        private static bool IsImmutableReference(string value)
        {
            var marker = value.LastIndexOf("@sha256:", StringComparison.Ordinal);
            return marker > 0 && marker + 8 + 64 == value.Length &&
                value[(marker + 8)..].All(static character =>
                    char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');
        }

        private static bool IsSha256(string value) =>
            value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal) &&
            value[7..].All(static character =>
                char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');

        private static bool IsOperationId(string value) =>
            value.Length == 35 && value.StartsWith("op_", StringComparison.Ordinal) &&
            value[3..].All(static character =>
                char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');
    }
}

sealed class PreflightOptions
{
    public const string Usage = """
        Usage:
          dotnet run eng/performance/runtime-performance-preflight.cs -- [options]

        Required live options:
          --supervisor-base-address <url>
          --repository-root <path>
          --profile <profiles/runtimes/candidates/<profile>.json>
          --preflight-profile <profiles/runtime-promotion-plans/<profile>.profile.json>
          --plan <profiles/runtime-promotion-plans/<profile>.json>
          --artifact-ref <sha256:...>
          --policy <profiles/runtime-performance-policies/<policy>.json>
          --output <profiles/runtime-promotion-evidence/<profile>/performance.json>

        Authentication and JIT:
          --token-file <path>       Preferred; otherwise use SHARPLABNEXT_INTERNAL_SERVICE_TOKEN.
          --method-filter <method>  Required when the selected profile supports jit-asm.

        Other:
          --overall-timeout-seconds <60..7200>  Default: 1800.
          --self-test
          --help
        """;

    public bool ShowHelp { get; private set; }
    public bool SelfTest { get; private set; }
    public string? SupervisorBaseAddress { get; private set; }
    public string? RepositoryRoot { get; private set; }
    public string? ProfilePath { get; private set; }
    public string? PreflightProfilePath { get; private set; }
    public string? PlanPath { get; private set; }
    public string? ArtifactRef { get; private set; }
    public string? MethodFilter { get; private set; }
    public string? PolicyPath { get; private set; }
    public string? OutputPath { get; private set; }
    public string? TokenFile { get; private set; }
    public int OverallTimeoutSeconds { get; private set; } = 1800;

    public static PreflightOptions Parse(string[] args)
    {
        var options = new PreflightOptions();
        for (var index = 0; index < args.Length; index++)
        {
            string Value() => index + 1 < args.Length
                ? args[++index]
                : throw new ArgumentException($"Missing value for {args[index]}.");
            switch (args[index])
            {
                case "--help" or "-h": options.ShowHelp = true; break;
                case "--self-test": options.SelfTest = true; break;
                case "--supervisor-base-address": options.SupervisorBaseAddress = Value(); break;
                case "--repository-root": options.RepositoryRoot = Value(); break;
                case "--profile": options.ProfilePath = Value(); break;
                case "--preflight-profile": options.PreflightProfilePath = Value(); break;
                case "--plan": options.PlanPath = Value(); break;
                case "--artifact-ref": options.ArtifactRef = Value(); break;
                case "--method-filter": options.MethodFilter = Value(); break;
                case "--policy": options.PolicyPath = Value(); break;
                case "--output": options.OutputPath = Value(); break;
                case "--token-file": options.TokenFile = Value(); break;
                case "--overall-timeout-seconds":
                    if (!int.TryParse(Value(), CultureInfo.InvariantCulture, out var timeout) ||
                        timeout is < 60 or > 7200)
                        throw new ArgumentException("--overall-timeout-seconds must be between 60 and 7200.");
                    options.OverallTimeoutSeconds = timeout;
                    break;
                default: throw new ArgumentException($"Unknown option '{args[index]}'.");
            }
        }
        if (options.ShowHelp || options.SelfTest)
            return options;
        foreach (var (value, name) in new[]
                 {
                     (options.SupervisorBaseAddress, "--supervisor-base-address"),
                     (options.RepositoryRoot, "--repository-root"),
                     (options.ProfilePath, "--profile"),
                     (options.PreflightProfilePath, "--preflight-profile"),
                     (options.PlanPath, "--plan"),
                     (options.ArtifactRef, "--artifact-ref"),
                     (options.PolicyPath, "--policy"),
                     (options.OutputPath, "--output")
                 })
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"{name} is required.");
        }
        if (!IsSha256(options.ArtifactRef!))
            throw new ArgumentException("--artifact-ref must be a canonical sha256 reference.");
        if (options.MethodFilter is { Length: > 256 } || options.MethodFilter?.Any(char.IsControl) == true)
            throw new ArgumentException("--method-filter is invalid.");
        return options;
    }

    private static bool IsSha256(string value) => value.Length == 71 &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value[7..].All(static character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');

}

sealed class PerformanceGateException(string message, Exception? innerException = null) :
    Exception(message, innerException);

sealed record PerformancePolicy(
    int SchemaVersion,
    string Id,
    SampleCounts SampleCounts,
    ResourceLimits ResourceLimits,
    ImageBudget Image,
    ScenarioPolicies Scenarios);

sealed record SampleCounts(int Cold, int Warm);
sealed record ResourceLimits(long NanoCpus, IReadOnlyList<long> AllowedMemoryBytes);
sealed record ImageBudget(long MaximumSizeBytes);
sealed record ScenarioPolicies(ScenarioPolicy Run, ScenarioPolicy Jit, ScenarioPolicy Mapping);
sealed record ScenarioPolicy(SampleBudget Cold, SampleBudget Warm);
sealed record SampleBudget(
    double MaximumP95LatencyMilliseconds,
    double MaximumSampleLatencyMilliseconds,
    long MaximumPeakMemoryBytes);

sealed record SampleRequest(
    string RuntimeProfileId,
    string PlanSha256,
    string ArtifactRef,
    string SecurityPolicyId,
    string Scenario,
    string? MethodFilter);

sealed record SampleResponse(
    string ProfileId,
    string Scenario,
    string OperationId,
    SampleImage Image,
    IReadOnlyList<string> Capabilities,
    string SourceMappingKind,
    SampleEnvironment Environment,
    SampleValue Sample,
    int ResourceSampleCount,
    int DistinctSequencePointRangeCount,
    DateTimeOffset CompletedAtUtc);

sealed record SampleImage(string Reference, string ImageId, long SizeBytes);
sealed record SampleEnvironment(
    string RunnerId,
    string OperatingSystem,
    string Architecture,
    long NanoCpus,
    long MemoryLimitBytes);
sealed record SampleValue(
    double LatencyMilliseconds,
    long PeakMemoryBytes,
    string? OperationId = null,
    int ResourceSampleCount = 0,
    DateTimeOffset CompletedAtUtc = default);
sealed record CollectedScenario(IReadOnlyList<SampleValue> Cold, IReadOnlyList<SampleValue> Warm);
sealed record PromotionInput(string Path, string Description, byte[] Bytes);
sealed record OutputSnapshot(bool Exists, long Length, byte[] Sha256);
