#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property ManagePackageVersionsCentrally=false
#:property EnableTrimAnalyzer=false
#:property EnableAotAnalyzer=false
#:package JsonSchema.Net@8.0.5

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Json.Schema;

return await PerformanceGateApplication.RunAsync(args);

static class PerformanceGateApplication
{
    private const int ReportSchemaVersion = 1;
    private static readonly JsonSerializerOptions ReportJson = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };
    private static readonly JsonSerializerOptions ExactNameJson = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    internal static JsonSerializerOptions BusinessJson { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = PascalCaseJsonNamingPolicy.Instance,
        DictionaryKeyPolicy = null,
        PropertyNameCaseInsensitive = false,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    private static readonly ScenarioDefinition[] Scenarios =
    [
        new("build", "Build", "il", null, ScenarioTerminal.Build),
        new("il-transform", "IL transform", "il", null, ScenarioTerminal.ArtifactRender),
        new("decompiled-csharp-transform", "Decompiled C# transform", "decompiled-csharp", null, ScenarioTerminal.ArtifactRender),
        new("run", "Run end-to-end", "run", "dotnet-10-linux-x64", ScenarioTerminal.Run),
        new("jit", "JIT end-to-end", "jit-asm", "dotnet-10-linux-x64", ScenarioTerminal.Jit)
    ];
    private static readonly double[] PercentileSelfTestValues = [8d, 1d, 3d, 2d, 5d, 4d, 6d, 7d, 9d, 10d];

    public static Task<int> RunAsync(string[] args) => RunAsync(args, httpHandler: null);

    private static async Task<int> RunAsync(string[] args, HttpMessageHandler? httpHandler)
    {
        GateOptions options;
        try
        {
            options = GateOptions.Parse(args);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine(GateOptions.Usage);
            return 2;
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(GateOptions.Usage);
            return 0;
        }

        try
        {
            if (options.SelfTest)
                return await RunSelfTestAsync(options);

            return await RunLiveGateAsync(options, httpHandler);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Performance gate was cancelled or exceeded its overall timeout.");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Performance gate infrastructure failure: {exception.Message}");
            return 2;
        }
    }

    private static async Task<int> RunLiveGateAsync(GateOptions options, HttpMessageHandler? httpHandler)
    {
        var configurationPath = Path.GetFullPath(options.ThresholdsPath);
        var configurationBytes = await File.ReadAllBytesAsync(configurationPath);
        var configuration = JsonSerializer.Deserialize<ThresholdConfiguration>(configurationBytes, ReportJson) ?? throw new InvalidDataException("Performance threshold configuration is empty.");
        ThresholdConfigurationValidator.Validate(configuration, Scenarios);
        var reportSchemaPath = Path.Combine(Path.GetDirectoryName(configurationPath)!, "report.schema.v1.json");
        var reportSchema = await ReportSchemaValidator.LoadAsync(reportSchemaPath);

        var outputPath = Path.GetFullPath(options.OutputPath);
        var startedAt = DateTimeOffset.UtcNow;
        using var overallTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(configuration.Workload.OverallTimeoutMinutes));
        using var http = httpHandler is null
            ? new HttpClient() : new HttpClient(httpHandler, disposeHandler: true);
        http.BaseAddress = options.BaseAddress;
        http.Timeout = TimeSpan.FromSeconds(configuration.Workload.OperationTimeoutSeconds + 30);
        var client = await GatewayPerformanceClient.CreateAsync(http, configuration.Workload, overallTimeout.Token);

        Console.WriteLine($"Performance gate {configuration.ProfileId}: {options.BaseAddress} " + $"({configuration.Workload.BaselineSamplesPerScenario} baseline samples/scenario; " + $"concurrency {string.Join('/', configuration.Workload.ConcurrencyLevels)})");

        var warmups = new List<SampleMeasurement>();
        foreach (var scenario in Scenarios)
        {
            for (var index = 0; index < configuration.Workload.WarmupSamplesPerScenario; index++)
            {
                var sample = await RunSampleAsync(client, scenario, $"warmup-{scenario.Id}-{index}", concurrencyLevel: 1, overallTimeout.Token);
                warmups.Add(sample);
                WriteSampleProgress("warmup", sample);
            }
        }

        var baseline = new List<ScenarioMeasurement>();
        foreach (var scenario in Scenarios)
        {
            var samples = await RunBoundedSamplesAsync(client, Enumerable.Range(0, configuration.Workload.BaselineSamplesPerScenario).Select(index => new SampleWorkItem(scenario, $"baseline-{scenario.Id}-{index}", ConcurrencyLevel: configuration.Workload.BaselineMaxConcurrency)).ToArray(), configuration.Workload.BaselineMaxConcurrency, overallTimeout.Token);
            var measurement = SummarizeScenario(scenario, concurrencyLevel: 1, samples);
            baseline.Add(measurement);
            WriteSummaryProgress("baseline", measurement);
        }

        var concurrencyMeasurements = new List<ConcurrencyMeasurement>();
        foreach (var concurrencyLevel in configuration.Workload.ConcurrencyLevels)
        {
            var work = CreateMixedConcurrencyWorkload(concurrencyLevel);
            var memorySamples = new ConcurrentQueue<SystemMemorySnapshot>();
            using var memoryCancellation = CancellationTokenSource.CreateLinkedTokenSource(overallTimeout.Token);
            var memorySampler = SampleMemoryAsync(memorySamples, configuration.Workload.MemorySampleIntervalMilliseconds, memoryCancellation.Token);
            var batchStartedAt = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            SampleMeasurement[] samples;
            try
            {
                samples = await RunSimultaneousSamplesAsync(client, work, overallTimeout.Token);
            }
            finally
            {
                stopwatch.Stop();
                memoryCancellation.Cancel();
                await IgnoreCancellationAsync(memorySampler);
                if (SystemMemoryReader.TryRead() is { } finalMemory)
                    memorySamples.Enqueue(finalMemory);
            }

            var scenarioMeasurements = Scenarios.Select(scenario => SummarizeScenario(scenario, concurrencyLevel, samples.Where(sample => sample.ScenarioId == scenario.Id).ToArray())).ToArray();
            var measurement = new ConcurrencyMeasurement(concurrencyLevel, batchStartedAt, RoundMilliseconds(stopwatch.Elapsed.TotalMilliseconds), SystemMemorySummary.Create(memorySamples), scenarioMeasurements);
            concurrencyMeasurements.Add(measurement);
            Console.WriteLine($"concurrency {concurrencyLevel}: {measurement.DurationMilliseconds:N1} ms, " + $"failures={scenarioMeasurements.Sum(static scenario => scenario.FailureCount)}, " + $"min-available-memory={FormatBytes(measurement.SystemMemory.MinimumAvailableBytes)}");
            foreach (var scenario in scenarioMeasurements)
                WriteSummaryProgress($"concurrency-{concurrencyLevel}", scenario);
        }

        var reportWithoutViolations = new GateReport(
            ReportSchemaVersion,
            startedAt,
            DateTimeOffset.UtcNow,
            new ThresholdProfileReference(configuration.SchemaVersion, configuration.ProfileId, Path.GetFileName(configurationPath), $"sha256:{Convert.ToHexString(SHA256.HashData(configurationBytes)).ToLowerInvariant()}"),
            client.Target,
            RuntimeEnvironmentInfo.Create(),
            configuration.Workload,
            warmups,
            baseline,
            concurrencyMeasurements,
            [],
            Passed: false);
        var violations = PerformanceBudgetEvaluator.Evaluate(reportWithoutViolations, configuration);
        var report = reportWithoutViolations with { CompletedAtUtc = DateTimeOffset.UtcNow, Violations = violations, Passed = violations.Count == 0 };

        await WriteReportAsync(outputPath, report, reportSchema, overallTimeout.Token);
        Console.WriteLine($"Performance report: {outputPath}");
        if (report.Passed)
        {
            Console.WriteLine("PASS performance release gate");
            return 0;
        }

        foreach (var violation in violations)
            Console.Error.WriteLine($"FAIL {violation.Scope}: {violation.Message}");
        return 1;
    }

    private static async Task<int> RunSelfTestAsync(GateOptions options)
    {
        var failures = new List<string>();
        await CheckAsync("nearest-rank percentile", () =>
        {
            Require(Statistics.Percentile(PercentileSelfTestValues, 0.50) == 5d, "p50 must use nearest rank.");
            Require(Statistics.Percentile(PercentileSelfTestValues, 0.95) == 10d, "p95 must use nearest rank.");
            Require(Statistics.Percentile([1d, 2d], 0.95) == 2d, "Small-sample p95 is wrong.");
            return Task.CompletedTask;
        }, failures);

        var configurationPath = Path.GetFullPath(options.ThresholdsPath);
        var configurationBytes = await File.ReadAllBytesAsync(configurationPath);
        var configuration = JsonSerializer.Deserialize<ThresholdConfiguration>(configurationBytes, ReportJson) ?? throw new InvalidDataException("Performance threshold configuration is empty.");

        await CheckAsync("threshold configuration", () =>
        {
            ThresholdConfigurationValidator.Validate(configuration, Scenarios);
            return Task.CompletedTask;
        }, failures);

        await CheckAsync("threshold boundaries", () =>
        {
            var budget = configuration.Scenarios["build"].Baseline;
            var passing = CreateSelfTestReport(configuration, budget.MaxP50Milliseconds, budget.MaxP95Milliseconds);
            Require(PerformanceBudgetEvaluator.Evaluate(passing, configuration).Count == 0, "Values equal to the approved budget must pass.");
            var failing = CreateSelfTestReport(configuration, budget.MaxP50Milliseconds, budget.MaxP95Milliseconds + 0.001);
            Require(PerformanceBudgetEvaluator.Evaluate(failing, configuration).Any(static violation => violation.Metric == "latency.p95Milliseconds"), "A p95 value above budget must fail.");
            return Task.CompletedTask;
        }, failures);

        await CheckAsync("failure classification", () =>
        {
            Require(PerformanceFailureClassifier.IsSampleFailure(new OperationTerminalFailureException("expected terminal failure")), "Operation terminal failures must remain release-gate sample failures.");
            Require(!PerformanceFailureClassifier.IsSampleFailure(new HttpRequestException("transport")), "HTTP failures must be infrastructure failures.");
            Require(!PerformanceFailureClassifier.IsSampleFailure(new JsonException("protocol JSON")), "Protocol JSON failures must be infrastructure failures.");
            Require(!PerformanceFailureClassifier.IsSampleFailure(new InvalidDataException("protocol contract")), "Protocol contract failures must be infrastructure failures.");
            return Task.CompletedTask;
        }, failures);

        await CheckAsync("operation cancellation contract", async () =>
        {
            string? requestBody = null;
            using var successHttp = new HttpClient(new DelegateHttpMessageHandler(async (request, cancellationToken) =>
            {
                requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            }))
            {
                BaseAddress = new Uri("http://127.0.0.1:8080")
            };
            var httpJson = BusinessJson;
            await GatewayPerformanceClient.CancelOperationAsync(successHttp, httpJson, "op_self_test", CancellationToken.None);
            using var body = JsonDocument.Parse(requestBody!);
            Require(body.RootElement.GetProperty("OperationId").GetString() == "op_self_test", "Cancellation body must include operationId.");
            Require(!body.RootElement.TryGetProperty("operationId", out _), "Cancellation body must not use the legacy camelCase operationId wire name.");
            Require(body.RootElement.GetProperty("Reason").GetString() == "performance-gate-timeout", "Cancellation body must include the timeout reason.");

            using var failureHttp = new HttpClient(new DelegateHttpMessageHandler(static (_, _) =>
                Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("rejected")
                })))
            {
                BaseAddress = new Uri("http://127.0.0.1:8080")
            };
            await RequireThrowsAsync<HttpRequestException>(() => GatewayPerformanceClient.CancelOperationAsync(failureHttp, httpJson, "op_self_test", CancellationToken.None));
        }, failures);

        await CheckAsync("Gateway dispatch event contract", () =>
        {
            var acceptedAt = DateTimeOffset.UnixEpoch.AddSeconds(1);
            var dispatchedAt = acceptedAt.AddMilliseconds(25);
            Require(GatewayPerformanceClient.CalculateGatewayDispatchWaitMilliseconds([SelfTestOperationEvent("accepted", acceptedAt), SelfTestOperationEvent("progress", dispatchedAt)]) == 25, "Valid accepted-to-dispatch timing is wrong.");
            RequireThrows(() => GatewayPerformanceClient.CalculateGatewayDispatchWaitMilliseconds([SelfTestOperationEvent("progress", dispatchedAt), SelfTestOperationEvent("completed", dispatchedAt)]), "A stream without a first accepted event must be rejected.");
            RequireThrows(() => GatewayPerformanceClient.CalculateGatewayDispatchWaitMilliseconds([SelfTestOperationEvent("accepted", acceptedAt), SelfTestOperationEvent("accepted", dispatchedAt)]), "A duplicate accepted event must be rejected.");
            RequireThrows(() => GatewayPerformanceClient.CalculateGatewayDispatchWaitMilliseconds([SelfTestOperationEvent("progress", acceptedAt), SelfTestOperationEvent("accepted", dispatchedAt)]), "A non-first accepted event must be rejected.");
            RequireThrows(() => GatewayPerformanceClient.CalculateGatewayDispatchWaitMilliseconds([SelfTestOperationEvent("accepted", acceptedAt)]), "An accepted event without a subsequent dispatch event must be rejected.");
            RequireThrows(() => GatewayPerformanceClient.CalculateGatewayDispatchWaitMilliseconds([SelfTestOperationEvent("accepted", acceptedAt), SelfTestOperationEvent("progress", acceptedAt.AddTicks(-1))]), "A timestamp reversal must be rejected.");
            var legacyCamelCaseEvent = JsonSerializer.SerializeToElement(new { TimestampUtc = acceptedAt, Payload = new { kind = "accepted" } }, ExactNameJson);
            RequireThrows(() => GatewayPerformanceClient.CalculateGatewayDispatchWaitMilliseconds([legacyCamelCaseEvent, SelfTestOperationEvent("progress", dispatchedAt)]), "Legacy camelCase event properties must be rejected.");
            return Task.CompletedTask;
        }, failures);

        await CheckAsync("RunAsync infrastructure exit codes", async () =>
        {
            var outputPath = Path.Combine(Path.GetTempPath(), $"SharpLabNext.performance-infrastructure-self-test.{Guid.NewGuid():N}.json");
            var liveArgs = new[]
            {
                "--base-address", "http://performance-fixture.test",
                "--thresholds", configurationPath,
                "--output", outputPath
            };
            var fixtures = new HttpMessageHandler[]
            {
                new DelegateHttpMessageHandler(static (_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("simulated transport disconnect"))),
                new DelegateHttpMessageHandler(static (_, _) =>
                    Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
                    {
                        Content = new StringContent("unavailable")
                    })),
                new DelegateHttpMessageHandler(static (request, _) =>
                {
                    var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
                    if (request.RequestUri?.AbsolutePath == "/api/v1/catalog")
                        response.Content = new StringContent("{ malformed-json", Encoding.UTF8, "application/json");
                    return Task.FromResult(response);
                }),
                new DelegateHttpMessageHandler(static (_, _) => Task.FromException<HttpResponseMessage>(new OperationCanceledException("simulated transport timeout")))
            };
            try
            {
                foreach (var fixture in fixtures)
                {
                    var exitCode = await RunAsync(liveArgs, fixture);
                    Require(exitCode == 2, $"Infrastructure fixture returned exit code {exitCode} instead of 2.");
                    Require(!File.Exists(outputPath), "Infrastructure failure must not write a release report.");
                }
            }
            finally
            {
                if (File.Exists(outputPath))
                    File.Delete(outputPath);
            }
        }, failures);

        await CheckAsync("report schema", async () =>
        {
            var schemaPath = Path.Combine(Path.GetDirectoryName(configurationPath)!, "report.schema.v1.json");
            var schema = await ReportSchemaValidator.LoadAsync(schemaPath);

            var report = CreateSelfTestReport(configuration, 1, 2);
            var json = JsonSerializer.SerializeToUtf8Bytes(report, ReportJson);
            using var document = JsonDocument.Parse(json);
            ReportSchemaValidator.Validate(schema, document.RootElement);

            var missingPassed = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidOperationException("Self-test report JSON is empty.");
            missingPassed.Remove("passed");
            using var invalidDocument = JsonDocument.Parse(missingPassed.ToJsonString());
            RequireThrows(() => ReportSchemaValidator.Validate(schema, invalidDocument.RootElement), "A report without 'passed' must be rejected.");

            var unexpectedNestedProperty = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidOperationException("Self-test report JSON is empty.");
            unexpectedNestedProperty["environment"]!["unexpected"] = true;
            using var unexpectedDocument = JsonDocument.Parse(unexpectedNestedProperty.ToJsonString());
            RequireThrows(() => ReportSchemaValidator.Validate(schema, unexpectedDocument.RootElement), "A report with an unexpected nested property must be rejected.");
        }, failures);

        if (failures.Count == 0)
        {
            Console.WriteLine("PASS performance gate self-test");
            return 0;
        }

        foreach (var failure in failures)
            Console.Error.WriteLine($"FAIL {failure}");
        return 1;
    }

    private static GateReport CreateSelfTestReport(ThresholdConfiguration configuration, double buildP50, double buildP95)
    {
        var now = DateTimeOffset.UnixEpoch;
        var baseline = Scenarios.Select(scenario =>
        {
            var budget = configuration.Scenarios[scenario.Id].Baseline;
            var p50 = scenario.Id == "build" ? buildP50 : Math.Min(1, budget.MaxP50Milliseconds);
            var p95 = scenario.Id == "build" ? buildP95 : Math.Min(2, budget.MaxP95Milliseconds);
            return SelfTestScenarioMeasurement(scenario, 1, p50, p95);
        }).ToArray();
        var concurrency = configuration.Workload.ConcurrencyLevels.Select(level =>
        {
            var batchBudget = configuration.Concurrency[level.ToString(System.Globalization.CultureInfo.InvariantCulture)];
            var measurements = Scenarios.Select(scenario =>
            {
                var budget = configuration.Scenarios[scenario.Id].Concurrency[
                    level.ToString(System.Globalization.CultureInfo.InvariantCulture)];
                return SelfTestScenarioMeasurement(scenario, level, Math.Min(1, budget.MaxP50Milliseconds), Math.Min(2, budget.MaxP95Milliseconds));
            }).ToArray();
            return new ConcurrencyMeasurement(level, now, Math.Min(1, batchBudget.MaxBatchDurationMilliseconds), new SystemMemorySummary(Supported: true, TotalBytes: 16L * 1024 * 1024 * 1024, AvailableBeforeBytes: 12L * 1024 * 1024 * 1024, MinimumAvailableBytes: 11L * 1024 * 1024 * 1024, AvailableAfterBytes: 12L * 1024 * 1024 * 1024, PeakUsedDeltaBytes: 1L * 1024 * 1024 * 1024), measurements);
        }).ToArray();
        return new GateReport(
            ReportSchemaVersion,
            now,
            now,
            new ThresholdProfileReference(configuration.SchemaVersion, configuration.ProfileId, "thresholds.v1.json", $"sha256:{new string('0', 64)}"),
            new GatewayTarget(new Uri("http://127.0.0.1:8080"), "self-test", "self-test"),
            new RuntimeEnvironmentInfo("SelfTest", "X64", "X64", 8, ".NET self-test", 16L * 1024 * 1024 * 1024),
            configuration.Workload,
            Scenarios.Select((scenario, index) => new SampleMeasurement($"self-test-warmup-{scenario.Id}-{index}", scenario.Id, ConcurrencyLevel: 1, DurationMilliseconds: 1, GatewayDispatchWaitMilliseconds: 0, OperationCount: 1, Succeeded: true, Error: null)).ToArray(),
            baseline,
            concurrency,
            [],
            Passed: false);
    }

    private static JsonElement SelfTestOperationEvent(string kind, DateTimeOffset timestampUtc) => JsonSerializer.SerializeToElement(new { timestampUtc, payload = new { kind } }, BusinessJson);

    private static ScenarioMeasurement SelfTestScenarioMeasurement(ScenarioDefinition scenario, int concurrencyLevel, double p50, double p95)
    {
        var sampleCount = concurrencyLevel == 1 ? 10 : Math.Max(2, concurrencyLevel / Scenarios.Length);
        var values = Enumerable.Repeat(p50, sampleCount - 1).Append(p95).ToArray();
        var samples = values.Select((value, index) => new SampleMeasurement($"self-test-{scenario.Id}-{index}", scenario.Id, concurrencyLevel, value, GatewayDispatchWaitMilliseconds: 0, OperationCount: 1, Succeeded: true, Error: null)).ToArray();
        return new ScenarioMeasurement(scenario.Id, scenario.DisplayName, concurrencyLevel, samples.Length, samples.Length, FailureCount: 0, new StatisticalSummary(samples.Length, p50, p50, p95, p95), new StatisticalSummary(samples.Length, 0, 0, 0, 0), samples);
    }

    private static List<SampleWorkItem> CreateMixedConcurrencyWorkload(int concurrencyLevel)
    {
        Require(concurrencyLevel % Scenarios.Length == 0, $"Concurrency {concurrencyLevel} must divide evenly across {Scenarios.Length} scenarios.");
        var work = new List<SampleWorkItem>(concurrencyLevel);
        var samplesPerScenario = concurrencyLevel / Scenarios.Length;
        foreach (var scenario in Scenarios)
        {
            for (var index = 0; index < samplesPerScenario; index++)
            {
                work.Add(new SampleWorkItem(scenario, $"concurrency-{concurrencyLevel}-{scenario.Id}-{index}", concurrencyLevel));
            }
        }
        return work;
    }

    private static async Task<SampleMeasurement[]> RunBoundedSamplesAsync(GatewayPerformanceClient client, IReadOnlyList<SampleWorkItem> work, int maxConcurrency, CancellationToken cancellationToken)
    {
        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var tasks = work.Select(async item =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                return await RunSampleAsync(client, item.Scenario, item.SampleId, item.ConcurrencyLevel, cancellationToken);
            }
            finally
            {
                semaphore.Release();
            }
        });
        return await Task.WhenAll(tasks);
    }

    private static async Task<SampleMeasurement[]> RunSimultaneousSamplesAsync(GatewayPerformanceClient client, IReadOnlyList<SampleWorkItem> work, CancellationToken cancellationToken)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = work.Select(async item =>
        {
            await start.Task.WaitAsync(cancellationToken);
            return await RunSampleAsync(client, item.Scenario, item.SampleId, item.ConcurrencyLevel, cancellationToken);
        }).ToArray();
        start.SetResult();
        return await Task.WhenAll(tasks);
    }

    private static async Task<SampleMeasurement> RunSampleAsync(GatewayPerformanceClient client, ScenarioDefinition scenario, string sampleId, int concurrencyLevel, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var execution = await client.ExecuteAsync(scenario, sampleId, cancellationToken);
            stopwatch.Stop();
            return new SampleMeasurement(sampleId, scenario.Id, concurrencyLevel, RoundMilliseconds(stopwatch.Elapsed.TotalMilliseconds), RoundMilliseconds(execution.GatewayDispatchWaitMilliseconds), execution.OperationCount, Succeeded: true, Error: null);
        }
        catch (PerformanceSampleFailureException exception)
        {
            stopwatch.Stop();
            return new SampleMeasurement(sampleId, scenario.Id, concurrencyLevel, RoundMilliseconds(stopwatch.Elapsed.TotalMilliseconds), GatewayDispatchWaitMilliseconds: 0, OperationCount: 0, Succeeded: false, Error: exception.Message);
        }
    }

    private static ScenarioMeasurement SummarizeScenario(ScenarioDefinition scenario, int concurrencyLevel, SampleMeasurement[] samples)
    {
        var successful = samples.Where(static sample => sample.Succeeded).ToArray();
        return new ScenarioMeasurement(scenario.Id, scenario.DisplayName, concurrencyLevel, samples.Length, successful.Length, samples.Length - successful.Length, Statistics.Summarize(successful.Select(static sample => sample.DurationMilliseconds)), Statistics.Summarize(successful.Select(static sample => sample.GatewayDispatchWaitMilliseconds)), samples);
    }

    private static async Task SampleMemoryAsync(ConcurrentQueue<SystemMemorySnapshot> samples, int intervalMilliseconds, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (SystemMemoryReader.TryRead() is { } sample)
                samples.Enqueue(sample);
            await Task.Delay(intervalMilliseconds, cancellationToken);
        }
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException) { }
    }

    private static async Task WriteReportAsync(string outputPath, GateReport report, JsonSchema reportSchema, CancellationToken cancellationToken)
    {
        var reportBytes = JsonSerializer.SerializeToUtf8Bytes(report, ReportJson);
        using (var reportDocument = JsonDocument.Parse(reportBytes))
            ReportSchemaValidator.Validate(reportSchema, reportDocument.RootElement);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var temporaryPath = $"{outputPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough)) {
                await output.WriteAsync(reportBytes, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, outputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static async Task CheckAsync(string name, Func<Task> test, List<string> failures)
    {
        try
        {
            await test();
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception exception)
        {
            failures.Add($"{name}: {exception.Message}");
        }
    }

    private static void WriteSampleProgress(string scope, SampleMeasurement sample) => Console.WriteLine($"{scope} {sample.ScenarioId}: " + (sample.Succeeded ? $"{sample.DurationMilliseconds:N1} ms" : $"failed ({sample.Error})"));

    private static void WriteSummaryProgress(string scope, ScenarioMeasurement measurement) =>
        Console.WriteLine($"{scope} {measurement.ScenarioId}: " + $"p50={measurement.LatencyMilliseconds.P50:N1} ms, " + $"p95={measurement.LatencyMilliseconds.P95:N1} ms, " + $"gateway-dispatch-p95={measurement.GatewayDispatchWaitMilliseconds.P95:N1} ms, " + $"failures={measurement.FailureCount}");

    private static string FormatBytes(long? bytes) => bytes is null ? "unsupported" : $"{bytes.Value / (1024d * 1024d):N0} MiB";

    private static double RoundMilliseconds(double value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void RequireThrows(Action action, string message)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static async Task RequireThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name} was not thrown.");
    }
}

