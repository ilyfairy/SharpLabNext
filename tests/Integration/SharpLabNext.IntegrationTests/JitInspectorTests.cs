extern alias JitInspector;

using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SharpLabNext.RuntimeProtocol;
using JitEvidenceRange = JitInspector::JitEvidenceRange;
using JitNativeIlRange = JitInspector::JitNativeIlRange;
using JitNativeMapLog = JitInspector::JitNativeMapLog;
using JitNativeMethodMap = JitInspector::JitNativeMethodMap;
using JitRichIlPoint = JitInspector::JitRichIlPoint;
using JitRichMapLog = JitInspector::JitRichMapLog;
using JitRichMethodMap = JitInspector::JitRichMethodMap;
using JitInspectorProgram = JitInspector::JitInspectorProgram;
using JitMethodResult = JitInspector::JitMethodResult;
using JitSourceMapping = JitInspector::JitSourceMapping;
using JitSourcePoint = JitInspector::JitSourcePoint;
using JitSourceTextRange = JitInspector::JitSourceTextRange;

namespace SharpLabNext.IntegrationTests;

public sealed class JitInspectorTests
{
    [Fact]
    public void InspectorIncludesInstanceConstructorsInCurrentMethodFiltering()
    {
        var inspectorPath = Path.Combine(AppContext.BaseDirectory, "SharpLabNext.JitInspector.dll");
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "SharpLabNext.RunnerFixture.dll");
        var inspectorAssembly = Assembly.LoadFrom(inspectorPath);
        var fixtureAssembly = Assembly.LoadFrom(fixturePath);
        var programType = inspectorAssembly.GetType("JitInspectorProgram");
        Assert.NotNull(programType);
        var inspectAssembly = programType.GetMethod(
            "InspectAssembly",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(inspectAssembly);

        var results = Assert.IsAssignableFrom<System.Collections.IEnumerable>(
            inspectAssembly.Invoke(null, [fixtureAssembly, "FixtureNode..ctor"]));
        var displayNames = results.Cast<object>()
            .Select(result => Assert.IsType<string>(result.GetType().GetProperty("DisplayName")?.GetValue(result)))
            .ToArray();

        Assert.Contains(displayNames, static name => name.EndsWith("FixtureNode..ctor", StringComparison.Ordinal));
    }

    [Fact]
    public void InspectorInstantiatesOnlyAttributedGenericMethods()
    {
        var results = InspectFixture("GenericFixture");
        var inspected = results.Cast<object>()
            .Select(result => new
            {
                Method = Assert.IsType<string>(result.GetType().GetProperty("Method")?.GetValue(result)),
                DisplayName = Assert.IsType<string>(result.GetType().GetProperty("DisplayName")?.GetValue(result)),
                Status = Assert.IsType<string>(result.GetType().GetProperty("Status")?.GetValue(result))
            })
            .ToArray();

        var identity = Assert.Single(inspected, static result => result.DisplayName.EndsWith(
            "GenericFixture.Identity",
            StringComparison.Ordinal));
        Assert.Equal("prepared", identity.Status);
        Assert.Contains("System.Int32", identity.Method, StringComparison.Ordinal);
        Assert.DoesNotContain(inspected, static result => result.DisplayName.EndsWith(
            "GenericFixture.Unspecified",
            StringComparison.Ordinal));
    }

