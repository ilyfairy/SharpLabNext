using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpLabNext.Contracts;

namespace SharpLabNext.ContractTests;

public sealed class RequestShapeTests
{
    private static readonly JsonSerializerOptions JsonOptions = ContractJson.CreateSerializerOptions();

    [Fact]
    public void ResolveSelectionMatchesThePublicRequestEnvelope()
    {
        var request = new ResolveSelectionRequest(
            "csharp",
            "roslyn-main",
            "net11-preview-ref",
            "jit-asm",
            "dotnet-11-preview-linux-x64",
            BuildConfiguration.Release,
            "20260711.1",
            42);

        var document = JsonNode.Parse(JsonSerializer.Serialize(request, JsonOptions))!.AsObject();

        Assert.Equal("csharp", document["LanguageId"]!.GetValue<string>());
        Assert.Equal("roslyn-main", document["ToolchainId"]!.GetValue<string>());
        Assert.Equal("release", document["BuildMode"]!.GetValue<string>());
        Assert.Equal(42, document["WorkspaceRevision"]!.GetValue<long>());
    }

    [Fact]
    public void JitRequestMatchesTheRuntimeJobEnvelope()
    {
        var request = new JitRequest(
            "req-03",
            "jit-key",
            "pipeline-01",
            new ArtifactRef("sha256:artifact"),
            "dotnet-11-preview-linux-x64",
            new JitOptions(null, "tier0-diffable", "disabled", "coreclr-jitdisasm", "runtime-job-default"),
            DateTimeOffset.Parse("2026-07-11T00:00:10Z", CultureInfo.InvariantCulture));

        var document = JsonNode.Parse(JsonSerializer.Serialize(request, JsonOptions))!.AsObject();

        Assert.Equal("sha256:artifact", document["ArtifactRef"]!.GetValue<string>());
        Assert.Equal("tier0-diffable", document["Options"]!["TieringPolicyId"]!.GetValue<string>());
        Assert.Equal("coreclr-jitdisasm", document["Options"]!["ProviderId"]!.GetValue<string>());
    }

    [Fact]
    public void ExplainRequestCarriesOnlyResolvedWorkspaceInput()
    {
        var request = new ExplainRequest(
            "req-explain",
            "explain-key",
            "pipeline-explain",
            new WorkspaceSnapshot(
                ContractSchemaVersions.WorkspaceSnapshot,
                7,
                9,
                "csharp",
                [new WorkspaceFile("Program.cs", 1, "return;")],
                "Program.cs",
                ["Program.cs"],
                "net10-ref",
                new BuildOptions(BuildConfiguration.Release, true, BuildOutputKind.Console, false, true)),
            DateTimeOffset.Parse("2026-07-11T00:00:10Z", CultureInfo.InvariantCulture));

        var document = JsonNode.Parse(JsonSerializer.Serialize(request, JsonOptions))!.AsObject();

        Assert.Equal("pipeline-explain", document["PipelineResolutionId"]!.GetValue<string>());
        Assert.Equal("csharp", document["Workspace"]!["LanguageId"]!.GetValue<string>());
        Assert.Null(document["WorkerUrl"]);
        Assert.Null(document["CompilerOptions"]);
    }