sealed class GatewayPerformanceClient
{
    private readonly HttpClient http;
    private readonly WorkloadConfiguration workload;
    private readonly JsonSerializerOptions json;
    private readonly string catalogRevision;

    private GatewayPerformanceClient(HttpClient http, WorkloadConfiguration workload, JsonSerializerOptions json, string catalogRevision, GatewayTarget target)
    {
        this.http = http;
        this.workload = workload;
        this.json = json;
        this.catalogRevision = catalogRevision;
        Target = target;
    }

    public GatewayTarget Target { get; }

    public static async Task<GatewayPerformanceClient> CreateAsync(HttpClient http, WorkloadConfiguration workload, CancellationToken cancellationToken)
    {
        var json = PerformanceGateApplication.BusinessJson;
        using var ready = await http.GetAsync("/health/ready", cancellationToken);
        await EnsureSuccessAsync(ready, "Gateway readiness", cancellationToken);
        using var catalogResponse = await http.GetAsync("/api/v1/catalog", cancellationToken);
        await EnsureSuccessAsync(catalogResponse, "Gateway Catalog", cancellationToken);
        using var catalog = JsonDocument.Parse(await catalogResponse.Content.ReadAsByteArrayAsync(cancellationToken));
        var catalogRevision = catalog.RootElement.GetProperty("Revision").GetString() ?? throw new InvalidDataException("Gateway Catalog has no revision.");
        using var systemResponse = await http.GetAsync("/api/v1/system", cancellationToken);
        await EnsureSuccessAsync(systemResponse, "Gateway system identity", cancellationToken);
        using var system = JsonDocument.Parse(await systemResponse.Content.ReadAsByteArrayAsync(cancellationToken));
        var releaseId = system.RootElement.GetProperty("ReleaseId").GetString() ?? throw new InvalidDataException("Gateway system identity has no release ID.");
        return new GatewayPerformanceClient(http, workload, json, catalogRevision, new GatewayTarget(http.BaseAddress!, releaseId, catalogRevision));
    }