    [Fact]
    public void AssemblyStatisticsDoNotMatchLeadingDynamicClassHeadersToUserMethods()
    {
        var inspectorPath = Path.Combine(AppContext.BaseDirectory, "SharpLabNext.JitInspector.dll");
        var inspectorAssembly = Assembly.LoadFrom(inspectorPath);
        var programType = inspectorAssembly.GetType("JitInspectorProgram");
        Assert.NotNull(programType);
        var methodNamesMatch = programType.GetMethod(
            "MethodNamesMatch",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(methodNamesMatch);

        Assert.False(Assert.IsType<bool>(methodNamesMatch.Invoke(
            null,
            ["(dynamicClass).IL_STUB_PInvoke(System.IntPtr).int", "Program.CurrentTarget"])));
        Assert.True(Assert.IsType<bool>(methodNamesMatch.Invoke(
            null,
            ["Program.CurrentTarget().int", "Program.CurrentTarget"])));
        Assert.False(Assert.IsType<bool>(methodNamesMatch.Invoke(
            null,
            ["Program.<Main>(System.String[]).int", "Program.<Main>$"])));
        Assert.True(Assert.IsType<bool>(methodNamesMatch.Invoke(
            null,
            ["Program.<Main>$(System.String[]).int", "Program.<Main>$"])));
        Assert.True(Assert.IsType<bool>(methodNamesMatch.Invoke(
            null,
            ["GenericFixture.Identity[int](int).int", "GenericFixture.Identity"])));
    }

    [Fact]
    public async Task InspectorPreparesMethodsInsideItsOwnProcess()
    {
        var jitOutputPath = Path.Combine(Path.GetTempPath(), $"sharplabnext-jit-{Guid.NewGuid():N}.asm");
        var inspectorPath = Path.Combine(AppContext.BaseDirectory, "SharpLabNext.JitInspector.dll");
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "SharpLabNext.RunnerFixture.dll");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(inspectorPath);
        startInfo.ArgumentList.Add(fixturePath);
        startInfo.ArgumentList.Add("*Program*");
        startInfo.Environment["COMPlus_JitDisasm"] = "*";
        startInfo.Environment["COMPlus_JitDisasmAssemblies"] = "SharpLabNext.RunnerFixture";
        startInfo.Environment["COMPlus_JitDisasmWithCodeBytes"] = "1";
        startInfo.Environment["DOTNET_JitDisasmWithCodeBytes"] = "1";
        startInfo.Environment["COMPlus_JitStdOutFile"] = jitOutputPath;
        startInfo.Environment["SHARPLABNEXT_JIT_OUTPUT_PATH"] = jitOutputPath;
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start JIT Inspector.");
        var errorTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        var frames = new List<RuntimeFrame>();
        var reader = new RuntimeFrameLogReader(process.StandardOutput.BaseStream);
        while (await reader.ReadAsync(cancellationToken: TestContext.Current.CancellationToken) is { } frame)
        {
            frames.Add(frame);
        }

        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        var error = await errorTask;

        Assert.Equal(0, process.ExitCode);
        Assert.Empty(error);
        var summary = Assert.Single(frames, static frame => frame.Kind == RuntimeFrameKind.JitSummary);
        using var result = JsonDocument.Parse(summary.Payload);
        Assert.Contains(
            result.RootElement.GetProperty("Methods").EnumerateArray(),
            method =>
                method.GetProperty("Status").GetString() == "prepared" &&
                method.GetProperty("NativeCodeSize").GetInt32() > 0 &&
                method.GetProperty("InstructionCount").GetInt32() > 0);
        Assert.Contains(frames, static frame => frame.Kind == RuntimeFrameKind.Exit);
        var assemblyText = Encoding.UTF8.GetString(frames
            .Where(static frame => frame.Kind == RuntimeFrameKind.JitAssembly)
            .SelectMany(static frame => frame.Payload.ToArray())
            .ToArray());
        Assert.True(
            assemblyText.Length > 0,
            "JIT inspection must return native assembly text, not only prepared method addresses.");
        Assert.DoesNotContain(
            "; Assembly listing for method (dynamicClass)",
            assemblyText,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "; Assembly listing for method JitInspectorProgram:",
            assemblyText,
            StringComparison.Ordinal);
        File.Delete(jitOutputPath);
    }

    [Fact]
    public void InspectorUsesASeparateSynchronousEntryPoint()
    {
        var inspectorPath = Path.Combine(AppContext.BaseDirectory, "SharpLabNext.JitInspector.dll");
        var inspectorAssembly = Assembly.LoadFrom(inspectorPath);
        var entryPoint = inspectorAssembly.EntryPoint;

        Assert.NotNull(entryPoint);
        Assert.Equal("JitInspectorBootstrap", entryPoint!.DeclaringType?.Name);
        Assert.Equal(typeof(int), entryPoint.ReturnType);
        Assert.DoesNotContain(
            inspectorAssembly.GetTypes(),
            static type => string.Equals(type.FullName, "Program", StringComparison.Ordinal));
    }

    [Fact]
    public void NativeMapLogRequiresCompleteBoundedMethodDefRecords()
    {
        var parsed = JitNativeMapLog.ParseLine(
            "handle=1234 token=06000001 native=5678 count=3 -2:0:0 0:0:3 9:3:17");

        Assert.NotNull(parsed);
        Assert.Equal((nuint)0x1234, parsed.MethodHandle);
        Assert.Equal(0x06000001, parsed.MetadataToken);
        Assert.Equal(3, parsed.Ranges.Count);
        var controlFlowMap = JitNativeMapLog.ParseLine(
            "handle=1234 token=06000001 native=5678 count=7 " +
            "0:4:6 2:6:8 6:15:17 10:17:19 14:8:15 14:19:23 20:23:23");
        Assert.NotNull(controlFlowMap);
        Assert.Equal(
            [4U, 6U, 8U, 15U, 17U, 19U, 23U],
            controlFlowMap.Ranges.Select(static range => range.NativeStart));
        Assert.Null(JitNativeMapLog.ParseLine(
            "handle=1234 token=06000001 native=5678 count=2 0:0:3"));
        Assert.Null(JitNativeMapLog.ParseLine(
            "handle=1234 token=02000001 native=5678 count=1 0:0:3"));
        Assert.Null(JitNativeMapLog.ParseLine(
            "handle=1234 token=06000001 native=5678 count=2 0:3:6 1:2:7"));
        Assert.Null(JitNativeMapLog.ParseLine(
            "handle=1234 token=06000001 native=5678 count=1 0:0:70000000"));
    }

    [Fact]
    public void NativeMapLogPreservesDistinctVersionsAndDeduplicatesNativeStarts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sharplabnext-jit-map-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllLines(path,
            [
                "SLJM1",
                "handle=1234 token=06000001 native=1111 count=1 0:0:3",
                "handle=1234 token=06000001 native=2222 count=1 9:0:4",
                "handle=1234 token=06000001 native=1111 count=1 19:0:5"
            ]);

            var maps = JitNativeMapLog.Load(path);

