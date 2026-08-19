using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.Contracts;

namespace SharpLabNext.ContractTests;

public sealed class SerializationContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = ContractJson.CreateSerializerOptions();

    [Fact]
    public void AutomaticBuildOutputKindUsesStableWireValue()
    {
        var json = JsonSerializer.Serialize(BuildOutputKind.Auto, JsonOptions);

        Assert.Equal("\"auto\"", json);
        Assert.Equal(BuildOutputKind.Auto, JsonSerializer.Deserialize<BuildOutputKind>(json, JsonOptions));
    }

    [Fact]
    public void BuildRequestUsesStablePascalCaseWireShape()
    {
        var request = CreateBuildRequest();
        var document = JsonNode.Parse(JsonSerializer.Serialize(request, JsonOptions))!.AsObject();

        Assert.Equal("req-01", document["RequestId"]!.GetValue<string>());
        Assert.Equal("release", document["Options"]!["Configuration"]!.GetValue<string>());
        Assert.Equal("console", document["Workspace"]!["BuildOptions"]!["OutputKind"]!.GetValue<string>());
        Assert.Equal("Console.WriteLine(42);", document["Workspace"]!["Files"]![0]!["Text"]!.GetValue<string>());

        var roundTrip = JsonSerializer.Deserialize<BuildRequest>(document.ToJsonString(), JsonOptions);

        Assert.NotNull(roundTrip);
        Assert.Equal(42, roundTrip.Workspace.Revision);
        Assert.Equal(BuildConfiguration.Release, roundTrip.EffectiveOptions.Configuration);

        document["FutureMinorField"] = new JsonObject { ["Ignored"] = true };
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<BuildRequest>(document.ToJsonString(), JsonOptions));
    }

    [Fact]
    public void BusinessContractsRejectLegacyCamelCasePayloads()
    {
        const string legacy = """
            {
              "requestId": "req-legacy",
              "idempotencyKey": "idem-legacy",
              "pipelineResolutionId": "pipeline-legacy",
              "toolchainId": "roslyn-stable",
              "referenceSetId": "net10-ref",
              "workspace": {
                "schemaVersion": 1,
                "revision": 1,
                "selectionRevision": 1,
                "languageId": "csharp",
                "files": [{ "path": "Program.cs", "version": 1, "text": "class C {}" }],
                "activeFile": "Program.cs",
                "sourceOrder": ["Program.cs"],
                "referenceSetId": "net10-ref",
                "buildOptions": {
                  "configuration": "release",
                  "optimize": true,
                  "outputKind": "library",
                  "allowUnsafe": false,
                  "emitPortablePdb": true
                }
              },
              "deadlineUtc": "2026-07-11T00:00:00Z",
              "target": "artifact"
            }
            """;

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<BuildRequest>(legacy, JsonOptions));
    }

    [Fact]
    public void LspOptionsRetainStandardLowerCamelCaseWireNames()
    {
        var options = ContractJson.CreateLspSerializerOptions();
        var payload = new { Jsonrpc = "2.0", Method = "textDocument/hover" };

        var document = JsonNode.Parse(JsonSerializer.Serialize(payload, options))!.AsObject();
        Assert.Equal("2.0", document["jsonrpc"]!.GetValue<string>());
        Assert.Equal("textDocument/hover", document["method"]!.GetValue<string>());
        Assert.False(document.ContainsKey("JsonRpc"));
    }

    [Fact]
    public void FrontendAnonymousObjectsUsePascalCase()
    {
        var document = JsonNode.Parse(JsonSerializer.Serialize(
            new { error = "invalid-command", traceId = "trace-1" },
            JsonOptions))!.AsObject();

        Assert.Equal("invalid-command", document["Error"]!.GetValue<string>());
        Assert.Equal("trace-1", document["TraceId"]!.GetValue<string>());
        Assert.False(document.ContainsKey("error"));
        Assert.False(document.ContainsKey("traceId"));
    }

    [Fact]
    public void HostOwnedHttpOptionsUsePascalCaseForDictionaryMembersToo()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ContractJson.ApplySerializerOptions(options);

        Assert.Null(options.DictionaryKeyPolicy);
        var json = JsonSerializer.Serialize(
            new DictionaryEnvelope(new Dictionary<string, string> { ["customName"] = "value" }),
            options);
        var document = JsonNode.Parse(json)!.AsObject();
        Assert.True(document.ContainsKey("Metadata"));
        Assert.True(document["Metadata"]!.AsObject().ContainsKey("customName"));

        var unrelated = JsonNode.Parse(JsonSerializer.Serialize(
            new { metadata = new Dictionary<string, string> { ["customName"] = "value" } },
            options))!.AsObject();
        Assert.True(unrelated.ContainsKey("Metadata"));
        Assert.True(unrelated["Metadata"]!.AsObject().ContainsKey("customName"));

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DictionaryEnvelope>(
            "{\"metadata\":{\"customName\":\"value\"}}",
            options));
    }

    [Fact]
    public void HostOwnedHttpOptionsRetainPolymorphicWireDiscriminators()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ContractJson.ApplySerializerOptions(options);
        var eventValue = new OperationEvent(
            "op-1",
            1,
            DateTimeOffset.UnixEpoch,
            "trace-1",
            new CompletedOperationEventPayload(OperationCompletionStatus.Completed, TimeSpan.Zero));

        var document = JsonNode.Parse(JsonSerializer.Serialize(eventValue, options))!.AsObject();
        Assert.Equal("completed", document["Payload"]!["Kind"]!.GetValue<string>());
    }

    [Fact]
    public void CanonicalOptionsKeepStorageMemberNamesUnchanged()
    {
        var options = ContractJson.CreateCanonicalSerializerOptions();
        var value = new OperationEvent(
            "op-1",
            1,
            DateTimeOffset.UnixEpoch,
            "trace-1",
            new CompletedOperationEventPayload(OperationCompletionStatus.Completed, TimeSpan.Zero));

        var document = JsonNode.Parse(JsonSerializer.Serialize(value, options))!.AsObject();
        Assert.Equal("completed", document["payload"]!["kind"]!.GetValue<string>());
    }

    [Fact]
    public void BusinessContractPreservesDynamicDictionaryKeys()
    {
        var node = new AstNode(
            "Property",
            new TextRange(1, 0, 1, 8),
            null,
            new Dictionary<string, string?> { ["IsStatic"] = "true", ["customName"] = "value" },
            []);

        var document = JsonNode.Parse(JsonSerializer.Serialize(node, JsonOptions))!.AsObject();
        var properties = document["Properties"]!.AsObject();
        Assert.True(properties.ContainsKey("IsStatic"));
        Assert.True(properties.ContainsKey("customName"));
    }

    [Fact]
    public void ArtifactReferencesSerializeAsOpaqueStrings()
    {
        var request = new RunRequest(
            "req-02",
            "run-key",
            "pipeline-01",
            new ArtifactRef("sha256:abc"),
            "dotnet-11-preview-linux-x64",
            new RunOptions([], null, RunInstrumentation.None, "runtime-job-default"),
            DateTimeOffset.Parse("2026-07-11T00:00:05Z", CultureInfo.InvariantCulture));

        var document = JsonNode.Parse(JsonSerializer.Serialize(request, JsonOptions))!.AsObject();
        Assert.Equal("sha256:abc", document["ArtifactRef"]!.GetValue<string>());

        var roundTrip = JsonSerializer.Deserialize<RunRequest>(document.ToJsonString(), JsonOptions);
        Assert.Equal(new ArtifactRef("sha256:abc"), roundTrip!.ArtifactRef);
    }

    [Fact]
    public void OperationPayloadAndTypedResultRoundTripWithDiscriminators()
    {
        var result = new RunResult(
            RunTerminalStatus.UserException,
            1,
            new UserExceptionInfo("System.InvalidOperationException", "failed", null, null),
            TimeSpan.FromMilliseconds(25),
            false,
            new RuntimeIdentity("11.0.0-preview.5", "runtime-commit", "sha256:image", "linux-x64", "x64"));
        var operationEvent = new OperationEvent(
            "op-1",
            3,
            DateTimeOffset.Parse("2026-07-11T00:00:01Z", CultureInfo.InvariantCulture),
            "trace-1",
            new TypedResultOperationEventPayload(result));

        var json = JsonSerializer.Serialize(operationEvent, JsonOptions);
        var document = JsonNode.Parse(json)!.AsObject();

        Assert.Equal("typed-result", document["Payload"]!["Kind"]!.GetValue<string>());
        Assert.Equal("run", document["Payload"]!["Result"]!["ResultType"]!.GetValue<string>());
        Assert.Equal("user-exception", document["Payload"]!["Result"]!["Status"]!.GetValue<string>());

        var roundTrip = JsonSerializer.Deserialize<OperationEvent>(json, JsonOptions);
        var typedResult = Assert.IsType<TypedResultOperationEventPayload>(roundTrip!.Payload);
        var runResult = Assert.IsType<RunResult>(typedResult.Result);
        Assert.Equal(RunTerminalStatus.UserException, runResult.Status);
    }

    [Fact]
    public void UserExceptionDetailsPreserveStackTraceAndInnerExceptionAcrossWire()
    {
        const string outerStackTrace =
            "System.InvalidOperationException: outer\n   at Program.Main() in Program.cs:line 7";
        const string innerStackTrace =
            "System.ArgumentException: inner\n   at Program.Parse() in Program.cs:line 3";
        var result = new RunResult(
            RunTerminalStatus.UserException,
            1,
            new UserExceptionInfo(
                "System.InvalidOperationException",
                "outer",
                outerStackTrace,
                new UserExceptionInfo(
                    "System.ArgumentException",
                    "inner",
                    innerStackTrace,
                    null)),
            TimeSpan.FromMilliseconds(25),
            false,
            new RuntimeIdentity("11.0.0-preview.5", "runtime-commit", "sha256:image", "linux-x64", "x64"));

        var json = JsonSerializer.Serialize<OperationResult>(result, JsonOptions);
        var document = JsonNode.Parse(json)!.AsObject();
        var exception = document["Exception"]!.AsObject();

        Assert.Equal(outerStackTrace, exception["StackTrace"]!.GetValue<string>());
        Assert.Equal("System.ArgumentException", exception["InnerException"]!["TypeName"]!.GetValue<string>());
        Assert.Equal(innerStackTrace, exception["InnerException"]!["StackTrace"]!.GetValue<string>());

        var roundTrip = Assert.IsType<RunResult>(
            JsonSerializer.Deserialize<OperationResult>(json, JsonOptions));
        Assert.Equal(outerStackTrace, roundTrip.Exception?.StackTrace);
        Assert.Equal("System.ArgumentException", roundTrip.Exception?.InnerException?.TypeName);
        Assert.Equal(innerStackTrace, roundTrip.Exception?.InnerException?.StackTrace);
    }

    [Fact]
    public void ExplainResultRoundTripsAsStructuredRanges()
    {
        OperationResult result = new ExplainResult(new ExplanationDocument(
            "csharp",
            "roslyn-stable",
            4,
            5,
            [new ExplanationFile(
                "Program.cs",
                [new ExplanationNode(
                    "return",
                    "Return statement",
                    "Returns control to the caller.",
                    new TextRange(2, 4, 2, 11),
                    1)])],
            false),
            new BuildIdentity(
                "release-1",
                "csharp",
                "roslyn-stable",
                "5.6.0",
                "compiler-commit",
                "net10-ref",
                "sha256:worker"));

        var json = JsonSerializer.Serialize(result, JsonOptions);
        var document = JsonNode.Parse(json)!.AsObject();

        Assert.Equal("explain", document["ResultType"]!.GetValue<string>());
        Assert.Equal(2, document["Document"]!["Files"]![0]!["Nodes"]![0]!["Range"]!["StartLine"]!.GetValue<int>());
        Assert.Equal("compiler-commit", document["Identity"]!["CompilerCommit"]!.GetValue<string>());
        var roundTrip = Assert.IsType<ExplainResult>(JsonSerializer.Deserialize<OperationResult>(json, JsonOptions));
        Assert.Equal("sha256:worker", roundTrip.Identity?.WorkerImageId);
    }

    [Fact]
    public void ArtifactProcessorIdentityRoundTripsWithRenderedContent()
    {
        OperationResult result = new RenderArtifactResult(
            ArtifactJobOutcome.Succeeded,
            new ContentRef($"sha256:{new string('a', 64)}"),
            "text/plain",
            [],
            [],
            new ArtifactProcessorIdentity(
                "release-1",
                "artifacts-default",
                "ilspy/10.1.0.8386",
                $"sha256:{new string('b', 64)}"));

        var json = JsonSerializer.Serialize(result, JsonOptions);
        var document = JsonNode.Parse(json)!.AsObject();

        Assert.Equal("artifacts-default", document["Identity"]!["ProcessorId"]!.GetValue<string>());
        Assert.Equal("ilspy/10.1.0.8386", document["Identity"]!["ProcessorVersion"]!.GetValue<string>());
        var roundTrip = Assert.IsType<RenderArtifactResult>(
            JsonSerializer.Deserialize<OperationResult>(json, JsonOptions));
        Assert.Equal("release-1", roundTrip.Identity?.ReleaseId);
    }

    [Fact]
    public void GistWorkspaceRoundTripsWithoutOAuthOrTransportSecrets()
    {
        var request = new CreateGistRequest(
            "sample",
            false,
            new GistWorkspaceState(
                ContractSchemaVersions.WorkspaceSnapshot,
                "csharp",
                "roslyn-stable",
                "net10-ref",
                "explain",
                null,
                BuildConfiguration.Release,
                "20260711.1",
                "Program.cs",
                ["Program.cs"],
                [new GistSourceFile("Program.cs", "class Program { }")]));

        var json = JsonSerializer.Serialize(request, JsonOptions);
        var document = JsonNode.Parse(json)!.AsObject();

        Assert.Equal("release", document["Workspace"]!["BuildMode"]!.GetValue<string>());
        Assert.Equal("class Program { }", document["Workspace"]!["Files"]![0]!["Text"]!.GetValue<string>());
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authorization", json, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(JsonSerializer.Deserialize<CreateGistRequest>(json, JsonOptions));
    }

    [Fact]
    public void ArtifactManifestRoundTripsWithoutEmbeddingArtifactBytes()
    {
        var manifest = new ArtifactManifest(
            ContractSchemaVersions.ArtifactManifest,
            new ArtifactRef("sha256:bundle"),
            new ArtifactProducer("20260711.1", "csharp", "roslyn-stable", "5.6.0", "commit", "sha256:worker"),
            "net10-ref",
            "net10.0",
            "dotnet-managed-pe-v1",
            new ArtifactRuntimeRequirement(
                "coreclr",
                [new FrameworkRequirement("Microsoft.NETCore.App", "10.0.9")],
                "anycpu",
                []),
            [],
            BuildOutputKind.Console,
            "app.dll",
            "Program.Main",
            [new ArtifactFileDescriptor("primary-assembly", "app.dll", 123, "sha256:file")]);

        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        Assert.DoesNotContain("base64", json, StringComparison.OrdinalIgnoreCase);

        var roundTrip = JsonSerializer.Deserialize<ArtifactManifest>(json, JsonOptions);
        Assert.Equal(new ArtifactRef("sha256:bundle"), roundTrip!.ArtifactId);
        Assert.Equal("app.dll", roundTrip.Files[0].Path);
    }

    [Fact]
    public void MainContractsAssemblyOnlyReferencesBclAssemblies()
    {
        var references = typeof(WorkspaceSnapshot).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(references, reference =>
            reference.Name is not null &&
            !reference.Name.Equals("System.Runtime", StringComparison.Ordinal) &&
            !reference.Name.StartsWith("System.", StringComparison.Ordinal));
    }

    private static BuildRequest CreateBuildRequest()
    {
        var options = new BuildOptions(
            BuildConfiguration.Release,
            true,
            BuildOutputKind.Console,
            false,
            true);
        var workspace = new WorkspaceSnapshot(
            ContractSchemaVersions.WorkspaceSnapshot,
            42,
            9,
            "csharp",
            [new WorkspaceFile("Program.cs", 12, "Console.WriteLine(42);")],
            "Program.cs",
            ["Program.cs"],
            "net11-preview-ref",
            options);

        return new BuildRequest(
            "req-01",
            "sha256:key",
            "pipeline-01",
            "roslyn-main",
            "net11-preview-ref",
            workspace,
            DateTimeOffset.Parse("2026-07-11T00:00:15Z", CultureInfo.InvariantCulture),
            options);
    }

    private sealed record DictionaryEnvelope(IReadOnlyDictionary<string, string> Metadata);
}