    public async Task<PipelineMeasurement> ExecuteAsync(ScenarioDefinition scenario, string sampleId, CancellationToken cancellationToken)
    {
        var resolution = await ResolveAsync(scenario, cancellationToken);
        var pipelineId = RequiredString(resolution, "PipelineResolutionId");
        var effective = resolution.GetProperty("EffectiveSelection");
        var referenceSetId = RequiredString(effective, "ReferenceSetId");
        var stages = resolution.GetProperty("PipelinePlan").GetProperty("Stages").EnumerateArray().ToArray();
        RequireProtocol(stages.Length > 0, "Resolved pipeline has no stages.");

        var invocation = sampleId.Replace('-', '_');
        var source = $$"""
            using System;
            using System.Runtime.CompilerServices;

            public static class Program
            {
                private const string PerformanceGateInvocation = "{{invocation}}";

                [MethodImpl(MethodImplOptions.NoInlining)]
                public static int Add(int left, int right) => left + right;

                public static void Main()
                {
                    Console.WriteLine($"{Add(19, 23)}:perf-ok");
                    GC.KeepAlive(PerformanceGateInvocation);
                }
            }
            """;
        var buildOptions = new { configuration = "release", optimize = true, outputKind = "console", allowUnsafe = false, emitPortablePdb = true, nullableContext = "project-default", languageVersion = (string?)null, preprocessorSymbols = Array.Empty<string>(), checkOverflow = false };
        var workspace = new { schemaVersion = 1, revision = 1, selectionRevision = 1, languageId = "csharp", files = new[] { new { path = "Program.cs", version = 1, text = source } }, activeFile = "Program.cs", sourceOrder = new[] { "Program.cs" }, referenceSetId, buildOptions };

        var operations = new List<OperationExecution>();
        var buildIdentity = Identity("build");
        var build = await StartAndWaitAsync("/api/v1/builds", new { buildIdentity.requestId, buildIdentity.idempotencyKey, pipelineResolutionId = pipelineId, toolchainId = RequiredString(effective, "ToolchainId"), referenceSetId, workspace, deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(workload.OperationTimeoutSeconds), options = buildOptions, target = "artifact" }, cancellationToken);
        operations.Add(build);
        RequireProtocol(ResultType(build.Result) == "build", "Build returned the wrong result type.");
        RequireSample(RequiredString(build.Result, "Outcome") == "succeeded", "Build did not succeed.");
        var artifactRef = RequiredString(build.Result, "ArtifactRef");
        if (scenario.Terminal == ScenarioTerminal.Build)
            return Summarize(operations);

        for (var index = 1; index < stages.Length; index++)
        {
            var stage = stages[index];
            var kind = RequiredString(stage, "Kind");
            var stageId = RequiredString(stage, "Id");
            var providerId = RequiredString(stage, "ProviderId");
            OperationExecution execution;
            switch (kind)
            {
                case "transform":
                {
                    var identity = Identity("transform");
                    execution = await StartAndWaitAsync("/api/v1/artifact-transforms", new { identity.requestId, identity.idempotencyKey, pipelineResolutionId = pipelineId, artifactRef, processorId = providerId, transformId = stageId, options = new { preservePortablePdb = true, preserveSequencePoints = true, rewriterProfileId = (string?)null }, deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(workload.OperationTimeoutSeconds) }, cancellationToken);
                    RequireProtocol(ResultType(execution.Result) == "artifact-transform", "Transform returned the wrong result type.");
                    RequireSample(RequiredString(execution.Result, "Outcome") == "succeeded", "Transform did not succeed.");
                    artifactRef = RequiredString(execution.Result, "ArtifactRef");
                    break;
                }
                case "render":
                {
                    var identity = Identity("render");
                    execution = await StartAndWaitAsync("/api/v1/artifact-renders", new { identity.requestId, identity.idempotencyKey, pipelineResolutionId = pipelineId, artifactRef, processorId = providerId, outputId = stageId, options = new { includeSequencePoints = true, includeCompilerGeneratedMembers = true, maxCharacters = 1_000_000 }, deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(workload.OperationTimeoutSeconds) }, cancellationToken);
                    RequireProtocol(ResultType(execution.Result) == "artifact-render", "Render returned the wrong result type.");
                    RequireSample(RequiredString(execution.Result, "Outcome") == "succeeded", "Render did not succeed.");
                    break;
                }
                case "run":
                {
                    var identity = Identity("run");
                    execution = await StartAndWaitAsync("/api/v1/runs", new { identity.requestId, identity.idempotencyKey, pipelineResolutionId = pipelineId, artifactRef, runtimeProfileId = RequiredString(effective, "RuntimeId"), options = new { arguments = Array.Empty<string>(), stdin = (string?)null, instrumentation = "none", securityPolicyId = RequiredString(resolution.GetProperty("PipelinePlan"), "SecurityPolicyId") }, deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(workload.OperationTimeoutSeconds) }, cancellationToken);
                    RequireProtocol(ResultType(execution.Result) == "run", "Run returned the wrong result type.");
                    RequireSample(RequiredString(execution.Result, "Status") == "completed", "Run did not complete.");
                    RequireSample(execution.Result.GetProperty("ExitCode").GetInt32() == 0, "Run returned a non-zero exit code.");
                    RequireSample(DecodeOutput(execution.Events, "stdout").Contains("42:perf-ok", StringComparison.Ordinal), "Run stdout is incorrect.");
                    break;
                }
                case "jit":
                {
                    var identity = Identity("jit");
                    execution = await StartAndWaitAsync("/api/v1/jit", new
                    {
                        identity.requestId,
                        identity.idempotencyKey,
                        pipelineResolutionId = pipelineId,
                        artifactRef,
                        runtimeProfileId = RequiredString(effective, "RuntimeId"),
                        options = new { methodFilter = (string?)null, tieringPolicyId = "tier0-diffable", pgoPolicyId = "disabled", providerId = "coreclr-jitdisasm", securityPolicyId = RequiredString(resolution.GetProperty("PipelinePlan"), "SecurityPolicyId") },
                        deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(workload.OperationTimeoutSeconds)
                    }, cancellationToken);
                    RequireProtocol(ResultType(execution.Result) == "jit", "JIT returned the wrong result type.");
                    RequireSample(RequiredString(execution.Result, "Status") == "completed", "JIT did not complete.");
                    RequireSample(execution.Result.GetProperty("Methods").GetArrayLength() > 0, "JIT returned no methods.");
                    break;
                }
                default:
                    throw new InvalidOperationException($"Unsupported performance pipeline stage '{kind}'.");
            }

            operations.Add(execution);
            if (index != stages.Length - 1)
                continue;

            switch (scenario.Terminal)
            {
                case ScenarioTerminal.ArtifactRender:
                {
                    var content = await ReadResultContentAsync(execution, cancellationToken);
                    RequireSample(scenario.Id == "il-transform" ? content.Contains(".method", StringComparison.OrdinalIgnoreCase) : content.Contains("Program", StringComparison.Ordinal), $"{scenario.DisplayName} content is invalid.");
                    break;
                }
                case ScenarioTerminal.Jit:
                {
                    var content = await ReadResultContentAsync(execution, cancellationToken);
                    RequireSample(content.Contains("Assembly listing for method", StringComparison.Ordinal), "JIT text contains no CoreCLR assembly listing.");
                    break;
                }
            }
            return Summarize(operations);
        }

        throw new InvalidOperationException($"Scenario '{scenario.Id}' produced no terminal stage.");
    }