            var versions = Assert.Single(maps).Value;
            Assert.Equal(2, versions.Count);
            Assert.Equal([(nuint)0x1111, (nuint)0x2222], versions.Select(static map => map.NativeCodeStart));
            Assert.Equal([0, 9], versions.Select(static map => map.Ranges[0].IlOffset));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RichMapLogRequiresCompleteBoundedVersionedRecords()
    {
        var parsed = JitRichMapLog.ParseLine(
            "method=1234 clr=7 nativeversion=2 ilversion=3 inline=2 count=4 " +
            "0:0:0:2 3:4:0:2 3:9:0:2 5:12:1:2");

        Assert.NotNull(parsed);
        Assert.Equal((nuint)0x1234, parsed.MethodHandle);
        Assert.Equal((ushort)7, parsed.ClrInstanceId);
        Assert.Equal((ulong)2, parsed.NativeVersionId);
        Assert.Equal((ulong)3, parsed.IlVersionId);
        Assert.Equal(4, parsed.Points.Count);
        Assert.Null(JitRichMapLog.ParseLine(
            "method=1234 clr=7 nativeversion=2 ilversion=3 inline=1 count=2 0:0:0:2"));
        Assert.Null(JitRichMapLog.ParseLine(
            "method=1234 clr=7 nativeversion=2 ilversion=3 inline=1 count=2 3:0:0:2 2:1:0:2"));
        Assert.Null(JitRichMapLog.ParseLine(
            "method=1234 clr=7 nativeversion=2 ilversion=3 inline=1 count=1 0:0:1:2"));
        Assert.Null(JitRichMapLog.ParseLine(
            "method=1234 clr=7 nativeversion=2 ilversion=3 inline=1 count=1 0:0:0:32"));
        Assert.Null(JitRichMapLog.ParseLine(
            "method=1234 clr=7 nativeversion=2 ilversion=3 inline=1 count=20001"));
    }

