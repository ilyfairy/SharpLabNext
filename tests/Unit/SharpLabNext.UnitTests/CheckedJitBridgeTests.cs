using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpLabNext.CheckedJitBridge;
using SharpLabNext.RuntimeProfile.Sdk;

namespace SharpLabNext.UnitTests;

public sealed class CheckedJitBridgeTests
{
    [Fact]
    public void StableContractValuesAreExact()
    {
        Assert.Equal("sharplabnext-checked-jit-bridge-v1", CheckedJitBridgeContract.ImplementationId);
        Assert.Equal("checked-jit-debug-info", CheckedJitBridgeContract.SourceMappingKind);
        Assert.Equal(
            "SHARPLABNEXT_CHECKED_JIT_SOURCE_MAPPING_KIND",
            CheckedJitBridgeContract.SourceMappingKindEnvironmentVariable);
        Assert.Equal(
            "/opt/sharplabnext/SharpLabNext.CheckedJitBridge.dll",
            CheckedJitBridgeContract.InstalledAssemblyPath);
    }

    [Fact]
    public void RuntimeVersionVerifierRequiresAnExactRuntimeIdentity()
    {
        using var error = new StringWriter();

        Assert.Equal(
            0,
            RuntimeVersionVerifier.Run(
                [RuntimeVersionVerifier.Switch, "7.0.20"],
                error,
                static () => "7.0.20"));
        Assert.Empty(error.ToString());

        Assert.Equal(
            1,
            RuntimeVersionVerifier.Run(
                [RuntimeVersionVerifier.Switch, "7.0.20"],
                error,
                static () => "7.0.22"));
        Assert.Contains("'7.0.22' does not match '7.0.20'", error.ToString(), StringComparison.Ordinal);

        error.GetStringBuilder().Clear();
        Assert.Equal(64, RuntimeVersionVerifier.Run([RuntimeVersionVerifier.Switch], error));
        Assert.Contains("exact-runtime-version", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PublicArgumentsAcceptOptionalMethodFilterOnly()
    {
        var assemblyPath = typeof(CheckedJitBridgeArguments).Assembly.Location;

        var parsedWithoutFilter = CheckedJitBridgeArguments.Parse(["jit", assemblyPath]);
        var parsedWithEmptyFilter = CheckedJitBridgeArguments.Parse(["jit", assemblyPath, ""]);

        Assert.Equal(assemblyPath, parsedWithoutFilter.AssemblyPath);
        Assert.Null(parsedWithoutFilter.MethodFilter);
        Assert.Equal(assemblyPath, parsedWithEmptyFilter.AssemblyPath);
        Assert.Null(parsedWithEmptyFilter.MethodFilter);
        Assert.Throws<ArgumentException>(() => CheckedJitBridgeArguments.Parse(["jit"]));
        Assert.Throws<ArgumentException>(() =>
            CheckedJitBridgeArguments.Parse(["jit", assemblyPath, "filter", "extra"]));
        Assert.Throws<ArgumentException>(() =>
            CheckedJitBridgeArguments.Parse(["jit", assemblyPath, "bad\nfilter"]));
    }

    [Fact]
    public void RuntimeProfileCommandWithoutFilterMatchesPublicArgumentShape()
    {
        var profile = new RuntimeProfileDefinition
        {
            Operations = new RuntimeProfileOperations
            {
                Jit = new RuntimeJitOperationDefinition
                {
                    ImplementationId = RuntimeOperationImplementationIds.CheckedJitBridge,
                    SourceMappingKind = RuntimeJitSourceMappingKinds.CheckedJitDebugInfo,
                    Command = new RuntimeOperationCommandDefinition
                    {
                        Executable = "/opt/sharplabnext/target-dotnet/dotnet",
                        Argv =
                        [
                            CheckedJitBridgeContract.InstalledAssemblyPath,
                            "jit",
                            RuntimeOperationPlaceholders.EntryAssembly,
                            RuntimeOperationPlaceholders.MethodFilter
                        ]
                    }
                }
            }
        };

        var command = RuntimeProfileCommandBuilder.CreateJitCommand(
            profile,
            "SharpLabNext.User.dll",
            methodFilter: null);

        Assert.Equal(
            [
                "/opt/sharplabnext/target-dotnet/dotnet",
                CheckedJitBridgeContract.InstalledAssemblyPath,
                "jit",
                "/workspace/SharpLabNext.User.dll"
            ],
            command);

        // Substitute only the container workspace path so the parser can perform
        // its host-side file existence check while preserving the generated shape.
        var publicArguments = command.Skip(2).ToArray();
        publicArguments[1] = typeof(CheckedJitBridgeArguments).Assembly.Location;
        var parsed = CheckedJitBridgeArguments.Parse(publicArguments);

        Assert.Null(parsed.MethodFilter);
    }

    [Fact]
    public void ChildLaunchUsesSameHostAndSeparatedArguments()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sl-next-checked-jit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var hostPath = Path.Combine(directory, RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "dotnet.exe"
                : "dotnet");
            var bridgePath = Path.Combine(directory, "SharpLabNext.CheckedJitBridge.dll");
            File.WriteAllBytes(hostPath, []);
            File.WriteAllBytes(bridgePath, []);
            var assemblyPath = typeof(CheckedJitBridgeArguments).Assembly.Location;
            var options = new CheckedJitBridgeArguments(assemblyPath, "Type.Method");

            var startInfo = CheckedJitChildProcess.CreateStartInfo(
                options,
                "pipe-handle",
                "0123456789abcdef0123456789abcdef",
                "SharpLabNext.CheckedJitBridge",
                hostPath,
                bridgePath);

            Assert.Equal(hostPath, startInfo.FileName);
            Assert.Equal(
                [
                    bridgePath,
                    "--child",
                    "pipe-handle",
                    assemblyPath,
                    "0123456789abcdef0123456789abcdef",
                    "Type.Method"
                ],
                startInfo.ArgumentList);
            Assert.False(startInfo.UseShellExecute);
            Assert.True(startInfo.RedirectStandardOutput);
            Assert.True(startInfo.RedirectStandardError);
            Assert.False(startInfo.RedirectStandardInput);
            Assert.Equal("*", startInfo.Environment["DOTNET_JitDisasm"]);
            Assert.Equal(
                "SharpLabNext.CheckedJitBridge",
                startInfo.Environment["DOTNET_JitDisasmAssemblies"]);
            Assert.Equal("1", startInfo.Environment["DOTNET_JitDisasmWithDebugInfo"]);
            Assert.False(startInfo.Environment.ContainsKey("DOTNET_JitStdOutFile"));
            Assert.False(startInfo.Environment.ContainsKey("COMPlus_JitStdOutFile"));
            Assert.False(startInfo.Environment.ContainsKey("SHARPLABNEXT_JIT_OUTPUT_PATH"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CaptureBudgetEnforcesPerStreamAndCombinedLimits()
    {
        var capture = new BoundedProcessOutput(standardOutputLimit: 4, standardErrorLimit: 4, totalLimit: 6);

        Assert.True(capture.TryAppend(ProcessOutputKind.StandardOutput, "abcd"u8));
        Assert.False(capture.TryAppend(ProcessOutputKind.StandardError, "xyz"u8));

        Assert.Equal("abcd", Encoding.UTF8.GetString(capture.StandardOutput));
        Assert.Equal("xy", Encoding.UTF8.GetString(capture.StandardError));
        Assert.True(capture.LimitExceeded);
        Assert.Equal(6, capture.TotalBytes);
    }

    [Fact]
    public async Task ProcessRunnerKillsAndWaitsAfterOutputLimit()
    {
        var startInfo = new ProcessStartInfo(FindDotnetHost())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--info");

        var result = await BoundedChildProcessRunner.RunAsync(
            startInfo,
            new BoundedChildProcessLimits(
                standardOutputBytes: 1,
                standardErrorBytes: 1,
                totalOutputBytes: 1,
                executionTimeout: TimeSpan.FromSeconds(10),
                cleanupTimeout: TimeSpan.FromSeconds(2)),
            CancellationToken.None);

        Assert.Equal(ChildTerminationReason.OutputLimitExceeded, result.TerminationReason);
        Assert.True(result.StandardOutput.Length + result.StandardError.Length <= 1);
        AssertProcessExited(result.ProcessId);
    }

    [Fact]
    public async Task ProcessRunnerKillsAndWaitsAfterTimeout()
    {
        var result = await BoundedChildProcessRunner.RunAsync(
            CreateHangingFixtureStartInfo(),
            CreateProcessLimits(TimeSpan.FromMilliseconds(150)),
            CancellationToken.None);

        Assert.Equal(ChildTerminationReason.TimedOut, result.TerminationReason);
        AssertProcessExited(result.ProcessId);
    }

    [Fact]
    public async Task ProcessRunnerKillsAndWaitsAfterCancellation()
    {
        using var cancellation = new CancellationTokenSource();

        var result = await BoundedChildProcessRunner.RunAsync(
            CreateHangingFixtureStartInfo(),
            CreateProcessLimits(TimeSpan.FromSeconds(10)),
            cancellation.Token,
            processStarted: cancellation.Cancel);

        Assert.Equal(ChildTerminationReason.Cancelled, result.TerminationReason);
        AssertProcessExited(result.ProcessId);
    }

    [Fact]
    public async Task MetadataOverflowSignalsProtocolFailure()
    {
        using var stream = new MemoryStream(new byte[17]);
        var failure = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BoundedStreamReader.ReadAsync(stream, maximumBytes: 16, failure));

        Assert.True(failure.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task HeldOpenMetadataPipeCannotBlockCompletion()
    {
        using var server = new AnonymousPipeServerStream(
            PipeDirection.In,
            HandleInheritability.Inheritable);
        var failure = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var metadataRead = BoundedStreamReader.ReadAsync(server, maximumBytes: 16, failure);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CheckedJitBridgeProgram.AwaitMetadataAsync(
                metadataRead,
                TimeSpan.FromMilliseconds(100)));

        server.DisposeLocalCopyOfClientHandle();
        Assert.Empty(await metadataRead);
        Assert.False(failure.Task.IsCompleted);
    }

    [Fact]
    public void ChildResultValidationRejectsForgedMethodMetadata()
    {
        const string nonce = "0123456789abcdef0123456789abcdef";
        var targetMethod = typeof(CheckedJitBridgeArguments).GetMethod(
            nameof(CheckedJitBridgeArguments.Parse),
            BindingFlags.Public | BindingFlags.Static)!;
        var validMethod = new ChildMethodRecord(
            $"0x{targetMethod.MetadataToken:x8}",
            targetMethod.MetadataToken,
            $"{typeof(CheckedJitBridgeArguments).FullName}.{targetMethod.Name}",
            Array.Empty<ChildGenericArgument>(),
            Array.Empty<ChildGenericArgument>(),
            "prepared",
            "0x1234",
            null);
        var envelope = new ChildResultEnvelope(
            ChildResultEnvelope.ProtocolMagic,
            nonce,
            typeof(CheckedJitBridgeArguments).Assembly.GetName().Name!,
            [validMethod],
            null);

        var validated = ChildResultCodec.ParseAndValidate(
            ChildResultCodec.Serialize(envelope),
            typeof(CheckedJitBridgeArguments).Assembly.Location,
            nonce);

        Assert.Single(validated.Methods);
        var forged = envelope with
        {
            Methods = [validMethod with { DisplayName = "System.String.Concat" }]
        };
        Assert.Throws<InvalidDataException>(() => ChildResultCodec.ParseAndValidate(
            ChildResultCodec.Serialize(forged),
            typeof(CheckedJitBridgeArguments).Assembly.Location,
            nonce));
    }

    [Fact]
    public void ChildResultValidationAuthenticatesNonceAndRejectsUnknownJson()
    {
        const string nonce = "0123456789abcdef0123456789abcdef";
        var targetMethod = typeof(CheckedJitBridgeArguments).GetMethod(
            nameof(CheckedJitBridgeArguments.Parse),
            BindingFlags.Public | BindingFlags.Static)!;
        var envelope = new ChildResultEnvelope(
            ChildResultEnvelope.ProtocolMagic,
            nonce,
            typeof(CheckedJitBridgeArguments).Assembly.GetName().Name!,
            [
                new ChildMethodRecord(
                    $"0x{targetMethod.MetadataToken:x8}",
                    targetMethod.MetadataToken,
                    $"{typeof(CheckedJitBridgeArguments).FullName}.{targetMethod.Name}",
                    Array.Empty<ChildGenericArgument>(),
                    Array.Empty<ChildGenericArgument>(),
                    "prepared",
                    "0x1234",
                    null)
            ],
            null);
        var assemblyPath = typeof(CheckedJitBridgeArguments).Assembly.Location;

        Assert.Throws<InvalidDataException>(() => ChildResultCodec.ParseAndValidate(
            ChildResultCodec.Serialize(envelope),
            assemblyPath,
            "fedcba9876543210fedcba9876543210"));

        var json = JsonNode.Parse(ChildResultCodec.Serialize(envelope))!.AsObject();
        json["Unknown"] = true;
        Assert.Throws<InvalidDataException>(() => ChildResultCodec.ParseAndValidate(
            Encoding.UTF8.GetBytes(json.ToJsonString()),
            assemblyPath,
            nonce));
    }

    [Fact]
    public void MetadataSignatureDecoderSubstitutesGenericArguments()
    {
        var integer = JitMethodSignatures.CreateGenericArgument(typeof(int));
        var reference = JitMethodSignatures.CreateGenericArgument(typeof(string));
        var genericType = typeof(SignatureGenericType<>);
        var typeMethod = genericType.GetMethod(
            nameof(SignatureGenericType<int>.Echo),
            BindingFlags.Public | BindingFlags.Instance)!;
        var genericMethod = typeof(CheckedJitBridgeTests).GetMethod(
            nameof(SignatureGenericMethod),
            BindingFlags.NonPublic | BindingFlags.Static)!;
        using var metadata = ManagedAssemblyMetadata.Open(typeof(CheckedJitBridgeTests).Assembly.Location);

        var typeIdentity = metadata.ValidateMethod(new ChildMethodRecord(
            JitMethodSignatures.CreateMethodIdentity(typeMethod.MetadataToken, [integer], []),
            typeMethod.MetadataToken,
            $"{genericType.FullName}[System.Int32].{typeMethod.Name}",
            [integer],
            [],
            "prepared",
            "0x1234",
            null));
        var methodIdentity = metadata.ValidateMethod(new ChildMethodRecord(
            JitMethodSignatures.CreateMethodIdentity(genericMethod.MetadataToken, [], [reference]),
            genericMethod.MetadataToken,
            $"{typeof(CheckedJitBridgeTests).FullName}.{genericMethod.Name}",
            [],
            [reference],
            "prepared",
            "0x1234",
            null));

        Assert.Equal(
            $"{genericType.FullName}[int]:Echo(int):int:this",
            typeIdentity.HeaderKey);
        Assert.Equal(
            $"{typeof(CheckedJitBridgeTests).FullName}:SignatureGenericMethod[System.__Canon]" +
            "(System.__Canon):System.__Canon",
            methodIdentity.HeaderKey);
    }

    [Fact]
    public void MetadataSignatureDecoderDistinguishesOverloads()
    {
        var integer = typeof(CheckedJitBridgeTests).GetMethod(
            nameof(SignatureOverload),
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            [typeof(int)],
            modifiers: null)!;
        var @long = typeof(CheckedJitBridgeTests).GetMethod(
            nameof(SignatureOverload),
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            [typeof(long)],
            modifiers: null)!;
        using var metadata = ManagedAssemblyMetadata.Open(typeof(CheckedJitBridgeTests).Assembly.Location);

        var integerIdentity = metadata.ValidateMethod(CreateChildMethodRecord(integer));
        var longIdentity = metadata.ValidateMethod(CreateChildMethodRecord(@long));

        var typeName = typeof(CheckedJitBridgeTests).FullName;
        Assert.Equal($"{typeName}:SignatureOverload(int):int", integerIdentity.HeaderKey);
        Assert.Equal($"{typeName}:SignatureOverload(long):long", longIdentity.HeaderKey);
    }

    [Fact]
    public void PortablePdbMustMatchPeContentIdentity()
    {
        var assemblyPath = typeof(CheckedJitBridgeArguments).Assembly.Location;
        var method = typeof(CheckedJitBridgeArguments).GetMethod(
            nameof(CheckedJitBridgeArguments.Parse),
            BindingFlags.Public | BindingFlags.Static)!;
        var valid = CheckedJitSourceMapping.LoadSiblingPortablePdb(assemblyPath);
        Assert.Contains(method.MetadataToken, valid.Keys);

        var directory = Path.Combine(Path.GetTempPath(), $"sl-next-pdb-mismatch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var copiedAssembly = Path.Combine(directory, "Mismatch.dll");
            File.Copy(assemblyPath, copiedAssembly);
            var unrelatedPdb = Path.ChangeExtension(typeof(CheckedJitBridgeTests).Assembly.Location, ".pdb");
            Assert.True(File.Exists(unrelatedPdb));
            File.Copy(unrelatedPdb, Path.ChangeExtension(copiedAssembly, ".pdb"));

            Assert.Empty(CheckedJitSourceMapping.LoadSiblingPortablePdb(copiedAssembly));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("none")]
    public void DisabledDeclaredSourceMappingDoesNotLoadPortablePdb(string declaredKind)
    {
        var assemblyPath = typeof(CheckedJitBridgeArguments).Assembly.Location;

        var maps = CheckedJitSourceMapping.LoadForDeclaredKind(assemblyPath, declaredKind);

        Assert.Empty(maps);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("checked-jit-debug-info")]
    public void EnabledDeclaredSourceMappingLoadsPortablePdb(string? declaredKind)
    {
        var assemblyPath = typeof(CheckedJitBridgeArguments).Assembly.Location;
        var method = typeof(CheckedJitBridgeArguments).GetMethod(
            nameof(CheckedJitBridgeArguments.Parse),
            BindingFlags.Public | BindingFlags.Static)!;

        var maps = CheckedJitSourceMapping.LoadForDeclaredKind(assemblyPath, declaredKind);

        Assert.Contains(method.MetadataToken, maps.Keys);
    }

    [Fact]
    public void UnknownDeclaredSourceMappingFailsClosed()
    {
        var assemblyPath = typeof(CheckedJitBridgeArguments).Assembly.Location;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CheckedJitSourceMapping.LoadForDeclaredKind(assemblyPath, "method"));

        Assert.Contains("Unsupported Checked JIT source mapping kind", exception.Message);
    }

    [Fact]
    public void ValidCheckedJitMarkersMapToPortablePdbPoints()
    {
        var source = CreateSourceMap();
        const string section =
            "; Assembly listing for method Sample.Type:Add(int,int):int\n" +
            "; INLRT @ 0x0\n" +
            "       mov      eax, ecx\n" +
            "; INLRT @ 0x4\n" +
            "       add      eax, edx\n" +
            "; Total bytes of code 4\n";

        var mapped = CheckedJitSourceMapping.MapSection(section, source);

        Assert.Equal("checked-jit-debug-info", mapped.Source);
        Assert.Equal(2, mapped.Ranges.Count);
        Assert.All(mapped.Ranges, range => Assert.Equal("sequence-point", range.Precision));
    }

    [Fact]
    public void CheckedJitMarkersRetainNativeEvidenceAndSerializeItForPromotion()
    {
        var source = CreateSourceMap();
        const string section =
            "; Assembly listing for method Sample.Type:Add(int,int):int\n" +
            "G_M000_IG01:                ;; offset=0x0000\n" +
            "; INLRT @ 0x0\n" +
            "       55                   push     rbp\n" +
            "       4883EC10             sub      rsp, 16\n" +
            "; INLRT @ 0x4\n" +
            "       90                   nop\n" +
            "; Total bytes of code 6\n";

        var result = CreatePreparedResult(
            "Sample.Type.Add",
            0x06000001,
            "Sample.Type:Add(int,int):int");
        CheckedJitDisassemblyDocument.SelectPreparedMethods(
            section,
            [result],
            new Dictionary<int, CheckedMethodSourceMap>
            {
                [result.MetadataToken] = source
            });

        var evidence = result.EvidenceRanges;
        Assert.Equal(2, evidence.Count);
        Assert.Equal(new JitEvidenceRange(0, 0, 5, "Sample.cs", 2, 1, 2, 13), evidence[0]);
        Assert.Equal(new JitEvidenceRange(4, 5, 6, "Sample.cs", 3, 1, 3, 13), evidence[1]);

        using var payload = JsonDocument.Parse(BridgePayloadCodec.Serialize(
            new JitSummaryPayload("9.0.0", "Sample", null, [result])));
        var serializedEvidence = payload.RootElement
            .GetProperty("Methods")[0]
            .GetProperty("EvidenceRanges");
        Assert.Equal(2, serializedEvidence.GetArrayLength());
        Assert.Equal(5, serializedEvidence[0].GetProperty("NativeEndOffset").GetInt32());
    }

    [Fact]
    public void CheckedJitCodeBytesProducePdbVerifiableEvidenceRanges()
    {
        var source = CreateSourceMap();
        const string section =
            "; Assembly listing for method Sample.Type:Add(int,int):int\n" +
            "G_M000_IG01:  ;; offset=0x0000\n" +
            "; INLRT @ 0x0\n" +
            "       55                   push     rbp\n" +
            "; INLRT @ 0x4\n" +
            "       4883EC10             sub      rsp, 16\n" +
            "; Total bytes of code 5\n";

        var mapped = CheckedJitSourceMapping.MapSection(section, source);

        var evidence = mapped.Ranges
            .Select(static range => range.EvidenceRange)
            .OfType<JitEvidenceRange>()
            .ToArray();
        Assert.Equal(2, evidence.Length);
        Assert.Equal(new JitEvidenceRange(0, 0, 1, "Sample.cs", 2, 1, 2, 13), evidence[0]);
        Assert.Equal(new JitEvidenceRange(4, 1, 5, "Sample.cs", 3, 1, 3, 13), evidence[1]);
    }

    [Fact]
    public void UnmappedCodeBytesStillAdvanceLaterCheckedJitEvidenceOffsets()
    {
        var source = CreateSourceMap();
        const string section =
            "; Assembly listing for method Sample.Type:Add(int,int):int\n" +
            "G_M000_IG01:  ;; offset=0x0000\n" +
            "       55                   push     rbp\n" +
            "; INLRT @ 0x0\n" +
            "       4883EC10             sub      rsp, 16\n" +
            "; INL01 @ 0x0\n" +
            "       90                   nop\n" +
            "; INLRT @ 0x4\n" +
            "       C3                   ret\n" +
            "; Total bytes of code 7\n";

        var mapped = CheckedJitSourceMapping.MapSection(section, source);

        var evidence = mapped.Ranges
            .Select(static range => range.EvidenceRange)
            .OfType<JitEvidenceRange>()
            .ToArray();
        Assert.Equal(2, evidence.Length);
        Assert.Equal((1, 5), (evidence[0].NativeStartOffset, evidence[0].NativeEndOffset));
        Assert.Equal((6, 7), (evidence[1].NativeStartOffset, evidence[1].NativeEndOffset));
    }

    [Fact]
    public void MissingCodeBytesDoNotProducePartialCheckedJitEvidence()
    {
        var source = CreateSourceMap();
        const string section =
            "; Assembly listing for method Sample.Type:Add(int,int):int\n" +
            "G_M000_IG01:  ;; offset=0x0000\n" +
            "; INLRT @ 0x0\n" +
            "       55                   push     rbp\n" +
            "       add      eax, edx\n" +
            "; Total bytes of code 1\n";

        var mapped = CheckedJitSourceMapping.MapSection(section, source);

        var range = Assert.Single(mapped.Ranges);
        Assert.Null(range.EvidenceRange);
    }

    [Fact]
    public void InstructionGroupWithoutANewMarkerDoesNotReuseThePreviousSequencePoint()
    {
        var source = CreateSourceMap();
        const string section =
            "; Assembly listing for method Sample.Type:Add(int,int):int\n" +
            "G_M000_IG01:                ;; offset=0x0000\n" +
            "; INLRT @ 0x0\n" +
            "       mov      eax, ecx\n" +
            "G_M000_IG02:                ;; offset=0x0002\n" +
            "       add      eax, edx\n" +
            "; Total bytes of code 4\n";

        var mapped = CheckedJitSourceMapping.MapSection(section, source);

        Assert.Equal("checked-jit-debug-info", mapped.Source);
        var range = Assert.Single(mapped.Ranges);
        Assert.Equal("sequence-point", range.Precision);
        Assert.Equal(3, range.OutputRange.StartLine);
        Assert.Equal(3, range.OutputRange.EndLine);
    }

    [Theory]
    [InlineData("; INLRT @ ???")]
    [InlineData("; INLRT @ 0x20")]
    [InlineData("; no checked jit marker")]
    public void MissingUnknownOrOutOfRangeMarkersFallBackToMethod(string marker)
    {
        var source = CreateSourceMap();
        var section =
            "; Assembly listing for method Sample.Type:Add(int,int):int\n" +
            marker + "\n" +
            "       mov      eax, ecx\n" +
            "; Total bytes of code 2\n";

        var mapped = CheckedJitSourceMapping.MapSection(section, source);

        Assert.Equal("method", mapped.Source);
        Assert.Single(mapped.Ranges);
        Assert.Equal("method", mapped.Ranges[0].Precision);
    }

    [Fact]
    public void MultipleSectionsUseExactOutputLineOffsets()
    {
        var first = CreatePreparedResult(
            "Sample.Type.First",
            0x06000001,
            "Sample.Type:First():int");
        var second = CreatePreparedResult(
            "Sample.Type.Second",
            0x06000002,
            "Sample.Type:Second():int");
        const string assembly =
            "; Assembly listing for method Sample.Type:First():int\n" +
            "       mov      eax, 1\n" +
            "; Total bytes of code 1\n" +
            "; Assembly listing for method Sample.Type:Second():int\n" +
            "       mov      eax, 2\n" +
            "; Total bytes of code 1\n";

        var output = CheckedJitDisassemblyDocument.SelectPreparedMethods(
            assembly,
            [first, second],
            new Dictionary<int, CheckedMethodSourceMap>
            {
                [first.MetadataToken] = CreateSourceMap(),
                [second.MetadataToken] = CreateSourceMap()
            });

        Assert.Contains("First():int", output);
        Assert.Contains("Second():int", output);
        Assert.Equal(1, first.LinkedRanges.Single().OutputRange.StartLine);
        Assert.Equal(5, second.LinkedRanges.Single().OutputRange.StartLine);
    }

    [Fact]
    public void OverloadedSectionsBindByFullSignatureInsteadOfPreparationOrder()
    {
        var integer = CreatePreparedResult(
            "Sample.Type.Overload",
            0x06000001,
            "Sample.Type:Overload(int):int");
        var @long = CreatePreparedResult(
            "Sample.Type.Overload",
            0x06000002,
            "Sample.Type:Overload(long):long");
        const string assembly =
            "; Assembly listing for method Sample.Type:Overload(int):int\n" +
            "       mov      eax, ecx\n" +
            "; Total bytes of code 1\n" +
            "; Assembly listing for method Sample.Type:Overload(long):long\n" +
            "       mov      rax, rcx\n" +
            "; Total bytes of code 1\n";

        var output = CheckedJitDisassemblyDocument.SelectPreparedMethods(
            assembly,
            [@long, integer],
            new Dictionary<int, CheckedMethodSourceMap>
            {
                [integer.MetadataToken] = CreateSourceMap(10),
                [@long.MetadataToken] = CreateSourceMap(20)
            });

        Assert.Contains("Overload(int):int", output);
        Assert.Contains("Overload(long):long", output);
        Assert.Equal(1, integer.NativeCodeSize);
        Assert.Equal(1, @long.NativeCodeSize);
        Assert.Equal(1, integer.InstructionCount);
        Assert.Equal(1, @long.InstructionCount);
        Assert.Equal(10, integer.LinkedRanges.Single().SourceRange.StartLine);
        Assert.Equal(20, @long.LinkedRanges.Single().SourceRange.StartLine);
    }

    [Fact]
    public void NamespaceShortenedDeclaringTypeHeaderBindsUniqueMetadataSignature()
    {
        var method = typeof(CheckedJitBridgeTests).GetMethod(
            nameof(SignatureOverload),
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            [typeof(int)],
            modifiers: null)!;
        using var metadata = ManagedAssemblyMetadata.Open(typeof(CheckedJitBridgeTests).Assembly.Location);
        var signatureIdentity = metadata.ValidateMethod(CreateChildMethodRecord(method));
        var target = new JitMethodResult(
            $"0x{method.MetadataToken:x8}",
            method.MetadataToken,
            $"{typeof(CheckedJitBridgeTests).FullName}.{method.Name}",
            "prepared",
            "0x1234",
            null,
            signatureIdentity);
        var assembly =
            $"; Assembly listing for method {nameof(CheckedJitBridgeTests)}:SignatureOverload(int):int\n" +
            "       mov      eax, ecx\n" +
            "; Total bytes of code 1\n";

        var output = CheckedJitDisassemblyDocument.SelectPreparedMethods(
            assembly,
            [target],
            new Dictionary<int, CheckedMethodSourceMap>
            {
                [target.MetadataToken] = CreateSourceMap()
            });

        Assert.Contains("CheckedJitBridgeTests:SignatureOverload(int):int", output);
        Assert.Equal(1, target.NativeCodeSize);
        Assert.Equal(1, target.InstructionCount);
        Assert.Single(target.LinkedRanges);
    }

    [Fact]
    public void NamespaceShortenedDeclaringTypeCollisionRemainsVisibleButUnbound()
    {
        const string shortenedHeader = "SharedType:Target():int";
        var first = CreatePreparedResult(
            "First.Namespace.SharedType.Target",
            0x06000001,
            "First.Namespace.SharedType:Target():int",
            namespaceShortenedNameKey: "SharedType:Target",
            namespaceShortenedHeaderKey: shortenedHeader);
        var second = CreatePreparedResult(
            "Second.Namespace.SharedType.Target",
            0x06000002,
            "Second.Namespace.SharedType:Target():int",
            namespaceShortenedNameKey: "SharedType:Target",
            namespaceShortenedHeaderKey: shortenedHeader);
        var assembly =
            $"; Assembly listing for method {shortenedHeader}\n" +
            "       mov      eax, 1\n" +
            "; Total bytes of code 1\n";

        var output = CheckedJitDisassemblyDocument.SelectPreparedMethods(
            assembly,
            [first, second],
            new Dictionary<int, CheckedMethodSourceMap>());

        Assert.Contains(shortenedHeader, output);
        Assert.Equal(0, first.NativeCodeSize);
        Assert.Equal(0, second.NativeCodeSize);
        Assert.Equal(0, first.InstructionCount);
        Assert.Equal(0, second.InstructionCount);
    }

    [Fact]
    public void NamespaceShortenedUnrelatedSignatureIsFilteredOut()
    {
        var target = CreatePreparedResult(
            "Sample.Namespace.SharedType.Target",
            0x06000001,
            "Sample.Namespace.SharedType:Target(int):int",
            namespaceShortenedNameKey: "SharedType:Target",
            namespaceShortenedHeaderKey: "SharedType:Target(int):int");
        const string assembly =
            "; Assembly listing for method SharedType:Target(long):int\n" +
            "       mov      rax, rcx\n" +
            "; Total bytes of code 1\n";

        var output = CheckedJitDisassemblyDocument.SelectPreparedMethods(
            assembly,
            [target],
            new Dictionary<int, CheckedMethodSourceMap>());

        Assert.Empty(output);
        Assert.Equal(0, target.NativeCodeSize);
        Assert.Equal(0, target.InstructionCount);
    }

    [Fact]
    public void ExactHeaderTakesPriorityOverTheSameMethodsShortenedHeader()
    {
        var target = CreatePreparedResult(
            "Sample.Namespace.SharedType.Target",
            0x06000001,
            "Sample.Namespace.SharedType:Target():int",
            namespaceShortenedNameKey: "SharedType:Target",
            namespaceShortenedHeaderKey: "SharedType:Target():int");
        const string assembly =
            "; Assembly listing for method SharedType:Target():int\n" +
            "       mov      eax, 2\n" +
            "; Total bytes of code 2\n" +
            "; Assembly listing for method Sample.Namespace.SharedType:Target():int\n" +
            "       mov      eax, 1\n" +
            "; Total bytes of code 1\n";

        var output = CheckedJitDisassemblyDocument.SelectPreparedMethods(
            assembly,
            [target],
            new Dictionary<int, CheckedMethodSourceMap>());

        Assert.Contains("SharedType:Target():int", output);
        Assert.Contains("Sample.Namespace.SharedType:Target():int", output);
        Assert.Equal(1, target.NativeCodeSize);
        Assert.Equal(1, target.InstructionCount);
    }

    [Fact]
    public void ConstructedGenericDeclaringTypesBindByCanonicalHeader()
    {
        const int metadataToken = 0x06000001;
        var integer = CreatePreparedResult(
            "Sample.GenericType`1[[System.Int32]].Echo",
            metadataToken,
            "Sample.GenericType`1[int]:Echo(int):int",
            "type-int");
        var reference = CreatePreparedResult(
            "Sample.GenericType`1[[System.String]].Echo",
            metadataToken,
            "Sample.GenericType`1[System.__Canon]:Echo(System.__Canon):System.__Canon",
            "type-string");
        const string assembly =
            "; Assembly listing for method Sample.GenericType`1[System.__Canon]:Echo(System.__Canon):System.__Canon (Tier0)\n" +
            "       mov      rax, rdx\n" +
            "; Total bytes of code 2\n" +
            "; Assembly listing for method Sample.GenericType`1[int]:Echo(int):int (FullOpts)\n" +
            "       mov      eax, ecx\n" +
            "; Total bytes of code 1\n";

        var output = CheckedJitDisassemblyDocument.SelectPreparedMethods(
            assembly,
            [integer, reference],
            new Dictionary<int, CheckedMethodSourceMap>
            {
                [metadataToken] = CreateSourceMap()
            });

        Assert.Contains("GenericType`1[int]", output);
        Assert.Contains("GenericType`1[System.__Canon]", output);
        Assert.Equal(1, integer.NativeCodeSize);
        Assert.Equal(2, reference.NativeCodeSize);
        Assert.Single(integer.LinkedRanges);
        Assert.Single(reference.LinkedRanges);
    }

    [Fact]
    public void ConstructedGenericMethodsBindByCanonicalHeader()
    {
        const int metadataToken = 0x06000001;
        var integer = CreatePreparedResult(
            "Sample.Type.Generic",
            metadataToken,
            "Sample.Type:Generic[int](int):int",
            "method-int");
        var reference = CreatePreparedResult(
            "Sample.Type.Generic",
            metadataToken,
            "Sample.Type:Generic[System.__Canon](System.__Canon):System.__Canon",
            "method-string");
        const string assembly =
            "; Assembly listing for method Sample.Type:Generic[int](int):int\n" +
            "       mov      eax, ecx\n" +
            "; Total bytes of code 1\n" +
            "; Assembly listing for method Sample.Type:Generic[System.__Canon](System.__Canon):System.__Canon\n" +
            "       mov      rax, rdx\n" +
            "; Total bytes of code 2\n";

        var output = CheckedJitDisassemblyDocument.SelectPreparedMethods(
            assembly,
            [reference, integer],
            new Dictionary<int, CheckedMethodSourceMap>
            {
                [metadataToken] = CreateSourceMap()
            });

        Assert.Contains("Generic[int]", output);
        Assert.Contains("Generic[System.__Canon]", output);
        Assert.Equal(1, integer.NativeCodeSize);
        Assert.Equal(2, reference.NativeCodeSize);
        Assert.Single(integer.LinkedRanges);
        Assert.Single(reference.LinkedRanges);
    }

    [Fact]
    public void CanonicalReferenceCollisionRemainsVisibleButUnbound()
    {
        const string header =
            "Sample.Type:Generic[System.__Canon](System.__Canon):System.__Canon";
        var first = CreatePreparedResult("Sample.Type.Generic", 0x06000001, header, "method-string");
        var second = CreatePreparedResult("Sample.Type.Generic", 0x06000001, header, "method-object");
        var assembly =
            $"; Assembly listing for method {header}\n" +
            "       mov      rax, rdx\n" +
            "; Total bytes of code 2\n";

        var output = CheckedJitDisassemblyDocument.SelectPreparedMethods(
            assembly,
            [first, second],
            new Dictionary<int, CheckedMethodSourceMap>
            {
                [first.MetadataToken] = CreateSourceMap()
            });

        Assert.Contains(header, output);
        Assert.Equal(0, first.NativeCodeSize);
        Assert.Equal(0, second.NativeCodeSize);
        Assert.Empty(first.LinkedRanges);
        Assert.Empty(second.LinkedRanges);
        Assert.Equal("none", first.MappingSource);
        Assert.Equal("none", second.MappingSource);
    }

    [Fact]
    public void DuplicateSectionHeadersRemainVisibleButUnbound()
    {
        const string header = "Sample.Type:Duplicate():int";
        var target = CreatePreparedResult("Sample.Type.Duplicate", 0x06000001, header);
        var assembly =
            $"; Assembly listing for method {header}\n" +
            "       mov      eax, 1\n" +
            "; Total bytes of code 1\n" +
            $"; Assembly listing for method {header}\n" +
            "       mov      eax, 2\n" +
            "; Total bytes of code 1\n";

        var output = CheckedJitDisassemblyDocument.SelectPreparedMethods(
            assembly,
            [target],
            new Dictionary<int, CheckedMethodSourceMap>
            {
                [target.MetadataToken] = CreateSourceMap()
            });

        Assert.Equal(2, CountOccurrences(output, header));
        Assert.Equal(0, target.NativeCodeSize);
        Assert.Empty(target.LinkedRanges);
        Assert.Equal("none", target.MappingSource);
    }

    [Fact]
    public void UnrelatedJitSectionsAreFilteredOut()
    {
        var target = CreatePreparedResult(
            "Sample.Type.Target",
            0x06000001,
            "Sample.Type:Target():int");
        const string assembly =
            "; Assembly listing for method Sample.Type:Unrelated():int\n" +
            "       mov      eax, 0\n" +
            "; Total bytes of code 1\n" +
            "; Assembly listing for method Sample.Type:Target():int\n" +
            "       mov      eax, 1\n" +
            "; Total bytes of code 1\n";

        var output = CheckedJitDisassemblyDocument.SelectPreparedMethods(
            assembly,
            [target],
            new Dictionary<int, CheckedMethodSourceMap>());

        Assert.DoesNotContain("Unrelated", output);
        Assert.Contains("Target():int", output);
    }

    [Fact]
    public void RuntimeFrameWriterProducesOnlyFramedLines()
    {
        using var stream = new MemoryStream();
        using (var writer = new RuntimeFrameWriter(stream))
        {
            writer.Write(RuntimeFrameKind.JitAssembly, "raw-jit-bytes"u8.ToArray());
            writer.Write(RuntimeFrameKind.Exit, "{}"u8.ToArray());
        }

        var wireText = Encoding.ASCII.GetString(stream.ToArray());
        Assert.DoesNotContain("raw-jit-bytes", wireText);
        var lines = wireText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        for (var index = 0; index < lines.Length; index++)
        {
            var frame = Convert.FromBase64String(lines[index]);
            Assert.Equal("SLNR", Encoding.ASCII.GetString(frame, 0, 4));
            Assert.Equal(index + 1, BinaryPrimitives.ReadInt64LittleEndian(frame.AsSpan(6, 8)));
            Assert.Equal(frame.Length - 18, BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(14, 4)));
        }
    }

    private static CheckedMethodSourceMap CreateSourceMap(int startLine = 1) => new(
        IlLength: 8,
        [
            new CheckedSourcePoint(0, "Sample.cs", new JitTextRange(startLine, 0, startLine, 12)),
            new CheckedSourcePoint(4, "Sample.cs", new JitTextRange(startLine + 1, 0, startLine + 1, 12))
        ]);

    private static ChildMethodRecord CreateChildMethodRecord(MethodInfo method) =>
        new(
            JitMethodSignatures.CreateMethodIdentity(method.MetadataToken, [], []),
            method.MetadataToken,
            $"{method.DeclaringType!.FullName}.{method.Name}",
            [],
            [],
            "prepared",
            "0x1234",
            null);

    private static JitMethodResult CreatePreparedResult(
        string displayName,
        int metadataToken,
        string headerKey,
        string? methodIdentity = null,
        string? namespaceShortenedNameKey = null,
        string? namespaceShortenedHeaderKey = null)
    {
        Assert.True(JitMethodSignatures.TryParseHeader(headerKey, out var signatureIdentity));
        signatureIdentity = signatureIdentity with
        {
            NamespaceShortenedNameKey = namespaceShortenedNameKey,
            NamespaceShortenedHeaderKey = namespaceShortenedHeaderKey
        };
        return new JitMethodResult(
            methodIdentity ?? $"0x{metadataToken:x8}",
            metadataToken,
            displayName,
            "prepared",
            "0x1234",
            null,
            signatureIdentity);
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var start = 0;
        while ((start = value.IndexOf(search, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += search.Length;
        }
        return count;
    }

    private static BoundedChildProcessLimits CreateProcessLimits(TimeSpan executionTimeout) =>
        new(
            standardOutputBytes: 1_024,
            standardErrorBytes: 1_024,
            totalOutputBytes: 2_048,
            executionTimeout,
            cleanupTimeout: TimeSpan.FromSeconds(5));

    private static ProcessStartInfo CreateHangingFixtureStartInfo()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "SharpLabNext.RunnerFixture.dll");
        Assert.True(File.Exists(fixture), $"The process fixture was not found at '{fixture}'.");
        var startInfo = new ProcessStartInfo(FindDotnetHost())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(fixture);
        startInfo.ArgumentList.Add("compiler-child-hang");
        return startInfo;
    }

    private static string FindDotnetHost()
    {
        var executable = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet";
        var runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
        var candidate = Path.GetFullPath(Path.Combine(runtimeDirectory, "..", "..", "..", executable));
        Assert.True(File.Exists(candidate), $"The dotnet host was not found at '{candidate}'.");
        return candidate;
    }

    private static void AssertProcessExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            Assert.True(process.HasExited);
        }
        catch (ArgumentException)
        {
            // A reaped process no longer has a process-table entry.
        }
    }

    private static T SignatureGenericMethod<T>(T value) => value;

    private static int SignatureOverload(int value) => value;

    private static long SignatureOverload(long value) => value;

    private sealed class SignatureGenericType<T>
    {
        private int _calls;

        public T Echo(T value)
        {
            _calls++;
            return value;
        }
    }
}