    private async Task<JsonElement> ResolveAsync(ScenarioDefinition scenario, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync("/api/v1/selections/resolve", new { languageId = "csharp", toolchainId = "roslyn-stable", referenceSetId = "net10-ref", outputId = scenario.OutputId, runtimeId = scenario.RuntimeId, buildMode = "release", catalogRevision, workspaceRevision = 1 }, json, cancellationToken);
        await EnsureSuccessAsync(response, $"Resolve {scenario.Id}", cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
        return document.RootElement.Clone();
    }

    private async Task<OperationExecution> StartAndWaitAsync(string path, object request, CancellationToken cancellationToken)
    {
        using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationTimeout.CancelAfter(TimeSpan.FromSeconds(workload.OperationTimeoutSeconds));
        string? operationId = null;
        try
        {
            using var start = await http.PostAsJsonAsync(path, request, json, operationTimeout.Token);
            await EnsureSuccessAsync(start, path, operationTimeout.Token);
            using var handle = JsonDocument.Parse(await start.Content.ReadAsByteArrayAsync(operationTimeout.Token));
            operationId = RequiredString(handle.RootElement, "OperationId");

            JsonElement state = default;
            while (true)
            {
                using var response = await http.GetAsync($"/api/v1/operations/{operationId}", operationTimeout.Token);
                await EnsureSuccessAsync(response, $"Operation {operationId}", operationTimeout.Token);
                using var stateDocument = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(operationTimeout.Token));
                state = stateDocument.RootElement.Clone();
                var status = RequiredString(state, "Status");
                if (status is "completed" or "failed" or "cancelled")
                    break;
                await Task.Delay(workload.PollIntervalMilliseconds, operationTimeout.Token);
            }

            var terminalStatus = RequiredString(state, "Status");
            if (terminalStatus != "completed")
            {
                var error = state.TryGetProperty("Error", out var errorElement) &&
                    errorElement.ValueKind == JsonValueKind.Object &&
                    errorElement.TryGetProperty("PublicMessage", out var messageElement)
                        ? messageElement.GetString() : null;
                throw new OperationTerminalFailureException($"Operation {operationId} ended as {terminalStatus}: {error ?? "no public error"}");
            }

            using var eventsResponse = await http.GetAsync($"/api/v1/operations/{operationId}/events?FromSequence=0", operationTimeout.Token);
            await EnsureSuccessAsync(eventsResponse, $"Events for {operationId}", operationTimeout.Token);
            var eventText = await eventsResponse.Content.ReadAsStringAsync(operationTimeout.Token);
            var events = eventText.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(static line => line.StartsWith("data: ", StringComparison.Ordinal)).Select(static line =>
                {
                    using var document = JsonDocument.Parse(line["data: ".Length..]);
                    return document.RootElement.Clone();
                }).ToArray();
            RequireProtocol(events.Length > 0, $"Operation {operationId} returned no events.");
            var typedResults = events.Where(static operationEvent => RequiredString(operationEvent.GetProperty("Payload"), "Kind") == "typed-result").Select(static operationEvent => operationEvent.GetProperty("Payload").GetProperty("Result")).ToArray();
            RequireProtocol(typedResults.Length == 1, $"Operation {operationId} returned {typedResults.Length} typed results.");
            return new OperationExecution(operationId, typedResults[0].Clone(), events, CalculateGatewayDispatchWaitMilliseconds(events));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (operationId is not null)
                await TryCancelAsync(operationId);
            throw new TimeoutException($"Operation {operationId ?? path} exceeded {workload.OperationTimeoutSeconds} seconds.");
        }
    }