    [Fact]
    public void WorkerDescriptorRoundTripsNegotiatedIdentityAndCapabilities()
    {
        var descriptor = new WorkerDescriptor(
            new ServiceIdentity(
                "roslyn-stable",
                ServiceKind.ToolchainWorker,
                "20260711.1",
                new ProtocolVersion(1, 2),
                ["build", "lsp"],
                "ready"),
            "instance-1",
            WorkerKind.Toolchain,
            "sha256:worker",
            new ProtocolVersion(1, 1),
            [new ProtocolVersion(1, 2)],
            [new WorkerCapabilityDescriptor("build", 1, true, ["roslyn-stable"])],
            ["roslyn-stable"],
            DateTimeOffset.UnixEpoch,
            ReferenceSets:
            [
                new ReferenceSetAttestation(
                    "net10-ref",
                    "net10.0",
                    "sha512-reference-package",
                    $"sha256:{new string('a', 64)}",
                    new ReferenceSetProvenance(
                        "nuget-package",
                        "10.0.9",
                        "Microsoft.NETCore.App.Ref",
                        "https://example.test/microsoft.netcore.app.ref.10.0.9.nupkg",
                        SourceArchiveDigest: $"sha512:{new string('b', 128)}"))
            ]);

        var json = JsonSerializer.Serialize(descriptor, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<WorkerDescriptor>(json, JsonOptions);

        Assert.NotNull(roundTrip);
        Assert.Equal(new ProtocolVersion(1, 1), roundTrip.NegotiatedProtocol);
        Assert.Equal("build", roundTrip.Capabilities[0].Id);
        Assert.Equal(WorkerKind.Toolchain, roundTrip.WorkerKind);
        var attestation = Assert.Single(roundTrip.ReferenceSets!);
        Assert.Equal("net10-ref", attestation.Id);
        Assert.Equal("10.0.9", attestation.Provenance.ResolvedVersion);
        Assert.Equal("Microsoft.NETCore.App.Ref", attestation.Provenance.Package);
    }

    [Fact]
    public void UnknownFutureTypedResultFallsBackToIgnorableBaseResult()
    {
        const string json = """
            {
              "OperationId": "op-1",
              "Sequence": 2,
              "TimestampUtc": "2026-07-11T00:00:00Z",
              "TraceId": "trace-1",
              "Payload": {
                "Kind": "typed-result",
                "Result": {
                  "ResultType": "future-result",
                  "futureValue": 42
                }
              }
            }
            """;

        var operationEvent = JsonSerializer.Deserialize<OperationEvent>(json, JsonOptions);
        var typedResult = Assert.IsType<TypedResultOperationEventPayload>(operationEvent!.Payload);
        Assert.IsType<OperationResult>(typedResult.Result);
    }

    [Fact]
    public void LegacyCamelCasePolymorphicDiscriminatorsAreRejected()
    {
        const string json = """
            {
              "operationId": "op-legacy",
              "sequence": 1,
              "timestampUtc": "2026-07-11T00:00:00Z",
              "traceId": "trace-legacy",
              "payload": {
                "kind": "typed-result",
                "result": {
                  "resultType": "run",
                  "status": "completed",
                  "exitCode": 0,
                  "exception": null,
                  "elapsed": "00:00:00",
                  "outputTruncated": false,
                  "identity": {
                    "runtimeVersion": "10.0",
                    "runtimeCommit": "commit",
                    "runtimeImageId": "image",
                    "rid": "linux-x64",
                    "architecture": "x64"
                  }
                }
              }
            }
            """;

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<OperationEvent>(json, JsonOptions));
    }

    [Fact]
    public void MissingPascalCasePolymorphicDiscriminatorsAreRejectedWithoutDisablingFutureFallback()
    {
        // Keep the surrounding envelope canonical so this exercises the
        // converter itself rather than strict validation of OperationEvent.
        const string eventWithLegacyDiscriminator = """
            {
              "OperationId": "op-legacy",
              "Sequence": 1,
              "TimestampUtc": "2026-07-11T00:00:00Z",
              "TraceId": "trace-legacy",
              "Payload": {
                "kind": "completed",
                "Status": "completed",
                "Elapsed": "00:00:00"
              }
            }
            """;

        const string resultWithLegacyDiscriminator = """
            {
              "resultType": "run",
              "Status": "completed",
              "ExitCode": 0,
              "Elapsed": "00:00:00",
              "OutputTruncated": false,
              "Identity": {
                "RuntimeVersion": "10.0",
                "RuntimeCommit": "commit",
                "RuntimeImageId": "image",
                "Rid": "linux-x64",
                "Architecture": "x64"
              }
            }
            """;

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<OperationEvent>(eventWithLegacyDiscriminator, JsonOptions));
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<OperationResult>(resultWithLegacyDiscriminator, JsonOptions));

        // An unknown but correctly named discriminator remains the documented
        // forward-compatible ignorable base value.
        const string futureResult = """
            {
              "ResultType": "future-result",
              "FutureValue": 42
            }
            """;
        Assert.IsType<OperationResult>(
            JsonSerializer.Deserialize<OperationResult>(futureResult, JsonOptions));
    }

    [Fact]
    public void LegacyDiscriminatorAliasesAreRejectedForUnknownValuesToo()
    {
        // Unknown discriminator values remain opaque for forward
        // compatibility, but reserved discriminator aliases must not be
        // allowed to bypass the canonical PascalCase spelling.
        const string futureResultWithLegacyAlias = """
            {
              "ResultType": "future-result",
              "resultType": "future-result",
              "FutureValue": 42
            }
            """;

        const string futureEventWithLegacyAlias = """
            {
              "Kind": "future-event",
              "kind": "future-event",
              "FutureValue": 42
            }
            """;

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<OperationResult>(futureResultWithLegacyAlias, JsonOptions));
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<OperationEventPayload>(futureEventWithLegacyAlias, JsonOptions));
    }

}