    [Fact]
    public void RichMapLogFailsClosedForMalformedOrDuplicateVersionRecords()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sharplabnext-jit-rich-map-{Guid.NewGuid():N}.txt");
        const string valid =
            "method=1234 clr=7 nativeversion=2 ilversion=3 inline=1 count=1 0:0:0:2";
        try
        {
            File.WriteAllLines(path, ["SLJR1", valid, "malformed"]);
            Assert.Empty(JitRichMapLog.Load(path));

            File.WriteAllLines(path, ["SLJR1", valid, valid]);
            Assert.Empty(JitRichMapLog.Load(path));

            File.WriteAllLines(path,
            [
                "SLJR1",
                valid,
                "method=1234 clr=7 nativeversion=4 ilversion=3 inline=1 count=1 1:1:0:2"
            ]);
            Assert.Equal(2, Assert.Single(JitRichMapLog.Load(path)).Value.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RichMapUsesLastRootAtDuplicateNativeOffsetAndOrdinaryCodeEnd()
    {
        var ordinary = new JitNativeMethodMap(
            0x2222,
            0x06000001,
            0x3333,
            [new JitNativeIlRange(0, 0, 10)]);
        IReadOnlyDictionary<nuint, IReadOnlyList<JitRichMethodMap>> richMaps =
            new Dictionary<nuint, IReadOnlyList<JitRichMethodMap>>
            {
                [ordinary.MethodHandle] =
                [
                    new JitRichMethodMap(
                        ordinary.MethodHandle,
                        7,
                        2,
                        3,
                        2,
                        [
                            new JitRichIlPoint(0, 0, 0, 2),
                            new JitRichIlPoint(3, 4, 0, 2),
                            new JitRichIlPoint(3, 9, 0, 2),
                            new JitRichIlPoint(4, 99, 1, 2),
                            new JitRichIlPoint(7, 15, 0, 2),
                            new JitRichIlPoint(10, 19, 0, 2)
                        ])
                ]
            };

        var mapped = JitInspectorProgram.SelectRichNativeMap(ordinary, richMaps);

        Assert.NotNull(mapped);
        Assert.Equal(
            [(0, 0U, 3U), (9, 3U, 7U), (15, 7U, 10U)],
            mapped.Ranges.Select(static range =>
                (range.IlOffset, range.NativeStart, range.NativeEnd)));
    }

    [Fact]
    public void RichMapRejectsAmbiguousVersionsAndOutOfBoundsRootOffsets()
    {
        var ordinary = new JitNativeMethodMap(
            0x2222,
            0x06000001,
            0x3333,
            [new JitNativeIlRange(0, 0, 10)]);
        var valid = new JitRichMethodMap(
            ordinary.MethodHandle,
            7,
            2,
            3,
            1,
            [new JitRichIlPoint(0, 0, 0, 2)]);

        Assert.Null(JitInspectorProgram.SelectRichNativeMap(
            ordinary,
            new Dictionary<nuint, IReadOnlyList<JitRichMethodMap>>
            {
                [ordinary.MethodHandle] = [valid, valid with { NativeVersionId = 4 }]
            }));
        Assert.Null(JitInspectorProgram.SelectRichNativeMap(
            ordinary,
            new Dictionary<nuint, IReadOnlyList<JitRichMethodMap>>
            {
                [ordinary.MethodHandle] =
                [valid with { Points = [new JitRichIlPoint(11, 3, 0, 2)] }]
            }));
    }

    [Fact]
    public void AssemblyStatisticsUsesRichMapOnlyWhenItAddsDistinctPdbSpans()
    {
        const int metadataToken = 0x06000001;
        const nuint methodHandle = 0x1234;
        const string section =
            """
            ; Assembly listing for method MappingFixture:Target(int):int (FullOpts)
            G_M000_IG01:                ;; offset=0x0000
                   ; INLRT @ 0x000[--]
                   90                   nop
                   ; INLRT @ 0x003[--]
                   90                   nop
                   ; INLRT @ 0x005[--]
                   90                   nop
                   ; INLRT @ 0x007[--]
                   C3                   ret
            ; Total bytes of code 4
            """;
        var result = new JitMethodResult(
            "0x06000001",
            metadataToken,
            methodHandle,
            0x5678,
            "MappingFixture.Target",
            "prepared",
            "0x5678",
            null,
            0,
            0,
            []);
        IReadOnlyDictionary<int, IReadOnlyList<JitSourcePoint>> sourcePoints =
            new Dictionary<int, IReadOnlyList<JitSourcePoint>>
            {
                [metadataToken] =
                [
                    new JitSourcePoint(0, "Program.cs", new JitSourceTextRange(1, 0, 1, 10)),
                    new JitSourcePoint(3, "Program.cs", new JitSourceTextRange(3, 0, 3, 10)),
                    new JitSourcePoint(5, "Program.cs", new JitSourceTextRange(5, 0, 5, 10)),
                    new JitSourcePoint(7, "Program.cs", new JitSourceTextRange(7, 0, 7, 10))
                ]
            };
        var ordinary = new JitNativeMethodMap(
            methodHandle,
            metadataToken,
            0x5678,
            [new JitNativeIlRange(0, 0, 4)]);
        IReadOnlyDictionary<nuint, IReadOnlyList<JitNativeMethodMap>> ordinaryMaps =
            new Dictionary<nuint, IReadOnlyList<JitNativeMethodMap>>
            {
                [methodHandle] = [ordinary]
            };
        IReadOnlyDictionary<nuint, IReadOnlyList<JitRichMethodMap>> richMaps =
            new Dictionary<nuint, IReadOnlyList<JitRichMethodMap>>
            {
                [methodHandle] =
                [
                    new JitRichMethodMap(
                        methodHandle,
                        0,
                        0,
                        0,
                        1,
                        [
                            new JitRichIlPoint(0, 0, 0, 2),
                            new JitRichIlPoint(1, 3, 0, 2),
                            new JitRichIlPoint(2, 5, 0, 2)
                        ])
                ]
            };
        var results = new List<JitMethodResult> { result };

        JitInspectorProgram.ApplyAssemblyStatistics(
            results,
            section,
            sourcePoints,
            ordinaryMaps,
            richMaps);

        Assert.Equal([1, 3, 5], results[0].LinkedRanges.Select(static range => range.SourceRange.StartLine));
        Assert.Equal("rich", results[0].MappingSource);

        results = [result];
        richMaps = new Dictionary<nuint, IReadOnlyList<JitRichMethodMap>>
        {
            [methodHandle] =
            [
                new JitRichMethodMap(
                    methodHandle,
                    0,
                    0,
                    0,
                    1,
                    [new JitRichIlPoint(0, 5, 0, 2)])
            ]
        };
        JitInspectorProgram.ApplyAssemblyStatistics(
            results,
            section,
            sourcePoints,
            ordinaryMaps,
            richMaps);

        Assert.Equal(1, Assert.Single(results[0].LinkedRanges).SourceRange.StartLine);
        Assert.Equal("ordinary", results[0].MappingSource);
    }

    [Fact]
    public void AssemblyStatisticsSelectsExactNativeVersionAndRejectsAmbiguousFallback()
    {
        const int metadataToken = 0x06000001;
        const nuint methodHandle = 0x1234;
        const string section =
            """
            ; Assembly listing for method MappingFixture:Target():int (FullOpts)
            G_M000_IG01:                ;; offset=0x0000
                   B82A000000           mov      eax, 42
                   C3                   ret
            ; Total bytes of code 6
            """;
        IReadOnlyDictionary<int, IReadOnlyList<JitSourcePoint>> sourcePoints =
            new Dictionary<int, IReadOnlyList<JitSourcePoint>>
            {
                [metadataToken] =
                [
                    new JitSourcePoint(0, "Program.cs", new JitSourceTextRange(1, 4, 1, 15)),
                    new JitSourcePoint(9, "Program.cs", new JitSourceTextRange(3, 4, 3, 16))
                ]
            };
        IReadOnlyDictionary<nuint, IReadOnlyList<JitNativeMethodMap>> nativeMaps =
            new Dictionary<nuint, IReadOnlyList<JitNativeMethodMap>>
            {
                [methodHandle] =
                [
                    new JitNativeMethodMap(
                        methodHandle,
                        metadataToken,
                        0x1111,
                        [new JitNativeIlRange(0, 0, 6)]),
                    new JitNativeMethodMap(
                        methodHandle,
                        metadataToken,
                        0x2222,
                        [new JitNativeIlRange(9, 0, 6)])
                ]
            };

        var exactResults = new List<JitMethodResult>
        {
            Result(nativeCodeStart: 0x2222)
        };
        JitInspectorProgram.ApplyAssemblyStatistics(exactResults, section, sourcePoints, nativeMaps);

        var exactRange = Assert.Single(exactResults[0].LinkedRanges);
        Assert.Equal("sequence-point", exactRange.Precision);
        Assert.Equal(3, exactRange.SourceRange.StartLine);
        Assert.Equal("ordinary", exactResults[0].MappingSource);

        var ambiguousResults = new List<JitMethodResult>
        {
            Result(nativeCodeStart: 0x3333)
        };
        JitInspectorProgram.ApplyAssemblyStatistics(ambiguousResults, section, sourcePoints, nativeMaps);

        var fallbackRange = Assert.Single(ambiguousResults[0].LinkedRanges);
        Assert.Equal("method", fallbackRange.Precision);
        Assert.Equal("method", ambiguousResults[0].MappingSource);

        JitMethodResult Result(nuint nativeCodeStart) => new(
            "0x06000001",
            metadataToken,
            methodHandle,
            nativeCodeStart,
            "MappingFixture.Target",
            "prepared",
            $"0x{nativeCodeStart:x}",
            null,
            0,
            0,
            []);
    }

    [Fact]
    public void AssemblyStatisticsDoesNotLetRicherRootMarkersReplaceProfilerMap()
    {
        const int metadataToken = 0x06000001;
        const nuint methodHandle = 0x1234;
        const string section =
            """
            ; Assembly listing for method MappingFixture:Target(int):int (FullOpts)
            G_M000_IG01:                ;; offset=0x0000
                   ; INLRT @ 0x000[--]
                   8BC1                 mov      eax, ecx
                   ; INLRT @ 0x003[--]
                   FFC0                 inc      eax
                   ; INLRT @ 0x005[--]
                   C3                   ret
            ; Total bytes of code 5
            """;
        var results = new List<JitMethodResult> { Result() };

        JitInspectorProgram.ApplyAssemblyStatistics(
            results,
            section,
            SourcePoints(),
            NativeMaps(new JitNativeIlRange(0, 0, 5)));

        var profilerRange = Assert.Single(results[0].LinkedRanges);
        Assert.Equal(1, profilerRange.SourceRange.StartLine);
        Assert.Equal("sequence-point", profilerRange.Precision);
        Assert.Equal("ordinary", results[0].MappingSource);

        JitMethodResult Result() => new(
            "0x06000001",
            metadataToken,
            methodHandle,
            0x5678,
            "MappingFixture.Target",
            "prepared",
            "0x5678",
            null,
            0,
            0,
            []);

        IReadOnlyDictionary<int, IReadOnlyList<JitSourcePoint>> SourcePoints() =>
            new Dictionary<int, IReadOnlyList<JitSourcePoint>>
            {
                [metadataToken] =
                [
                    new JitSourcePoint(0, "Program.cs", new JitSourceTextRange(1, 4, 1, 15)),
                    new JitSourcePoint(3, "Program.cs", new JitSourceTextRange(3, 4, 3, 16)),
                    new JitSourcePoint(5, "Program.cs", new JitSourceTextRange(5, 4, 5, 10))
                ]
            };

        IReadOnlyDictionary<nuint, IReadOnlyList<JitNativeMethodMap>> NativeMaps(
            params JitNativeIlRange[] ranges) =>
            new Dictionary<nuint, IReadOnlyList<JitNativeMethodMap>>
            {
                [methodHandle] =
                [new JitNativeMethodMap(methodHandle, metadataToken, 0x5678, ranges)]
            };
    }

    [Fact]
    public void AssemblyStatisticsLabelsTextMarkersOnlyOnNoProfilerFallback()
    {
        const int metadataToken = 0x06000001;
        const string section =
            """
            ; Assembly listing for method MappingFixture:Target(int):int (FullOpts)
            G_M000_IG01:                ;; offset=0x0000
                   ; INLRT @ 0x000[--]
                   8BC1                 mov      eax, ecx
                   ; INLRT @ 0x003[--]
                   FFC0                 inc      eax
            ; Total bytes of code 4
            """;
        IReadOnlyDictionary<int, IReadOnlyList<JitSourcePoint>> sourcePoints =
            new Dictionary<int, IReadOnlyList<JitSourcePoint>>
            {
                [metadataToken] =
                [
                    new JitSourcePoint(0, "Program.cs", new JitSourceTextRange(1, 4, 1, 15)),
                    new JitSourcePoint(3, "Program.cs", new JitSourceTextRange(3, 4, 3, 16))
                ]
            };
        var results = new List<JitMethodResult>
        {
            new(
                "0x06000001",
                metadataToken,
                0x1234,
                0x5678,
                "MappingFixture.Target",
                "prepared",
                "0x5678",
                null,
                0,
                0,
                [])
        };

        JitInspectorProgram.ApplyAssemblyStatistics(
            results,
            section,
            sourcePoints,
            new Dictionary<nuint, IReadOnlyList<JitNativeMethodMap>>());

        Assert.Equal([1, 3], results[0].LinkedRanges.Select(static range => range.SourceRange.StartLine));
        Assert.All(results[0].LinkedRanges, static range => Assert.Equal("sequence-point", range.Precision));
        Assert.Equal("marker", results[0].MappingSource);
    }

    [Fact]
    public void AssemblyStatisticsKeepsNativeRangesForUnknownAndInlineOnlyMarkers()
    {
        const int metadataToken = 0x06000001;
        const nuint methodHandle = 0x1234;
        const string section =
            """
            ; Assembly listing for method MappingFixture:Target(int):int (FullOpts)
            G_M000_IG01:                ;; offset=0x0000
                   ; INLRT @ ???
                   8BC1                 mov      eax, ecx
                   ; INL01 @ 0x003[--]
                   FFC0                 inc      eax
                   C3                   ret
            ; Total bytes of code 5
            """;
        IReadOnlyDictionary<int, IReadOnlyList<JitSourcePoint>> sourcePoints =
            new Dictionary<int, IReadOnlyList<JitSourcePoint>>
            {
                [metadataToken] =
                [
                    new JitSourcePoint(0, "Program.cs", new JitSourceTextRange(1, 4, 1, 15)),
                    new JitSourcePoint(3, "Program.cs", new JitSourceTextRange(3, 4, 3, 16))
                ]
            };
        IReadOnlyDictionary<nuint, IReadOnlyList<JitNativeMethodMap>> nativeMaps =
            new Dictionary<nuint, IReadOnlyList<JitNativeMethodMap>>
            {
                [methodHandle] =
                [new JitNativeMethodMap(
                    methodHandle,
                    metadataToken,
                    0x5678,
                    [new JitNativeIlRange(0, 0, 5)])]
            };
        var results = new List<JitMethodResult>
        {
            new(
                "0x06000001",
                metadataToken,
                methodHandle,
                0x5678,
                "MappingFixture.Target",
                "prepared",
                "0x5678",
                null,
                0,
                0,
                [])
        };

        JitInspectorProgram.ApplyAssemblyStatistics(results, section, sourcePoints, nativeMaps);

        var nativeRange = Assert.Single(results[0].LinkedRanges);
        Assert.Equal("sequence-point", nativeRange.Precision);
        Assert.Equal(1, nativeRange.SourceRange.StartLine);
        Assert.Equal(3, nativeRange.OutputRange.StartLine);
        Assert.Equal(6, nativeRange.OutputRange.EndLine);
    }

    [Fact]
    public void AssemblyStatisticsKeepsNativeRangesWhenMarkersAreEquallyRich()
    {
        const int metadataToken = 0x06000001;
        const nuint methodHandle = 0x1234;
        const string section =
            """
            ; Assembly listing for method MappingFixture:Target(int):int (FullOpts)
            G_M000_IG01:                ;; offset=0x0000
                   ; INLRT @ 0x003[--]
                   8BC1                 mov      eax, ecx
                   C3                   ret
            ; Total bytes of code 3
            """;
        IReadOnlyDictionary<int, IReadOnlyList<JitSourcePoint>> sourcePoints =
            new Dictionary<int, IReadOnlyList<JitSourcePoint>>
            {
                [metadataToken] =
                [
                    new JitSourcePoint(0, "Program.cs", new JitSourceTextRange(1, 4, 1, 15)),
                    new JitSourcePoint(3, "Program.cs", new JitSourceTextRange(3, 4, 3, 16))
                ]
            };
        IReadOnlyDictionary<nuint, IReadOnlyList<JitNativeMethodMap>> nativeMaps =
            new Dictionary<nuint, IReadOnlyList<JitNativeMethodMap>>
            {
                [methodHandle] =
                [new JitNativeMethodMap(
                    methodHandle,
                    metadataToken,
                    0x5678,
                    [new JitNativeIlRange(0, 0, 3)])]
            };
        var results = new List<JitMethodResult>
        {
            new(
                "0x06000001",
                metadataToken,
                methodHandle,
                0x5678,
                "MappingFixture.Target",
                "prepared",
                "0x5678",
                null,
                0,
                0,
                [])
        };

        JitInspectorProgram.ApplyAssemblyStatistics(results, section, sourcePoints, nativeMaps);

        var nativeRange = Assert.Single(results[0].LinkedRanges);
        Assert.Equal(1, nativeRange.SourceRange.StartLine);
    }

    [Fact]
    public void AssemblyStatisticsDoesNotMatchTopLevelMainToInspectorMainPrefix()
    {
        const string assemblyText =
            """
            ; Assembly listing for method Program:<Main>(System.String[]):int (FullOpts)
            G_M000_IG01:
                   call     [Program:<Main>$(System.String[]):int]
                   ret
            ; Total bytes of code 7

            ; Assembly listing for method Program:<Main>$(System.String[]):int (FullOpts)
            G_M001_IG01:
                   mov      eax, 42
                   ret
            ; Total bytes of code 6
            """;
        var results = new List<JitMethodResult>
        {
            new(
                "0x06000001",
                0x06000001,
                0x1234,
                0x5678,
                "Program.<Main>$",
                "prepared",
                "0x5678",
                null,
                0,
                0,
                [])
        };

        var filtered = JitInspectorProgram.ApplyAssemblyStatistics(
            results,
            assemblyText,
            new Dictionary<int, IReadOnlyList<JitSourcePoint>>(),
            new Dictionary<nuint, IReadOnlyList<JitNativeMethodMap>>());

        Assert.Contains("mov      eax, 42", filtered, StringComparison.Ordinal);
        Assert.DoesNotContain("call     [Program:<Main>$", filtered, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeMapSelectionUsesUniqueTokenForConstructedGenericHandleMismatch()
    {
        const int metadataToken = 0x06000002;
        var genericMap = new JitNativeMethodMap(
            0x2222,
            metadataToken,
            0x3333,
            [new JitNativeIlRange(0, 0, 3)]);
        var result = new JitMethodResult(
            "0x06000002[System.Int32]",
            metadataToken,
            0x9999,
            0x8888,
            "MappingFixture.ConstructedGeneric",
            "prepared",
            "0x8888",
            null,
            0,
            0,
            []);
        IReadOnlyDictionary<nuint, IReadOnlyList<JitNativeMethodMap>> uniqueMaps =
            new Dictionary<nuint, IReadOnlyList<JitNativeMethodMap>>
            {
                [genericMap.MethodHandle] = [genericMap]
            };

        Assert.Same(genericMap, JitInspectorProgram.SelectNativeMap(result, uniqueMaps));

        var secondInstantiation = genericMap with
        {
            MethodHandle = 0x4444,
            NativeCodeStart = 0x5555
        };
        IReadOnlyDictionary<nuint, IReadOnlyList<JitNativeMethodMap>> ambiguousMaps =
            new Dictionary<nuint, IReadOnlyList<JitNativeMethodMap>>
            {
                [genericMap.MethodHandle] = [genericMap],
                [secondInstantiation.MethodHandle] = [secondInstantiation]
            };

        Assert.Null(JitInspectorProgram.SelectNativeMap(result, ambiguousMaps));
    }

    [Fact]
    public void NativeMapProducesRealSequencePointRangesAndStripsMappingColumns()
    {
        const string section =
            """
            ; Assembly listing for method MappingFixture:MultipleSequencePoints(int):int (FullOpts)
            ; Emitting BLENDED_CODE for generic X64 + VEX + EVEX on Unix
            ; FullOpts code
            ; optimized code
            ; rsp based frame
            ; partially interruptible
            ; No PGO data

            G_M000_IG01:                ;; offset=0x0000

            G_M000_IG02:                ;; offset=0x0000
                   8D4701               lea      eax, [rdi+0x01]
                   8D48FD               lea      ecx, [rax-0x03]
                   8D1400               lea      edx, [rax+rax]
                   83F80A               cmp      eax, 10
                   8BC2                 mov      eax, edx
                   0F4EC1               cmovle   eax, ecx

            G_M000_IG03:                ;; offset=0x0011
                   C3                   ret

            ; Total bytes of code 18
            """;
        var sourcePoints = new JitSourcePoint[]
        {
            new(0, "Program.cs", new JitSourceTextRange(2, 8, 2, 30)),
            new(9, "Program.cs", new JitSourceTextRange(4, 8, 7, 23)),
            new(19, "Program.cs", new JitSourceTextRange(8, 8, 8, 21))
        };
        var nativeMap = new JitNativeMethodMap(
            0x1234,
            0x06000001,
            0x5678,
            [
                new JitNativeIlRange(-2, 0, 0),
                new JitNativeIlRange(0, 0, 3),
                new JitNativeIlRange(9, 3, 17),
                new JitNativeIlRange(19, 17, 17),
                new JitNativeIlRange(-3, 17, 18)
            ]);

        var mapped = JitSourceMapping.MapNativeSection(section, sourcePoints, nativeMap);

        Assert.Equal(2, mapped.LinkedRanges.Count);
        Assert.All(mapped.LinkedRanges, static range => Assert.Equal("sequence-point", range.Precision));
        Assert.Equal(11, mapped.LinkedRanges[0].OutputRange.StartLine);
        Assert.Equal(12, mapped.LinkedRanges[1].OutputRange.StartLine);
        Assert.Equal(16, mapped.LinkedRanges[1].OutputRange.EndLine);
        Assert.Equal(
            new JitEvidenceRange(0, 0, 3, "Program.cs", 3, 9, 3, 31),
            mapped.LinkedRanges[0].EvidenceRange);
        Assert.Equal(
            new JitEvidenceRange(9, 3, 17, "Program.cs", 5, 9, 8, 24),
            mapped.LinkedRanges[1].EvidenceRange);
        Assert.DoesNotContain("8D4701", mapped.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("offset=", mapped.Text, StringComparison.Ordinal);
        Assert.Contains("       lea      eax, [rdi+0x01]", mapped.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeMappingHandlesLargeBoundedDocumentsWithoutQuadraticScanning()
    {
        const int count = 10_000;
        var section = new StringBuilder(
            "; Assembly listing for method MappingFixture:Large():int (FullOpts)\n" +
            "G_M000_IG01: ;; offset=0x0000\n");
        for (var index = 0; index < count; index++)
            section.AppendLine("       c3                   nop");
        var nativeRanges = Enumerable.Range(0, count)
            .Select(static index => new JitNativeIlRange(index, (uint)index, (uint)index + 1))
            .ToArray();
        var map = new JitNativeMethodMap(0x1234, 0x06000001, 0x5678, nativeRanges);
        JitSourcePoint[] points =
        [
            new(0, "Program.cs", new JitSourceTextRange(0, 0, 0, 10))
        ];

        var started = Stopwatch.GetTimestamp();
        var mapped = JitSourceMapping.MapNativeSection(section.ToString(), points, map);
        var elapsed = Stopwatch.GetElapsedTime(started);

        Assert.Single(mapped.LinkedRanges);
        Assert.DoesNotContain("       c3", mapped.Text, StringComparison.Ordinal);
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"Large native mapping took {elapsed}.");
    }

    [Fact]
    public void SourceMappingUsesSiblingPortablePdbAndRootInlineOffsets()
    {
        var inspectorPath = Path.Combine(AppContext.BaseDirectory, "SharpLabNext.JitInspector.dll");
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "SharpLabNext.RunnerFixture.dll");
        var inspectorAssembly = Assembly.LoadFrom(inspectorPath);
        var fixtureAssembly = Assembly.LoadFrom(fixturePath);
        var genericType = fixtureAssembly.GetType("GenericFixture");
        Assert.NotNull(genericType);
        var identity = genericType.GetMethod("Identity", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(identity);
        var mappingType = inspectorAssembly.GetType("JitSourceMapping");
        Assert.NotNull(mappingType);
        var mapSection = mappingType.GetMethod(
            "MapSection",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            [typeof(string), typeof(int), typeof(string)],
            modifiers: null);
        Assert.NotNull(mapSection);
        const string section =
            """
            ; Assembly listing for method GenericFixture:Identity[int](int):int (FullOpts)
            G_M000_IG01:
                   ; INLRT @ 0x000[--]
                   mov      eax, ecx
                   ; INL01 @ 0x099[--] <- INLRT @ 0x000[--]
                   add      eax, 1
                   ; INLRT @ ???
                   nop
            G_M000_IG02:
                   ret
            """;

        var mapped = Assert.IsAssignableFrom<System.Collections.IEnumerable>(
            mapSection.Invoke(null, [fixturePath, identity.MetadataToken, section]));
        var ranges = mapped.Cast<object>().ToArray();

        Assert.Equal(2, ranges.Length);
        Assert.All(ranges, range =>
        {
            var sourcePath = Assert.IsType<string>(
                range.GetType().GetProperty("SourceFilePath")?.GetValue(range));
            Assert.EndsWith("Program.cs", sourcePath, StringComparison.Ordinal);
            Assert.Equal(
                "sequence-point",
                Assert.IsType<string>(range.GetType().GetProperty("Precision")?.GetValue(range)));
        });
        var firstOutput = ranges[0].GetType().GetProperty("OutputRange")?.GetValue(ranges[0]);
        var secondOutput = ranges[1].GetType().GetProperty("OutputRange")?.GetValue(ranges[1]);
        Assert.NotNull(firstOutput);
        Assert.NotNull(secondOutput);
        Assert.Equal(3, firstOutput.GetType().GetProperty("StartLine")?.GetValue(firstOutput));
        Assert.Equal(5, secondOutput.GetType().GetProperty("StartLine")?.GetValue(secondOutput));

        const string sectionWithoutMarkers =
            """
            ; Assembly listing for method GenericFixture:Identity[int](int):int (FullOpts)
            G_M000_IG01:
                   mov      eax, ecx
                   ret
            """;
        var fallback = Assert.IsAssignableFrom<System.Collections.IEnumerable>(
            mapSection.Invoke(null, [fixturePath, identity.MetadataToken, sectionWithoutMarkers]));
        var fallbackRange = Assert.Single(fallback.Cast<object>());
        Assert.Equal(
            "method",
            Assert.IsType<string>(fallbackRange.GetType().GetProperty("Precision")?.GetValue(fallbackRange)));

        var withoutPdb = Assert.IsAssignableFrom<System.Collections.IEnumerable>(
            mapSection.Invoke(null, [$"{fixturePath}.without-pdb.dll", identity.MetadataToken, section]));
        Assert.Empty(withoutPdb.Cast<object>());
    }

    private static System.Collections.IEnumerable InspectFixture(string methodFilter)
    {
        var inspectorPath = Path.Combine(AppContext.BaseDirectory, "SharpLabNext.JitInspector.dll");
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "SharpLabNext.RunnerFixture.dll");
        var inspectorAssembly = Assembly.LoadFrom(inspectorPath);
        var fixtureAssembly = Assembly.LoadFrom(fixturePath);
        var programType = inspectorAssembly.GetType("JitInspectorProgram");
        Assert.NotNull(programType);
        var inspectAssembly = programType.GetMethod(
            "InspectAssembly",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(inspectAssembly);
        return Assert.IsAssignableFrom<System.Collections.IEnumerable>(
            inspectAssembly.Invoke(null, [fixtureAssembly, methodFilter]));
    }
}