    private async Task<string> ReadResultContentAsync(OperationExecution execution, CancellationToken cancellationToken)
    {
        var contentProperty = ResultType(execution.Result) switch
        {
            "artifact-render" => "ContentRef",
            "jit" => "RawTextRef",
            _ => throw new InvalidOperationException("The terminal result has no readable content.")
        };
        var contentRef = RequiredString(execution.Result, contentProperty);
        RequireProtocol(
            execution.Events.Any(operationEvent =>
            {
                var payload = operationEvent.GetProperty("Payload");
                return RequiredString(payload, "Kind") == "content-produced" &&
                    RequiredString(payload, "ContentRef") == contentRef;
            }),
            $"Operation returned {contentRef} without a content-produced event.");
        RequireProtocol(contentRef.StartsWith("sha256:", StringComparison.Ordinal), "Content reference is malformed.");
        var digest = contentRef["sha256:".Length..];
        using var response = await http.GetAsync($"/api/v1/operations/{execution.OperationId}/contents/sha256/{digest}", cancellationToken);
        await EnsureSuccessAsync(response, $"Content {contentRef}", cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private async Task TryCancelAsync(string operationId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await CancelOperationAsync(http, json, operationId, timeout.Token);
        }
        catch { }
    }

    public static async Task CancelOperationAsync(HttpClient http, JsonSerializerOptions json, string operationId, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync($"/api/v1/operations/{operationId}/cancel", new { operationId, reason = "performance-gate-timeout" }, json, cancellationToken);
        await EnsureSuccessAsync(response, $"Cancel operation {operationId}", cancellationToken);
    }

    private static PipelineMeasurement Summarize(List<OperationExecution> operations) => new(operations.Count, operations.Sum(static operation => operation.GatewayDispatchWaitMilliseconds));

    public static double CalculateGatewayDispatchWaitMilliseconds(IReadOnlyList<JsonElement> events)
    {
        if (events.Count < 2)
        {
            throw new InvalidDataException("Operation events must contain an accepted event followed by a dispatch event.");
        }

        var accepted = events[0];
        if (RequiredString(accepted.GetProperty("Payload"), "Kind") != "accepted")
            throw new InvalidDataException("The first operation event must be accepted.");
        var previousTimestamp = accepted.GetProperty("TimestampUtc").GetDateTimeOffset();
        for (var index = 1; index < events.Count; index++)
        {
            var operationEvent = events[index];
            if (RequiredString(operationEvent.GetProperty("Payload"), "Kind") == "accepted")
                throw new InvalidDataException("An operation event stream must contain exactly one accepted event.");
            var timestamp = operationEvent.GetProperty("TimestampUtc").GetDateTimeOffset();
            if (timestamp < previousTimestamp)
                throw new InvalidDataException("Operation event timestamps must be non-decreasing.");
            previousTimestamp = timestamp;
        }

        var dispatchedAt = events[1].GetProperty("TimestampUtc").GetDateTimeOffset();
        return (dispatchedAt - accepted.GetProperty("TimestampUtc").GetDateTimeOffset()).TotalMilliseconds;
    }

    private static string DecodeOutput(IReadOnlyList<JsonElement> events, string channel)
    {
        var output = new StringBuilder();
        foreach (var operationEvent in events)
        {
            var payload = operationEvent.GetProperty("Payload");
            if (RequiredString(payload, "Kind") != "output-chunk")
                continue;
            var chunk = payload.GetProperty("Chunk");
            if (RequiredString(chunk, "Channel") != channel)
                continue;
            output.Append(Encoding.UTF8.GetString(Convert.FromBase64String(RequiredString(chunk, "Data"))));
        }
        return output.ToString();
    }

    private static (string requestId, string idempotencyKey) Identity(string kind)
    {
        var suffix = Guid.NewGuid().ToString("N");
        return ($"perf-{kind}-{suffix}", $"perf-{kind}-key-{suffix}");
    }

    private static string ResultType(JsonElement result) => RequiredString(result, "ResultType");

    private static string RequiredString(JsonElement element, string property)
    {
        if (!TryGetProperty(element, property, out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"JSON property '{property}' is missing or is not a string.");
        return value.GetString()!;
    }

    private static bool TryGetProperty(JsonElement element, string property, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            return element.TryGetProperty(property, out value);
        value = default;
        return false;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException($"{operation} failed with {(int)response.StatusCode}: {body}");
    }

    private static void RequireSample(bool condition, string message)
    {
        if (!condition) throw new PerformanceSampleFailureException(message);
    }

    private static void RequireProtocol(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

static class Statistics
{
    public static StatisticalSummary Summarize(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        return ordered.Length == 0
            ? new StatisticalSummary(0, null, null, null, null) : new StatisticalSummary(ordered.Length, ordered[0], Percentile(ordered, 0.50), Percentile(ordered, 0.95), ordered[^1]);
    }

    public static double Percentile(IReadOnlyCollection<double> values, double percentile)
    {
        if (values.Count == 0) throw new ArgumentException("At least one value is required.", nameof(values));
        if (percentile is <= 0 or > 1) throw new ArgumentOutOfRangeException(nameof(percentile));
        var ordered = values.Order().ToArray();
        var rank = Math.Max(1, (int)Math.Ceiling(percentile * ordered.Length));
        return ordered[rank - 1];
    }
}

static class ThresholdConfigurationValidator
{
    public static void Validate(ThresholdConfiguration configuration, IReadOnlyList<ScenarioDefinition> scenarios)
    {
        Require(configuration.SchemaVersion == 1, "Threshold schemaVersion must be 1.");
        Require(!string.IsNullOrWhiteSpace(configuration.ProfileId), "Threshold profileId is required.");
        Require(configuration.Workload.WarmupSamplesPerScenario >= 1, "At least one warmup sample per scenario is required.");
        Require(configuration.Workload.BaselineSamplesPerScenario >= 10, "At least ten baseline samples per scenario are required.");
        Require(configuration.Workload.BaselineMaxConcurrency == 1, "Baseline concurrency must be one.");
        Require(configuration.Workload.OperationTimeoutSeconds is >= 30 and <= 600, "Operation timeout must be between 30 and 600 seconds.");
        Require(configuration.Workload.OverallTimeoutMinutes is >= 10 and <= 120, "Overall timeout must be between 10 and 120 minutes.");
        Require(configuration.Workload.PollIntervalMilliseconds is >= 25 and <= 1000, "Poll interval must be between 25 and 1000 milliseconds.");
        Require(configuration.Workload.MemorySampleIntervalMilliseconds is >= 25 and <= 5000, "Memory sample interval must be between 25 and 5000 milliseconds.");
        Require(configuration.Workload.ConcurrencyLevels.SequenceEqual([10, 50]), "The v1 release workload must execute concurrency levels 10 and 50.");

        var scenarioIds = scenarios.Select(static scenario => scenario.Id).ToHashSet(StringComparer.Ordinal);
        Require(configuration.Scenarios.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(scenarioIds), "Threshold scenarios must exactly match the release workload scenarios.");
        foreach (var scenario in scenarios)
        {
            var configured = configuration.Scenarios[scenario.Id];
            ValidateBudget(configured.Baseline, $"{scenario.Id}/baseline");
            var expectedLevels = configuration.Workload.ConcurrencyLevels.Select(static level => level.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToHashSet(StringComparer.Ordinal);
            Require(configured.Concurrency.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expectedLevels), $"Scenario '{scenario.Id}' must define budgets for concurrency 10 and 50.");
            foreach (var (level, budget) in configured.Concurrency)
                ValidateBudget(budget, $"{scenario.Id}/concurrency-{level}");
        }

        var expectedConcurrency = configuration.Workload.ConcurrencyLevels.Select(static level => level.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToHashSet(StringComparer.Ordinal);
        Require(configuration.Concurrency.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expectedConcurrency), "Concurrency batch budgets must exactly match levels 10 and 50.");
        foreach (var (level, budget) in configuration.Concurrency)
        {
            Require(budget.MaxBatchDurationMilliseconds > 0, $"Concurrency {level} batch duration budget must be positive.");
            Require(budget.MaxPeakUsedDeltaBytes > 0, $"Concurrency {level} memory delta budget must be positive.");
            Require(budget.MinAvailableBytes > 0, $"Concurrency {level} minimum available memory budget must be positive.");
        }
    }

    private static void ValidateBudget(LatencyBudget budget, string name)
    {
        Require(budget.MaxP50Milliseconds > 0, $"{name} p50 budget must be positive.");
        Require(budget.MaxP95Milliseconds >= budget.MaxP50Milliseconds, $"{name} p95 budget must be at least p50.");
        Require(budget.MaxGatewayDispatchP95Milliseconds > 0, $"{name} Gateway dispatch p95 budget must be positive.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
}

static class PerformanceBudgetEvaluator
{
    public static IReadOnlyList<BudgetViolation> Evaluate(GateReport report, ThresholdConfiguration configuration)
    {
        var violations = new List<BudgetViolation>();
        foreach (var scenarioId in configuration.Scenarios.Keys)
        {
            var warmupCount = report.Warmup.Count(sample => sample.ScenarioId == scenarioId);
            CheckCount("warmup", scenarioId, warmupCount, configuration.Workload.WarmupSamplesPerScenario, violations);
        }
        foreach (var measurement in report.Warmup.Where(static sample => !sample.Succeeded))
            violations.Add(new BudgetViolation("warmup", measurement.ScenarioId, "failureCount", 1, 0, $"Warmup sample '{measurement.SampleId}' failed: {measurement.Error}"));

        foreach (var measurement in report.Baseline)
        {
            CheckCount("baseline", measurement.ScenarioId, measurement.SampleCount, configuration.Workload.BaselineSamplesPerScenario, violations);
            EvaluateScenario("baseline", measurement, configuration.Scenarios[measurement.ScenarioId].Baseline, violations);
        }

        foreach (var concurrency in report.Concurrency)
        {
            var level = concurrency.ConcurrencyLevel.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var batchBudget = configuration.Concurrency[level];
            var expectedSamplesPerScenario = concurrency.ConcurrencyLevel / configuration.Scenarios.Count;
            CheckMaximum($"concurrency-{level}", scenarioId: null, "batchDurationMilliseconds", concurrency.DurationMilliseconds, batchBudget.MaxBatchDurationMilliseconds, violations);
            if (!concurrency.SystemMemory.Supported)
            {
                violations.Add(new BudgetViolation($"concurrency-{level}", ScenarioId: null, "systemMemory.supported", Observed: 0, Limit: 1, "System memory sampling is required for a release performance gate."));
            }
            else
            {
                CheckMaximum($"concurrency-{level}", scenarioId: null, "systemMemory.peakUsedDeltaBytes", concurrency.SystemMemory.PeakUsedDeltaBytes, batchBudget.MaxPeakUsedDeltaBytes, violations);
                CheckMinimum($"concurrency-{level}", scenarioId: null, "systemMemory.minimumAvailableBytes", concurrency.SystemMemory.MinimumAvailableBytes, batchBudget.MinAvailableBytes, violations);
            }

            foreach (var measurement in concurrency.Scenarios)
            {
                CheckCount($"concurrency-{level}", measurement.ScenarioId, measurement.SampleCount, expectedSamplesPerScenario, violations);
                EvaluateScenario($"concurrency-{level}", measurement, configuration.Scenarios[measurement.ScenarioId].Concurrency[level], violations);
            }
        }
        return violations;
    }

    private static void CheckCount(string scope, string scenarioId, int observed, int expected, List<BudgetViolation> violations)
    {
        if (observed == expected)
            return;
        violations.Add(new BudgetViolation(scope, scenarioId, "sampleCount", observed, expected, $"Observed {observed} samples; the release workload requires {expected}."));
    }

    private static void EvaluateScenario(string scope, ScenarioMeasurement measurement, LatencyBudget budget, List<BudgetViolation> violations)
    {
        if (measurement.FailureCount > 0)
        {
            violations.Add(new BudgetViolation(scope, measurement.ScenarioId, "failureCount", measurement.FailureCount, 0, $"{measurement.FailureCount} of {measurement.SampleCount} requests failed."));
        }
        if (measurement.SuccessCount == 0)
        {
            violations.Add(new BudgetViolation(scope, measurement.ScenarioId, "successCount", 0, 1, "No successful request was available for percentile evaluation."));
            return;
        }
        CheckMaximum(scope, measurement.ScenarioId, "latency.p50Milliseconds", measurement.LatencyMilliseconds.P50, budget.MaxP50Milliseconds, violations);
        CheckMaximum(scope, measurement.ScenarioId, "latency.p95Milliseconds", measurement.LatencyMilliseconds.P95, budget.MaxP95Milliseconds, violations);
        CheckMaximum(scope, measurement.ScenarioId, "gatewayDispatchWait.p95Milliseconds", measurement.GatewayDispatchWaitMilliseconds.P95, budget.MaxGatewayDispatchP95Milliseconds, violations);
    }

    private static void CheckMaximum(string scope, string? scenarioId, string metric, double? observed, double limit, List<BudgetViolation> violations)
    {
        if (observed is not null && observed <= limit)
            return;
        violations.Add(new BudgetViolation(scope, scenarioId, metric, observed, limit, observed is null ? $"Metric '{metric}' is missing." : $"{metric} {observed.Value:N3} exceeds budget {limit:N3}."));
    }

    private static void CheckMinimum(string scope, string? scenarioId, string metric, long? observed, long limit, List<BudgetViolation> violations)
    {
        if (observed is not null && observed >= limit)
            return;
        violations.Add(new BudgetViolation(scope, scenarioId, metric, observed, limit, observed is null ? $"Metric '{metric}' is missing." : $"{metric} {observed.Value} is below budget floor {limit}."));
    }
}

static class ReportSchemaValidator
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<JsonSchema>>> Schemas =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly EvaluationOptions EvaluationOptions = new()
    {
        OutputFormat = OutputFormat.List,
        RequireFormatValidation = true
    };
    private static readonly JsonSerializerOptions DiagnosticJson = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public static Task<JsonSchema> LoadAsync(string schemaPath)
    {
        var fullPath = Path.GetFullPath(schemaPath);
        return Schemas.GetOrAdd(fullPath, static path => new Lazy<Task<JsonSchema>>(() => LoadCoreAsync(path), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private static async Task<JsonSchema> LoadCoreAsync(string schemaPath)
    {
        try
        {
            return JsonSchema.FromText(await File.ReadAllTextAsync(schemaPath));
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new InvalidDataException($"Performance report schema '{schemaPath}' is invalid: {exception.Message}", exception);
        }
    }

    public static void Validate(JsonSchema schema, JsonElement report)
    {
        var results = schema.Evaluate(report, EvaluationOptions);
        if (results.IsValid)
            return;
        throw new InvalidDataException($"Performance report does not conform to report.schema.v1.json: " + JsonSerializer.Serialize(results, DiagnosticJson));
    }
}

static class SystemMemoryReader
{
    public static SystemMemorySnapshot? TryRead()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
                return GlobalMemoryStatusEx(ref status)
                    ? new SystemMemorySnapshot(checked((long)status.TotalPhysical), checked((long)status.AvailablePhysical)) : null;
            }
            if (OperatingSystem.IsLinux())
                return ReadLinux();
        }
        catch { }
        return null;
    }

    private static SystemMemorySnapshot? ReadLinux()
    {
        long? total = null;
        long? available = null;
        foreach (var line in File.ReadLines("/proc/meminfo"))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
                continue;
            var name = line[..separator];
            var valueText = line[(separator + 1)..].Trim();
            var firstSpace = valueText.IndexOf(' ');
            if (firstSpace >= 0)
                valueText = valueText[..firstSpace];
            if (!long.TryParse(valueText, out var kibibytes))
                continue;
            if (name == "MemTotal")
                total = checked(kibibytes * 1024);
            else if (name == "MemAvailable")
                available = checked(kibibytes * 1024);
        }
        return total is not null && available is not null
            ? new SystemMemorySnapshot(total.Value, available.Value) : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}

static class PerformanceFailureClassifier
{
    public static bool IsSampleFailure(Exception exception) => exception is PerformanceSampleFailureException;
}

class PerformanceSampleFailureException(string message) : Exception(message);

sealed class OperationTerminalFailureException(string message) :
    PerformanceSampleFailureException(message);

sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request, cancellationToken);
}

sealed record GateOptions(bool SelfTest, bool ShowHelp, Uri BaseAddress, string ThresholdsPath, string OutputPath)
{
    public const string Usage = """
        Usage:
          dotnet run eng/performance/gateway-performance.cs -- [options]

        Options:
          --base-address <uri>   Gateway base URI (default: SHARPLABNEXT_E2E_BASE_URL or http://127.0.0.1:8080)
          --thresholds <path>    Versioned threshold JSON (default: eng/performance/thresholds.v1.json)
          --output <path>        Machine-readable report path (default: artifacts/performance/gateway-performance.json)
          --self-test            Test percentile, threshold, and report-schema logic without a Gateway
          --help                 Show this help
        """;

    public static GateOptions Parse(string[] args)
    {
        var selfTest = false;
        var showHelp = false;
        var baseAddressText = Environment.GetEnvironmentVariable("SHARPLABNEXT_E2E_BASE_URL")
            ?? "http://127.0.0.1:8080";
        var thresholds = Path.Combine("eng", "performance", "thresholds.v1.json");
        var output = Path.Combine("artifacts", "performance", "gateway-performance.json");
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--self-test":
                    selfTest = true;
                    break;
                case "--help" or "-h":
                    showHelp = true;
                    break;
                case "--base-address":
                    baseAddressText = RequiredValue(args, ref index, "--base-address");
                    break;
                case "--thresholds":
                    thresholds = RequiredValue(args, ref index, "--thresholds");
                    break;
                case "--output":
                    output = RequiredValue(args, ref index, "--output");
                    break;
                default:
                    throw new ArgumentException($"Unknown performance gate argument '{args[index]}'.");
            }
        }
        if (!Uri.TryCreate(baseAddressText, UriKind.Absolute, out var baseAddress) || baseAddress.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("--base-address must be an absolute HTTP(S) URI.");
        }
        return new GateOptions(selfTest, showHelp, baseAddress, thresholds, output);
    }

    private static string RequiredValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            throw new ArgumentException($"{option} requires a value.");
        return args[index];
    }
}

sealed record ThresholdConfiguration(
    int SchemaVersion,
    string ProfileId,
    WorkloadConfiguration Workload,
    IReadOnlyDictionary<string, ScenarioThresholdConfiguration> Scenarios,
    IReadOnlyDictionary<string, ConcurrencyBatchThreshold> Concurrency);

sealed record WorkloadConfiguration(
    int WarmupSamplesPerScenario,
    int BaselineSamplesPerScenario,
    int BaselineMaxConcurrency,
    IReadOnlyList<int> ConcurrencyLevels,
    int OperationTimeoutSeconds,
    int OverallTimeoutMinutes,
    int PollIntervalMilliseconds,
    int MemorySampleIntervalMilliseconds);

sealed record ScenarioThresholdConfiguration(LatencyBudget Baseline, IReadOnlyDictionary<string, LatencyBudget> Concurrency);

sealed record LatencyBudget(double MaxP50Milliseconds, double MaxP95Milliseconds, double MaxGatewayDispatchP95Milliseconds);

sealed record ConcurrencyBatchThreshold(double MaxBatchDurationMilliseconds, long MaxPeakUsedDeltaBytes, long MinAvailableBytes);

sealed record GateReport(
    int SchemaVersion,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    ThresholdProfileReference ThresholdProfile,
    GatewayTarget Target,
    RuntimeEnvironmentInfo Environment,
    WorkloadConfiguration Workload,
    IReadOnlyList<SampleMeasurement> Warmup,
    IReadOnlyList<ScenarioMeasurement> Baseline,
    IReadOnlyList<ConcurrencyMeasurement> Concurrency,
    IReadOnlyList<BudgetViolation> Violations,
    bool Passed);

sealed record ThresholdProfileReference(int SchemaVersion, string ProfileId, string ConfigurationFile, string Sha256);

sealed record GatewayTarget(Uri BaseAddress, string ReleaseId, string CatalogRevision);

sealed record RuntimeEnvironmentInfo(string OperatingSystem, string ProcessArchitecture, string OsArchitecture, int ProcessorCount, string FrameworkDescription, long? TotalSystemMemoryBytes)
{
    public static RuntimeEnvironmentInfo Create() => new(RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(), RuntimeInformation.OSArchitecture.ToString(), Environment.ProcessorCount, RuntimeInformation.FrameworkDescription, SystemMemoryReader.TryRead()?.TotalBytes);
}

sealed record SampleMeasurement(string SampleId, string ScenarioId, int ConcurrencyLevel, double DurationMilliseconds, double GatewayDispatchWaitMilliseconds, int OperationCount, bool Succeeded, string? Error);

sealed record ScenarioMeasurement(
    string ScenarioId,
    string DisplayName,
    int ConcurrencyLevel,
    int SampleCount,
    int SuccessCount,
    int FailureCount,
    StatisticalSummary LatencyMilliseconds,
    StatisticalSummary GatewayDispatchWaitMilliseconds,
    IReadOnlyList<SampleMeasurement> Samples);

sealed record StatisticalSummary(int Count, double? Minimum, double? P50, double? P95, double? Maximum);

sealed record ConcurrencyMeasurement(int ConcurrencyLevel, DateTimeOffset StartedAtUtc, double DurationMilliseconds, SystemMemorySummary SystemMemory, IReadOnlyList<ScenarioMeasurement> Scenarios);

sealed record SystemMemorySummary(bool Supported, long? TotalBytes, long? AvailableBeforeBytes, long? MinimumAvailableBytes, long? AvailableAfterBytes, long? PeakUsedDeltaBytes)
{
    public static SystemMemorySummary Create(IEnumerable<SystemMemorySnapshot> values)
    {
        var samples = values.ToArray();
        if (samples.Length == 0)
            return new SystemMemorySummary(false, null, null, null, null, null);
        var first = samples[0];
        var initialUsed = first.TotalBytes - first.AvailableBytes;
        var peakUsed = samples.Max(static sample => sample.TotalBytes - sample.AvailableBytes);
        return new SystemMemorySummary(true, first.TotalBytes, first.AvailableBytes, samples.Min(static sample => sample.AvailableBytes), samples[^1].AvailableBytes, Math.Max(0, peakUsed - initialUsed));
    }
}

sealed record SystemMemorySnapshot(long TotalBytes, long AvailableBytes);

sealed record BudgetViolation(string Scope, string? ScenarioId, string Metric, double? Observed, double Limit, string Message);

sealed record ScenarioDefinition(string Id, string DisplayName, string OutputId, string? RuntimeId, ScenarioTerminal Terminal);

enum ScenarioTerminal
{
    Build,
    ArtifactRender,
    Run,
    Jit
}

sealed record SampleWorkItem(ScenarioDefinition Scenario, string SampleId, int ConcurrencyLevel);

sealed record PipelineMeasurement(int OperationCount, double GatewayDispatchWaitMilliseconds);

sealed record OperationExecution(string OperationId, JsonElement Result, IReadOnlyList<JsonElement> Events, double GatewayDispatchWaitMilliseconds);

sealed class PascalCaseJsonNamingPolicy : JsonNamingPolicy
{
    public static readonly PascalCaseJsonNamingPolicy Instance = new();

    public override string ConvertName(string name) =>
        name.Length == 0 || !char.IsAsciiLetterLower(name[0])
            ? name : char.ToUpperInvariant(name[0]) + name[1..];
}
