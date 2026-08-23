#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property EnableTrimAnalyzer=false
#:property EnableAotAnalyzer=false

using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

var full = args.Contains("--full", StringComparer.Ordinal);
var security = args.Contains("--security", StringComparer.Ordinal);
var baseAddressArgument = args.FirstOrDefault(static argument =>
    !StringComparer.Ordinal.Equals(argument, "--full") &&
    !StringComparer.Ordinal.Equals(argument, "--security"));
var baseAddress = baseAddressArgument is null
    ? new Uri("http://127.0.0.1:8080", UriKind.Absolute)
    : new Uri(baseAddressArgument, UriKind.Absolute);
using var overallTimeout = new CancellationTokenSource(
    full || security ? TimeSpan.FromMinutes(15) : TimeSpan.FromMinutes(5));
using var http = new HttpClient
{
    BaseAddress = baseAddress,
    Timeout = TimeSpan.FromSeconds(90)
};
var json = CreateJsonOptions();
var lspJson = CreateLspJsonOptions();

await EnsureSuccessAsync(await http.GetAsync("/health/ready", overallTimeout.Token), "Gateway readiness");
using var catalogResponse = await http.GetAsync("/api/v1/catalog", overallTimeout.Token);
await EnsureSuccessAsync(catalogResponse, "Catalog");
using var catalog = JsonDocument.Parse(await catalogResponse.Content.ReadAsByteArrayAsync(overallTimeout.Token));
var catalogRevision = catalog.RootElement.GetProperty("Revision").GetString()
    ?? throw new InvalidOperationException("Catalog revision is missing.");

var languages = new[]
{
    new LanguageCase(
        "csharp",
        "roslyn-stable",
        "Program.cs",
        """
        using System;

        public static class Program
        {
            public static void Main() => Console.WriteLine("compose-csharp");
            public static int Add(int left, int right) => left + right;
        }
        """,
        "compose-csharp",
        SupportsAst: true),
    new LanguageCase(
        "visual-basic",
        "roslyn-stable",
        "Program.vb",
        """
        Imports System

        Public Module Program
            Public Sub Main()
                Console.WriteLine("compose-vb")
            End Sub

            Public Function Add(left As Integer, right As Integer) As Integer
                Return left + right
            End Function
        End Module
        """,
        "compose-vb",
        SupportsAst: true),
    new LanguageCase(
        "fsharp",
        "fsharp-stable",
        "Program.fs",
        """
        open System

        let add left right = left + right

        [<EntryPoint>]
        let main _ =
            printfn "compose-fsharp"
            0
        """,
        "compose-fsharp",
        SupportsAst: true),
    new LanguageCase(
        "gsharp",
        "gsharp-stable",
        "Program.gs",
        """
        package SharpLab

        import System

        Console.WriteLine("compose-gsharp")
        """,
        "compose-gsharp",
        SupportsAst: false,
        LanguageVersion: "0.3.33"),
    new LanguageCase(
        "php",
        "peachpie-stable",
        "index.php",
        """
        <?php

        function square(int $value): int
        {
            return $value * $value;
        }

        echo "compose-php", PHP_EOL;
        """,
        "compose-php",
        SupportsAst: false,
        LanguageVersion: "8.5"),
    new LanguageCase(
        "il",
        "mobius-ilasm-stable",
        "Program.il",
        """
        .assembly SharpLabNext.ComposeSmoke {}
        .module SharpLabNext.ComposeSmoke.dll
        .class public auto ansi Program extends [System.Runtime]System.Object
        {
          .method public hidebysig static void Main() cil managed
          {
            .entrypoint
            .maxstack 1
            ldstr "compose-il"
            call void [System.Console]System.Console::WriteLine(string)
            ret
          }

          .method public hidebysig static int32 Add(int32 left, int32 right) cil managed
          {
            .maxstack 2
            ldarg.0
            ldarg.1
            add
            ret
          }
        }
        """,
        "compose-il",
        SupportsAst: false),
    new LanguageCase(
        "minilang",
        "minilang-stable",
        "Program.mini",
        "print \"compose-minilang\"\n",
        "compose-minilang",
        SupportsAst: false)
};

var runtimes = new[]
{
    new RuntimeCase("dotnet-10-linux-x64", "10.")
};

var gsharpStable = languages.Single(static language => language.Id == "gsharp");
var gsharpLegacy = gsharpStable with
{
    ToolchainId = "gsharp-legacy-0.3.8",
    LanguageVersion = "0.3.8",
    Source = """
        package SharpLab

        import System

        Console.WriteLine("compose-gsharp-legacy")
        """,
    ExpectedOutput = "compose-gsharp-legacy"
};

var failures = new List<string>();
var passed = 0;

foreach (var language in languages)
{
    await CheckAsync($"{language.Id} compile", async () =>
    {
        var result = await ExecutePipelineAsync(language, "compile-check", null);
        Require(ResultType(result) == "compile-check", "Compile Check returned the wrong result type.");
        Require(result.GetProperty("CompilationSucceeded").GetBoolean(), "Compilation did not succeed.");
    });

    if (language.SupportsAst)
    {
        await CheckAsync($"{language.Id} AST", async () =>
        {
            var result = await ExecutePipelineAsync(language, "ast", null);
            Require(ResultType(result) == "ast", "AST returned the wrong result type.");
            var document = result.GetProperty("Document");
            Require(document.GetProperty("LanguageId").GetString() == language.Id, "AST language identity is wrong.");
            Require(document.GetProperty("Root").GetProperty("Kind").GetString()?.Length > 0, "AST root is empty.");
        });
    }

    await CheckAsync($"{language.Id} IL", async () =>
    {
        var execution = await ExecutePipelineDetailedAsync(language, "il", null);
        var result = execution.Result;
        Require(ResultType(result) == "artifact-render", "IL returned the wrong result type.");
        var text = await ReadResultContentAsync(execution);
        Require(text.Contains(".method", StringComparison.OrdinalIgnoreCase), "IL output contains no method.");
    });
}

foreach (var profile in new[] { gsharpStable, gsharpLegacy })
{
    await CheckAsync($"{profile.ToolchainId} auto top-level compile identity", async () =>
    {
        var execution = await ExecutePipelineDetailedAsync(
            profile,
            "compile-check",
            null,
            buildOutputKind: "auto");
        var result = execution.Result;
        Require(ResultType(result) == "compile-check", "G# Compile Check returned the wrong result type.");
        Require(result.GetProperty("CompilationSucceeded").GetBoolean(), "G# top-level source did not compile in auto mode.");
        var identity = result.GetProperty("Identity");
        Require(identity.GetProperty("ToolchainId").GetString() == profile.ToolchainId, "G# used the wrong toolchain profile.");
        Require(identity.GetProperty("CompilerVersion").GetString() == profile.LanguageVersion, "G# used the wrong compiler version.");
    });

    await CheckAsync($"{profile.ToolchainId} auto top-level decompiled C#", async () =>
    {
        var execution = await ExecutePipelineDetailedAsync(
            profile,
            "decompiled-csharp",
            null,
            buildOutputKind: "auto");
        Require(ResultType(execution.Result) == "artifact-render", "G# decompile returned the wrong result type.");
        var text = await ReadResultContentAsync(execution);
        Require(text.Length > 40, "G# decompiled C# output is empty.");
    });

    await CheckAsync($"{profile.ToolchainId} Gateway LSP WebSocket", () =>
        CheckGatewayLspAsync(profile));
}

var ilLibrary = languages.Single(static language => language.Id == "il") with
{
    Source = """
        .assembly SharpLabNext.LibrarySmoke {}
        .class public auto ansi Program extends [System.Runtime]System.Object
        {
          .method public hidebysig static void Method(string arg) cil managed
          {
            .maxstack 1
            ldarg.0
            brfalse.s done
            ldstr "not null"
            call void [System.Console]System.Console::WriteLine(string)
          done:
            ret
          }
        }
        """
};
await CheckAsync("il library decompiled C# without entry point", async () =>
{
    var execution = await ExecutePipelineDetailedAsync(
        ilLibrary,
        "decompiled-csharp",
        null,
        buildOutputKind: "library");
    Require(ResultType(execution.Result) == "artifact-render", "IL library decompile returned the wrong result type.");
    var text = await ReadResultContentAsync(execution);
    Require(text.Contains("Method", StringComparison.Ordinal), "IL library decompile lost the user method.");
});

var jsilSource = languages[0] with
{
    Source = """
        public static class Program
        {
            private static int Add(int left, int right) => left + right;

            public static void Main() => System.Console.WriteLine(Add(20, 22));
        }
        """
};
await CheckAsync("csharp classic JavaScript via JSIL", async () =>
{
    var execution = await ExecutePipelineDetailedAsync(jsilSource, "javascript", null);
    Require(ResultType(execution.Result) == "artifact-render", "JSIL returned the wrong result type.");
    Require(
        execution.Result.GetProperty("Outcome").GetString() == "succeeded",
        $"JSIL did not translate the Roslyn artifact: {execution.Result.GetRawText()}");
    var text = await ReadResultContentAsync(execution);
    Require(
        text.Contains("'use strict';", StringComparison.Ordinal),
        "JSIL output has no classic strict-mode prologue.");
    Require(
        text.Contains("var $asm00 = JSIL.DeclareAssembly", StringComparison.Ordinal),
        "JSIL output has no classic global entry assembly binding.");
    Require(
        text.Contains("Program_Add", StringComparison.Ordinal),
        "JSIL output has no generated method body.");
    Require(
        !text.Contains("export default", StringComparison.Ordinal),
        "Classic JSIL output unexpectedly contains an ESM default export.");
    Require(
        !text.Contains("export function run", StringComparison.Ordinal),
        "Classic JSIL output unexpectedly contains an ESM run export.");
});

var runtimeLanguages = full ? languages : new[] { languages[0], languages[^1] };
foreach (var language in runtimeLanguages)
{
    foreach (var runtime in runtimes)
    {
        await CheckAsync($"{language.Id} Run {runtime.Id}", async () =>
        {
            var execution = await ExecutePipelineDetailedAsync(language, "run", runtime.Id);
            var result = execution.Result;
            Require(ResultType(result) == "run", "Run returned the wrong result type.");
            Require(result.GetProperty("Status").GetString() == "completed", "Run did not complete.");
            Require(result.GetProperty("ExitCode").GetInt32() == 0, "Run returned a non-zero exit code.");
            var output = DecodeOutput(execution.Events, "stdout");
            Require(output.Contains(language.ExpectedOutput, StringComparison.Ordinal), "Run stdout is incorrect.");
            Require(
                result.GetProperty("Identity").GetProperty("RuntimeVersion").GetString()?.StartsWith(
                    runtime.VersionPrefix,
                    StringComparison.Ordinal) == true,
                "Run used the wrong runtime.");
        });

        await CheckAsync($"{language.Id} JIT {runtime.Id}", async () =>
        {
            var execution = await ExecutePipelineDetailedAsync(language, "jit-asm", runtime.Id);
            var result = execution.Result;
            Require(ResultType(result) == "jit", "JIT returned the wrong result type.");
            Require(
                result.GetProperty("Status").GetString() == "completed",
                $"JIT did not complete: {result.GetRawText()}");
            var methods = result.GetProperty("Methods").EnumerateArray().ToArray();
            Require(methods.Length > 0, "JIT returned no methods.");
            Require(
                methods.Any(static method =>
                    method.GetProperty("NativeCodeSize").GetInt32() > 0 &&
                    method.GetProperty("InstructionCount").GetInt32() > 0),
                "JIT returned no method with native code statistics.");
            var text = await ReadResultContentAsync(execution);
            Require(text.Length > 40, "JIT assembly output is empty.");
            Require(
                text.Contains("Assembly listing for method", StringComparison.Ordinal),
                "JIT output contains no CoreCLR assembly listing.");
            Require(
                result.GetProperty("Identity").GetProperty("RuntimeVersion").GetString()?.StartsWith(
                    runtime.VersionPrefix,
                    StringComparison.Ordinal) == true,
                "JIT used the wrong runtime.");
        });
    }
}

var net5OnNewerRuntime = languages[0] with
{
    ReferenceSetId = "net5-ref",
    Source = """
        using System;

        public static class Program
        {
            public static void Main() => Console.WriteLine("compose-net5-roll-forward");
        }
        """,
    ExpectedOutput = "compose-net5-roll-forward"
};
await CheckAsync("net5 defaults to its matching runtime", async () =>
{
    var execution = await ExecutePipelineDetailedAsync(net5OnNewerRuntime, "run", null);
    Require(ResultType(execution.Result) == "run", "net5 default Run returned the wrong result type.");
    Require(execution.Result.GetProperty("Status").GetString() == "completed", "net5 default Run did not complete.");
    Require(
        DecodeOutput(execution.Events, "stdout").Contains(net5OnNewerRuntime.ExpectedOutput, StringComparison.Ordinal),
        "net5 default Run stdout is incorrect.");
    Require(
        execution.Result.GetProperty("Identity").GetProperty("RuntimeVersion").GetString()?.StartsWith(
            "5.",
            StringComparison.Ordinal) == true,
        "net5 default Run did not select the matching .NET 5 runtime.");
});
await CheckAsync("net5 can run on explicitly selected net10", async () =>
{
    var execution = await ExecutePipelineDetailedAsync(
        net5OnNewerRuntime,
        "run",
        "dotnet-10-linux-x64");
    Require(ResultType(execution.Result) == "run", "net5-on-net10 Run returned the wrong result type.");
    Require(execution.Result.GetProperty("Status").GetString() == "completed", "net5-on-net10 Run did not complete.");
    Require(
        DecodeOutput(execution.Events, "stdout").Contains(net5OnNewerRuntime.ExpectedOutput, StringComparison.Ordinal),
        "net5-on-net10 Run stdout is incorrect.");
    Require(
        execution.Result.GetProperty("Identity").GetProperty("RuntimeVersion").GetString()?.StartsWith(
            "10.",
            StringComparison.Ordinal) == true,
        "net5-on-net10 Run did not use the explicitly selected .NET 10 runtime.");
});

if (full)
{
    string? constGenericsArtifactRef = null;
    var lockedConstGenericsCompiler = ReadLockedComponent("roslyn-const-generics");
    var lockedConstGenericsRuntime = ReadLockedComponent("const-generics-linux-x64");
    var roslynMainCSharp = languages[0] with
    {
        ToolchainId = "roslyn-main",
        ReferenceSetId = "net11-preview-ref",
        LanguageVersion = "preview"
    };
    var roslynMainVisualBasic = languages[1] with
    {
        ToolchainId = "roslyn-main",
        ReferenceSetId = "net11-preview-ref"
    };
    var gsharp = gsharpStable;
    var php = languages.Single(static language => language.Id == "php");
    var cppCli = new LanguageCase(
        "cppcli",
        "msvc-cppcli-netfx48",
        "Program.cpp",
        """
        using namespace System;

        static int Add(int left, int right)
        {
            return left + right;
        }

        int main(array<String^>^)
        {
            Console::WriteLine("compose-cppcli:{0}", Add(19, 23));
            Console::Error->WriteLine("compose-cppcli-stderr");
            return 0;
        }
        """,
        "compose-cppcli:42",
        SupportsAst: false,
        ReferenceSetId: "netfx48-ref",
        LanguageVersion: "19.51.36248");
    var jsharp = new LanguageCase(
        "jsharp",
        "vjc-jsharp20",
        "Program.jsl",
        """
        public class Program
        {
            public static int Add(int left, int right)
            {
                return left + right;
            }

            public static void main(String[] args)
            {
                System.Console.WriteLine("compose-jsharp:" + Add(19, 23));
                System.Console.get_Error().WriteLine("compose-jsharp-stderr");
            }
        }
        """,
        "compose-jsharp:42",
        SupportsAst: false,
        ReferenceSetId: "jsharp20-ref");
    var netFxCSharp = languages[0] with
    {
        ToolchainId = "roslyn-stable-netfx48",
        ReferenceSetId = "netfx48-managed-ref",
        ExpectedOutput = "compose-netfx-csharp",
        Source = """
            using System;

            public static class Program
            {
                public static void Main() => Console.WriteLine("compose-netfx-csharp");
                public static int Add(int left, int right) => left + right;
            }
            """
    };
    var netFxVisualBasic = languages[1] with
    {
        ToolchainId = "roslyn-stable-netfx48",
        ReferenceSetId = "netfx48-managed-ref",
        ExpectedOutput = "compose-netfx-vb",
        Source = """
            Imports System

            Public Module Program
                Public Sub Main()
                    Console.WriteLine("compose-netfx-vb")
                End Sub

                Public Function Add(left As Integer, right As Integer) As Integer
                    Return left + right
                End Function
            End Module
            """
    };

    async Task CheckManagedNetFxAsync(LanguageCase language, string displayName)
    {
        await CheckAsync($"{displayName} compile identity", async () =>
        {
            var result = await ExecutePipelineAsync(language, "compile-check", null);
            Require(ResultType(result) == "compile-check", $"{displayName} Compile Check returned the wrong result type.");
            Require(result.GetProperty("CompilationSucceeded").GetBoolean(), $"{displayName} source did not compile.");
            var identity = result.GetProperty("Identity");
            Require(identity.GetProperty("LanguageId").GetString() == language.Id, $"{displayName} used the wrong language.");
            Require(identity.GetProperty("ToolchainId").GetString() == language.ToolchainId, $"{displayName} used the wrong toolchain.");
            Require(identity.GetProperty("ReferenceSetId").GetString() == language.ReferenceSetId, $"{displayName} used the wrong reference set.");
        });

        await CheckAsync($"{displayName} decompiled C#", async () =>
        {
            var execution = await ExecutePipelineDetailedAsync(language, "decompiled-csharp", null);
            Require(ResultType(execution.Result) == "artifact-render", $"{displayName} decompile returned the wrong result type.");
            var text = await ReadResultContentAsync(execution);
            Require(text.Contains("Add", StringComparison.Ordinal), $"{displayName} decompile lost the user method.");
        });

        await CheckAsync($"{displayName} IL", async () =>
        {
            var execution = await ExecutePipelineDetailedAsync(language, "il", null);
            Require(ResultType(execution.Result) == "artifact-render", $"{displayName} IL returned the wrong result type.");
            var text = await ReadResultContentAsync(execution);
            Require(text.Contains(".method", StringComparison.OrdinalIgnoreCase), $"{displayName} IL contains no method.");
            Require(text.Contains("Add", StringComparison.Ordinal), $"{displayName} IL lost the user method.");
        });

        await CheckAsync($"{displayName} Wine Run", async () =>
        {
            var execution = await ExecutePipelineDetailedAsync(language, "run", "wine-netfx48-linux-x64");
            var result = execution.Result;
            Require(ResultType(result) == "run", $"{displayName} Wine Run returned the wrong result type.");
            Require(result.GetProperty("Status").GetString() == "completed", $"{displayName} Wine Run did not complete.");
            Require(result.GetProperty("ExitCode").GetInt32() == 0, $"{displayName} Wine Run returned a non-zero exit code.");
            Require(
                DecodeOutput(execution.Events, "stdout").Contains(language.ExpectedOutput, StringComparison.Ordinal),
                $"{displayName} Wine Run stdout is incorrect.");
            Require(
                result.GetProperty("Identity").GetProperty("RuntimeVersion").GetString() == "4.8",
                $"{displayName} did not use the Wine/.NET Framework runtime.");
        });
    }

    await CheckAsync("gsharp decompiled C#", async () =>
    {
        var execution = await ExecutePipelineDetailedAsync(gsharp, "decompiled-csharp", null);
        Require(ResultType(execution.Result) == "artifact-render", "G# decompile returned the wrong result type.");
        var text = await ReadResultContentAsync(execution);
        Require(text.Length > 40, "G# decompiled C# output is empty.");
    });

    await CheckAsync("gsharp IL Verify", async () =>
    {
        var result = await ExecutePipelineAsync(gsharp, "il-verify", null);
        RequireIlVerification(result, "G#");
    });

    await CheckAsync("gsharp legacy 0.3.8 Run .NET 10", async () =>
    {
        var execution = await ExecutePipelineDetailedAsync(
            gsharpLegacy,
            "run",
            "dotnet-10-linux-x64",
            buildOutputKind: "console");
        Require(ResultType(execution.Result) == "run", "G# legacy Run returned the wrong result type.");
        Require(execution.Result.GetProperty("Status").GetString() == "completed", "G# legacy Run did not complete.");
        Require(execution.Result.GetProperty("ExitCode").GetInt32() == 0, "G# legacy Run returned a non-zero exit code.");
        Require(
            DecodeOutput(execution.Events, "stdout").Contains(gsharpLegacy.ExpectedOutput, StringComparison.Ordinal),
            "G# legacy Run stdout is incorrect.");
    });

    await CheckAsync("php decompiled C#", async () =>
    {
        var execution = await ExecutePipelineDetailedAsync(php, "decompiled-csharp", null);
        Require(ResultType(execution.Result) == "artifact-render", "PHP decompile returned the wrong result type.");
        var text = await ReadResultContentAsync(execution);
        Require(text.Length > 40, "PHP decompiled C# output is empty.");
        Require(
            text.Contains("square", StringComparison.OrdinalIgnoreCase),
            "PHP decompiled C# does not contain the source function.");
    });

    await CheckAsync("php IL Verify", async () =>
    {
        var result = await ExecutePipelineAsync(php, "il-verify", null);
        RequireIlVerification(result, "PHP");
    });

    await CheckAsync("php filesystem native Run", async () =>
    {
        var filesystem = php with
        {
            Source = """
                <?php

                $info = stat('/tmp');
                echo is_array($info) && isset($info['mode'])
                    ? "php-native-ok\n"
                    : "php-native-failed\n";
                """,
            ExpectedOutput = "php-native-ok"
        };
        var execution = await ExecutePipelineDetailedAsync(filesystem, "run", runtimes[0].Id);
        var result = execution.Result;
        Require(ResultType(result) == "run", "PHP filesystem Run returned the wrong result type.");
        Require(result.GetProperty("Status").GetString() == "completed", "PHP filesystem Run did not complete.");
        Require(result.GetProperty("ExitCode").GetInt32() == 0, "PHP filesystem Run returned a non-zero exit code.");
        Require(
            DecodeOutput(execution.Events, "stdout").Contains(filesystem.ExpectedOutput, StringComparison.Ordinal),
            "PHP filesystem Run did not load the Mono.Unix native dependency.");
    });

    await CheckAsync("cppcli compile identity", async () =>
    {
        var result = await ExecutePipelineAsync(cppCli, "compile-check", null);
        Require(ResultType(result) == "compile-check", "C++/CLI Compile Check returned the wrong result type.");
        Require(result.GetProperty("CompilationSucceeded").GetBoolean(), "C++/CLI source did not compile.");
        var identity = result.GetProperty("Identity");
        Require(identity.GetProperty("LanguageId").GetString() == cppCli.Id, "C++/CLI Compile Check used the wrong language.");
        Require(identity.GetProperty("ToolchainId").GetString() == cppCli.ToolchainId, "C++/CLI Compile Check used the wrong toolchain.");
        Require(identity.GetProperty("CompilerVersion").GetString() == cppCli.LanguageVersion, "C++/CLI Compile Check used the wrong MSVC version.");
        Require(identity.GetProperty("ReferenceSetId").GetString() == cppCli.ReferenceSetId, "C++/CLI Compile Check used the wrong reference set.");
    });

    await CheckAsync("cppcli decompiled C#", async () =>
    {
        var execution = await ExecutePipelineDetailedAsync(cppCli, "decompiled-csharp", null);
        Require(ResultType(execution.Result) == "artifact-render", "C++/CLI decompile returned the wrong result type.");
        var text = await ReadResultContentAsync(execution);
        Require(text.Contains("main", StringComparison.Ordinal), "C++/CLI decompile lost the user entry point.");
        Require(text.Contains("compose-cppcli:", StringComparison.Ordinal), "C++/CLI decompile lost the user output literal.");
    });

    await CheckAsync("cppcli IL", async () =>
    {
        var execution = await ExecutePipelineDetailedAsync(cppCli, "il", null);
        Require(ResultType(execution.Result) == "artifact-render", "C++/CLI IL returned the wrong result type.");
        var text = await ReadResultContentAsync(execution);
        Require(text.Contains(".method", StringComparison.OrdinalIgnoreCase), "C++/CLI IL contains no method.");
        Require(text.Contains("main", StringComparison.Ordinal), "C++/CLI IL lost the user entry point.");
        Require(text.Contains("compose-cppcli:", StringComparison.Ordinal), "C++/CLI IL lost the user output literal.");
    });

    await CheckAsync("cppcli Wine Run", async () =>
    {
        var execution = await ExecutePipelineDetailedAsync(
            cppCli,
            "run",
            "wine-netfx48-linux-x64");
        var result = execution.Result;
        Require(ResultType(result) == "run", "C++/CLI Wine Run returned the wrong result type.");
        Require(result.GetProperty("Status").GetString() == "completed", "C++/CLI Wine Run did not complete.");
        Require(result.GetProperty("ExitCode").GetInt32() == 0, "C++/CLI Wine Run returned a non-zero exit code.");
        Require(
            DecodeOutput(execution.Events, "stdout").Contains(cppCli.ExpectedOutput, StringComparison.Ordinal),
            "C++/CLI Wine Run stdout is incorrect.");
        Require(
            DecodeOutput(execution.Events, "stderr").Contains("compose-cppcli-stderr", StringComparison.Ordinal),
            "C++/CLI Wine Run stderr is incorrect.");
        var runtimeIdentity = result.GetProperty("Identity");
        Require(
            runtimeIdentity.GetProperty("RuntimeVersion").GetString() == "4.8",
            "C++/CLI Run did not use the Wine/.NET Framework runtime.");
        Require(
            runtimeIdentity.GetProperty("RuntimeCommit").GetString() == "not-applicable",
            "C++/CLI Wine Run reported a CoreCLR runtime commit.");
    });

    foreach (var unsupported in new[]
    {
        (OutputId: "il-verify", RuntimeId: (string?)null, Field: "Output"),
        (OutputId: "jit-asm", RuntimeId: "wine-netfx48-linux-x64", Field: "Runtime"),
        (OutputId: "execution-flow", RuntimeId: "wine-netfx48-linux-x64", Field: "Runtime"),
        (OutputId: "run-il", RuntimeId: (string?)null, Field: "Output")
    })
    {
        await CheckAsync($"cppcli rejects {unsupported.OutputId}", async () =>
        {
            using var response = await PostResolutionAsync(cppCli, unsupported.OutputId, unsupported.RuntimeId);
            Require(
                response.StatusCode == System.Net.HttpStatusCode.BadRequest,
                $"C++/CLI unexpectedly resolved unsupported output '{unsupported.OutputId}'.");
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsByteArrayAsync(overallTimeout.Token));
            var error = document.RootElement;
            Require(
                error.GetProperty("Error").GetString() == "unsupported-capability",
                $"C++/CLI '{unsupported.OutputId}' failed for the wrong reason.");
            Require(
                error.GetProperty("Field").GetString() == unsupported.Field,
                $"C++/CLI '{unsupported.OutputId}' rejected the wrong selection field.");
        });
    }

    await CheckAsync("jsharp x64 compile identity", async () =>
    {
        var result = await ExecutePipelineAsync(jsharp, "compile-check", null);
        Require(ResultType(result) == "compile-check", "J# Compile Check returned the wrong result type.");
        Require(result.GetProperty("CompilationSucceeded").GetBoolean(), "J# x64 source did not compile.");
        var identity = result.GetProperty("Identity");
        Require(identity.GetProperty("LanguageId").GetString() == jsharp.Id, "J# Compile Check used the wrong language.");
        Require(identity.GetProperty("ToolchainId").GetString() == jsharp.ToolchainId, "J# Compile Check used the wrong toolchain.");
        Require(identity.GetProperty("CompilerVersion").GetString() == "2.0.50727.937", "J# Compile Check used the wrong x64 compiler version.");
        Require(identity.GetProperty("ReferenceSetId").GetString() == jsharp.ReferenceSetId, "J# Compile Check used the wrong reference set.");
    });

    await CheckAsync("jsharp x64 decompiled C#", async () =>
    {
        var execution = await ExecutePipelineDetailedAsync(jsharp, "decompiled-csharp", null);
        Require(ResultType(execution.Result) == "artifact-render", "J# decompile returned the wrong result type.");
        var text = await ReadResultContentAsync(execution);
        Require(text.Contains("Add", StringComparison.Ordinal), "J# decompile lost the user method.");
        Require(text.Contains("compose-jsharp:", StringComparison.Ordinal), "J# decompile lost the user output literal.");
    });

    await CheckAsync("jsharp x64 IL", async () =>
    {
        var execution = await ExecutePipelineDetailedAsync(jsharp, "il", null);
        Require(ResultType(execution.Result) == "artifact-render", "J# IL returned the wrong result type.");
        var text = await ReadResultContentAsync(execution);
        Require(text.Contains(".method", StringComparison.OrdinalIgnoreCase), "J# IL contains no method.");
        Require(text.Contains("main", StringComparison.Ordinal), "J# IL lost the managed entry point.");
        Require(text.Contains("Add", StringComparison.Ordinal), "J# IL lost the user method.");
    });

    await CheckAsync("jsharp x64 Wine Run", async () =>
    {
        var execution = await ExecutePipelineDetailedAsync(
            jsharp,
            "run",
            "wine-jsharp20-linux-x64");
        var result = execution.Result;
        Require(ResultType(result) == "run", "J# Wine Run returned the wrong result type.");
        Require(result.GetProperty("Status").GetString() == "completed", "J# Wine Run did not complete.");
        Require(result.GetProperty("ExitCode").GetInt32() == 0, "J# Wine Run returned a non-zero exit code.");
        Require(
            DecodeOutput(execution.Events, "stdout").Contains(jsharp.ExpectedOutput, StringComparison.Ordinal),
            "J# Wine Run stdout is incorrect.");
        Require(
            DecodeOutput(execution.Events, "stderr").Contains("compose-jsharp-stderr", StringComparison.Ordinal),
            "J# Wine Run stderr is incorrect.");
        var runtimeIdentity = result.GetProperty("Identity");
        Require(
            runtimeIdentity.GetProperty("RuntimeVersion").GetString() == "wine-9.0+clr2+jsharp-2.0.50727.937",
            "J# Run did not use the dedicated x64 CLR2/J# runtime.");
        Require(
            runtimeIdentity.GetProperty("RuntimeCommit").GetString() == "not-applicable",
            "J# Wine Run reported a CoreCLR runtime commit.");
    });

    foreach (var unsupported in new[]
    {
        (OutputId: "ast", RuntimeId: (string?)null, Field: "Output"),
        (OutputId: "il-verify", RuntimeId: (string?)null, Field: "Output"),
        (OutputId: "jit-asm", RuntimeId: "wine-jsharp20-linux-x64", Field: "Runtime"),
        (OutputId: "execution-flow", RuntimeId: "wine-jsharp20-linux-x64", Field: "Runtime"),
        (OutputId: "run-il", RuntimeId: (string?)null, Field: "Output"),
        (OutputId: "run", RuntimeId: "wine-netfx48-linux-x64", Field: "Runtime"),
        (OutputId: "run", RuntimeId: "dotnet-10-linux-x64", Field: "Runtime")
    })
    {
        await CheckAsync($"jsharp rejects {unsupported.OutputId} via {unsupported.RuntimeId ?? "no runtime"}", async () =>
        {
            using var response = await PostResolutionAsync(jsharp, unsupported.OutputId, unsupported.RuntimeId);
            Require(
                response.StatusCode == System.Net.HttpStatusCode.BadRequest,
                $"J# unexpectedly resolved unsupported selection '{unsupported.OutputId}/{unsupported.RuntimeId}'.");
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsByteArrayAsync(overallTimeout.Token));
            var error = document.RootElement;
            Require(
                error.GetProperty("Error").GetString() == "unsupported-capability",
                $"J# '{unsupported.OutputId}' failed for the wrong reason.");
            Require(
                error.GetProperty("Field").GetString() == unsupported.Field,
                $"J# '{unsupported.OutputId}' rejected the wrong selection field.");
        });
    }

    await CheckManagedNetFxAsync(netFxCSharp, "C# net48");
    await CheckManagedNetFxAsync(netFxVisualBasic, "VB net48");

    foreach (var unsupported in new[] { "jit-asm", "execution-flow", "run-il" })
    {
        await CheckAsync($"C# net48 rejects {unsupported}", async () =>
        {
            using var response = await PostResolutionAsync(
                netFxCSharp,
                unsupported,
                unsupported is "jit-asm" or "execution-flow" ? "wine-netfx48-linux-x64" : null);
            Require(
                response.StatusCode == System.Net.HttpStatusCode.BadRequest,
                $"C# net48 unexpectedly resolved unsupported output '{unsupported}'.");
        });
    }

    await CheckAsync("roslyn-main C# compile identity", async () =>
    {
        var result = await ExecutePipelineAsync(roslynMainCSharp, "compile-check", null);
        Require(ResultType(result) == "compile-check", "Roslyn main Compile Check returned the wrong result type.");
        Require(result.GetProperty("CompilationSucceeded").GetBoolean(), "Roslyn main C# did not compile.");
        Require(
            result.GetProperty("Identity").GetProperty("ToolchainId").GetString() == "roslyn-main",
            "Compile Check did not use Roslyn main.");
        Require(
            result.GetProperty("Identity").GetProperty("ReferenceSetId").GetString() == "net11-preview-ref",
            "Roslyn main did not use the .NET 11 reference set.");
    });

    await CheckAsync("roslyn-main VB compile", async () =>
    {
        var result = await ExecutePipelineAsync(roslynMainVisualBasic, "compile-check", null);
        Require(ResultType(result) == "compile-check", "Roslyn main VB Compile Check returned the wrong result type.");
        Require(result.GetProperty("CompilationSucceeded").GetBoolean(), "Roslyn main VB did not compile.");
    });

    await CheckAsync("roslyn-main C# Run dotnet-11-preview-linux-x64", async () =>
    {
        var execution = await ExecutePipelineDetailedAsync(
            roslynMainCSharp,
            "run",
            "dotnet-11-preview-linux-x64");
        Require(ResultType(execution.Result) == "run", "Roslyn main Run returned the wrong result type.");
        Require(execution.Result.GetProperty("Status").GetString() == "completed", "Roslyn main Run did not complete.");
        Require(
            DecodeOutput(execution.Events, "stdout").Contains("compose-csharp", StringComparison.Ordinal),
            "Roslyn main Run stdout is incorrect.");
    });

    await CheckAsync("roslyn-main C# JIT dotnet-11-preview-linux-x64", async () =>
    {
        var execution = await ExecutePipelineDetailedAsync(
            roslynMainCSharp,
            "jit-asm",
            "dotnet-11-preview-linux-x64");
        Require(ResultType(execution.Result) == "jit", "Roslyn main JIT returned the wrong result type.");
        Require(
            execution.Result.GetProperty("Methods").GetArrayLength() > 0,
            $"Roslyn main JIT returned no methods: {execution.Result.GetRawText()}");
        Require((await ReadResultContentAsync(execution)).Length > 40, "Roslyn main JIT output is empty.");
    });

    var constGenerics = new LanguageCase(
        "csharp",
        "roslyn-const-generics",
        "Program.cs",
        """
        using System;

        public static class FixedValue<int Value>
        {
            public static int GetValue() => Value;
        }

        public static class Program
        {
            public static void Main() =>
                Console.WriteLine(FixedValue<42>.GetValue());
        }
        """,
        "42",
        SupportsAst: true,
        ReferenceSetId: "const-generics-ref",
        LanguageVersion: "preview");

    await CheckAsync("const-generics compile identity", async () =>
    {
        var result = await ExecutePipelineAsync(constGenerics, "compile-check", null);
        Require(ResultType(result) == "compile-check", "ConstGenerics Compile Check returned the wrong result type.");
        Require(result.GetProperty("CompilationSucceeded").GetBoolean(), "ConstGenerics source did not compile.");
        var identity = result.GetProperty("Identity");
        Require(identity.GetProperty("ToolchainId").GetString() == "roslyn-const-generics", "Compile Check used the wrong compiler.");
        Require(
            identity.GetProperty("CompilerCommit").GetString() == lockedConstGenericsCompiler.Commit,
            "Compile Check used the wrong ConstGenerics Roslyn commit.");
        Require(identity.GetProperty("ReferenceSetId").GetString() == "const-generics-ref", "Compile Check used the wrong reference set.");
    });

    await CheckAsync("const-generics AST", async () =>
    {
        var result = await ExecutePipelineAsync(constGenerics, "ast", null);
        Require(ResultType(result) == "ast", "ConstGenerics AST returned the wrong result type.");
        Require(
            result.GetRawText().Contains("LiteralTypeArgument", StringComparison.Ordinal),
            "ConstGenerics AST did not expose LiteralTypeArgument.");
    });

    await CheckAsync("const-generics IL", async () =>
    {
        var execution = await ExecutePipelineDetailedAsync(constGenerics, "il", null);
        constGenericsArtifactRef = execution.BuildArtifactRef;
        Require(constGenericsArtifactRef is not null, "ConstGenerics IL pipeline returned no build artifact reference.");
        Require(ResultType(execution.Result) == "artifact-render", "ConstGenerics IL returned the wrong result type.");
        var text = await ReadResultContentAsync(execution);
        Require(text.Contains("FixedValue", StringComparison.Ordinal), "ConstGenerics IL lost the generic type.");
        Require(text.Contains("42", StringComparison.Ordinal), "ConstGenerics IL lost the constant type argument.");
    });

    await CheckAsync("const-generics decompiled C#", async () =>
    {
        var execution = await ExecutePipelineDetailedAsync(constGenerics, "decompiled-csharp", null);
        Require(ResultType(execution.Result) == "artifact-render", "ConstGenerics decompile returned the wrong result type.");
        var text = await ReadResultContentAsync(execution);
        Require(text.Contains("FixedValue", StringComparison.Ordinal), "ConstGenerics decompile lost the generic type.");
        Require(text.Contains("42", StringComparison.Ordinal), "ConstGenerics decompile lost the constant type argument.");
    });

    await CheckAsync("const-generics IL Verify", async () =>
    {
        var result = await ExecutePipelineAsync(constGenerics, "il-verify", null);
        RequireIlVerification(result, "ConstGenerics");
    });

    await CheckAsync("const-generics Run", async () =>
    {
        var execution = await ExecutePipelineDetailedAsync(
            constGenerics,
            "run",
            "const-generics-linux-x64");
        Require(ResultType(execution.Result) == "run", "ConstGenerics Run returned the wrong result type.");
        Require(execution.Result.GetProperty("Status").GetString() == "completed", "ConstGenerics Run did not complete.");
        Require(execution.Result.GetProperty("ExitCode").GetInt32() == 0, "ConstGenerics Run returned a non-zero exit code.");
        Require(DecodeOutput(execution.Events, "stdout").Trim() == "42", "ConstGenerics Run stdout is incorrect.");
        Require(
            execution.Result.GetProperty("Identity").GetProperty("RuntimeVersion").GetString() ==
                lockedConstGenericsRuntime.ResolvedVersion,
            "ConstGenerics Run used the wrong runtime.");
    });

    await CheckAsync("const-generics JIT", async () =>
    {
        var execution = await ExecutePipelineDetailedAsync(
            constGenerics,
            "jit-asm",
            "const-generics-linux-x64");
        Require(ResultType(execution.Result) == "jit", "ConstGenerics JIT returned the wrong result type.");
        Require(execution.Result.GetProperty("Status").GetString() == "completed", "ConstGenerics JIT did not complete.");
        Require(execution.Result.GetProperty("Methods").GetArrayLength() > 0, "ConstGenerics JIT returned no methods.");
        Require((await ReadResultContentAsync(execution)).Contains("Assembly listing for method", StringComparison.Ordinal), "ConstGenerics JIT output is empty.");
    });

    await CheckAsync("const-generics rejects ordinary runtime", async () =>
    {
        using var response = await PostResolutionAsync(
            constGenerics,
            "run",
            "dotnet-10-linux-x64");
        Require(
            response.StatusCode == System.Net.HttpStatusCode.BadRequest,
            "ConstGenerics artifact was routed to an ordinary .NET runtime.");
    });

    await CheckAsync("ordinary artifact worker rejects const-generics", async () =>
    {
        var artifactRef = constGenericsArtifactRef
            ?? throw new InvalidOperationException("No ConstGenerics artifact is available for the worker boundary test.");
        var ordinaryResolution = await ResolveAsync(languages[0], "il", null);
        var pipelineId = ordinaryResolution.GetProperty("PipelineResolutionId").GetString()
            ?? throw new InvalidOperationException("Ordinary IL resolution returned no pipeline ID.");
        var renderStage = ordinaryResolution.GetProperty("PipelinePlan").GetProperty("Stages")
            .EnumerateArray()
            .Single(static stage => stage.GetProperty("Kind").GetString() == "render");
        var identity = Identity("boundary-render");
        var rejection = await StartAndWaitAsync("/api/v1/artifact-renders", new
        {
            identity.requestId,
            identity.idempotencyKey,
            pipelineResolutionId = pipelineId,
            artifactRef,
            processorId = renderStage.GetProperty("ProviderId").GetString(),
            outputId = renderStage.GetProperty("Id").GetString(),
            options = new
            {
                includeSequencePoints = false,
                includeCompilerGeneratedMembers = true,
                maxCharacters = 1_000_000
            },
            deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(45)
        });
        Require(ResultType(rejection.Result) == "artifact-render", "Ordinary artifact worker returned the wrong result type.");
        Require(
            rejection.Result.GetProperty("Outcome").GetString() == "invalid-artifact",
            $"Ordinary artifact worker accepted the ConstGenerics artifact: {rejection.Result.GetRawText()}");
    });

    await CheckAsync("minilang Generated IL", async () =>
    {
        var execution = await ExecutePipelineDetailedAsync(languages[^1], "generated-il", null);
        Require(ResultType(execution.Result) == "artifact-render", "Generated IL returned the wrong result type.");
        var text = await ReadResultContentAsync(execution);
        Require(text.Contains(".assembly", StringComparison.OrdinalIgnoreCase), "Generated IL contains no assembly.");
        Require(text.Contains("compose-minilang", StringComparison.Ordinal), "Generated IL lost the source literal.");
    });

    await CheckAsync("csharp Explain", async () =>
    {
        var result = await ExecutePipelineAsync(languages[0], "explain", null);
        Require(ResultType(result) == "explain", "Explain returned the wrong result type.");
        var document = result.GetProperty("Document");
        Require(document.GetProperty("LanguageId").GetString() == "csharp", "Explain language identity is wrong.");
        var files = document.GetProperty("Files");
        Require(files.GetArrayLength() == 1, "Explain returned the wrong file count.");
        Require(files[0].GetProperty("Nodes").GetArrayLength() > 0, "Explain returned no source nodes.");
    });

    await CheckAsync("csharp Execution Flow", async () =>
    {
        var execution = await ExecutePipelineDetailedAsync(
            languages[0],
            "execution-flow",
            "dotnet-10-linux-x64");
        Require(ResultType(execution.Result) == "run", "Execution Flow returned the wrong result type.");
        Require(execution.Result.GetProperty("Status").GetString() == "completed", "Execution Flow did not complete.");
        Require(DecodeOutput(execution.Events, "flow").Length > 0, "Execution Flow emitted no structured flow frames.");
    });

    foreach (var language in languages.Where(static language => language.Id is "fsharp" or "gsharp"))
    {
        await CheckAsync($"{language.Id} Execution Flow", async () =>
        {
            var execution = await ExecutePipelineDetailedAsync(
                language,
                "execution-flow",
                "dotnet-10-linux-x64");
            Require(ResultType(execution.Result) == "run", "Execution Flow returned the wrong result type.");
            Require(execution.Result.GetProperty("Status").GetString() == "completed", "Execution Flow did not complete.");
            var flow = DecodeOutput(execution.Events, "flow");
            Require(flow.Length > 0, "Execution Flow emitted no structured flow frames.");
            Require(
                flow.Contains(language.FileName, StringComparison.Ordinal),
                $"Execution Flow did not preserve the {language.FileName} source identity.");
        });
    }

    await CheckAsync("csharp Run IL", async () =>
    {
        var execution = await ExecutePipelineDetailedAsync(languages[0], "run-il", null);
        Require(ResultType(execution.Result) == "artifact-render", "Run IL returned the wrong result type.");
        var text = await ReadResultContentAsync(execution);
        Require(text.Contains(".method", StringComparison.OrdinalIgnoreCase), "Run IL contains no methods.");
        Require(text.Contains("SharpLab", StringComparison.Ordinal), "Run IL contains no instrumentation reference.");
    });

    await CheckAsync("Generated Source hidden without a content provider", async () =>
    {
        using var response = await PostResolutionAsync(languages[0], "generated-source", null);
        Require(
            response.StatusCode == System.Net.HttpStatusCode.BadRequest,
            "Generated Source is selectable even though no worker publishes generated-source documents.");
    });

    foreach (var language in languages)
    {
        await CheckAsync($"{language.Id} decompiled C#", async () =>
        {
            var execution = await ExecutePipelineDetailedAsync(language, "decompiled-csharp", null);
            var result = execution.Result;
            Require(ResultType(result) == "artifact-render", "Decompile returned the wrong result type.");
            var text = await ReadResultContentAsync(execution);
            Require(text.Length > 40, "Decompiled C# output is empty.");
        });

        await CheckAsync($"{language.Id} IL Verify", async () =>
        {
            var result = await ExecutePipelineAsync(language, "il-verify", null);
            RequireIlVerification(result, language.Id);
        });
    }

    await CheckAsync("concurrent compiler identity isolation", async () =>
    {
        var stopwatch = Stopwatch.StartNew();
        var work = Enumerable.Range(0, 10).Select(async index =>
        {
            var language = languages[index % languages.Length];
            var result = await ExecutePipelineAsync(language, "compile-check", null);
            Require(ResultType(result) == "compile-check", "Concurrent compile returned the wrong result type.");
            Require(result.GetProperty("CompilationSucceeded").GetBoolean(), "Concurrent compilation failed.");
            var identity = result.GetProperty("Identity");
            Require(identity.GetProperty("LanguageId").GetString() == language.Id, "Concurrent compile language identity crossed requests.");
            Require(identity.GetProperty("ToolchainId").GetString() == language.ToolchainId, "Concurrent compile toolchain identity crossed requests.");
            Require(identity.GetProperty("ReferenceSetId").GetString() == language.ReferenceSetId, "Concurrent compile reference identity crossed requests.");
        }).ToArray();
        await Task.WhenAll(work);
        Require(stopwatch.Elapsed < TimeSpan.FromMinutes(2), "Concurrent compile gate exceeded two minutes.");
    });

    await CheckAsync("concurrent one-shot runtime isolation", async () =>
    {
        var stopwatch = Stopwatch.StartNew();
        var work = Enumerable.Range(0, 4).Select(async index =>
        {
            var marker = $"parallel-run-{index}";
            var language = languages[0] with
            {
                Source = languages[0].Source.Replace("compose-csharp", marker, StringComparison.Ordinal),
                ExpectedOutput = marker
            };
            var runtime = runtimes[index % runtimes.Length];
            var execution = await ExecutePipelineDetailedAsync(language, "run", runtime.Id);
            Require(ResultType(execution.Result) == "run", "Concurrent Run returned the wrong result type.");
            Require(execution.Result.GetProperty("Status").GetString() == "completed", "Concurrent Run did not complete.");
            var output = DecodeOutput(execution.Events, "stdout");
            Require(output.Contains(marker, StringComparison.Ordinal), "Concurrent Run stdout crossed requests.");
            for (var other = 0; other < 4; other++)
            {
                if (other != index)
                    Require(!output.Contains($"parallel-run-{other}", StringComparison.Ordinal), "Concurrent Run leaked another request's stdout.");
            }
        }).ToArray();
        await Task.WhenAll(work);
        Require(stopwatch.Elapsed < TimeSpan.FromMinutes(2), "Concurrent runtime gate exceeded two minutes.");
    });
}

if (security)
{
    await CheckAsync("runtime container security", async () =>
    {
        var securityLanguage = new LanguageCase(
            "csharp",
            "roslyn-stable",
            "Program.cs",
            """
            using System;
            using System.IO;
            using System.Net.Sockets;
            using System.Threading;
            using System.Threading.Tasks;

            static void ProbeWrite(string path, string blocked, string writable)
            {
                try
                {
                    File.WriteAllText(path, "probe");
                    Console.WriteLine(writable);
                }
                catch
                {
                    Console.WriteLine(blocked);
                }
            }

            ProbeWrite("/usr/share/dotnet/security-probe", "rootfs-blocked", "rootfs-writable");
            ProbeWrite("/workspace/security-probe", "workspace-blocked", "workspace-writable");
            ProbeWrite("/tmp/security-probe", "tmp-blocked", "tmp-writable");

            try
            {
                using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await socket.ConnectAsync("1.1.1.1", 53, timeout.Token);
                Console.WriteLine("network-open");
            }
            catch
            {
                Console.WriteLine("network-blocked");
            }

            await Task.Delay(TimeSpan.FromSeconds(8));
            """,
            "security-probe",
            SupportsAst: true);
        var compileCheck = await ExecutePipelineAsync(securityLanguage, "compile-check", null);
        Require(
            ResultType(compileCheck) == "compile-check" &&
            compileCheck.GetProperty("CompilationSucceeded").GetBoolean(),
            $"Security probe did not compile: {compileCheck.GetRawText()}");
        RuntimeContainerInspection? inspection = null;
        var execution = await ExecutePipelineDetailedAsync(
            securityLanguage,
            "run",
            "dotnet-10-linux-x64",
            async _ => inspection = await InspectRuntimeContainerAsync());
        var result = execution.Result;
        Require(ResultType(result) == "run", "Security probe returned the wrong result type.");
        Require(result.GetProperty("Status").GetString() == "completed", "Security probe did not complete.");
        Require(result.GetProperty("ExitCode").GetInt32() == 0, "Security probe returned a non-zero exit code.");
        var observed = inspection
            ?? throw new InvalidOperationException("The one-shot runtime container was not inspected.");
        var output = DecodeOutput(execution.Events, "stdout");
        Require(output.Contains("rootfs-blocked", StringComparison.Ordinal), "The runtime root filesystem was writable.");
        Require(output.Contains("workspace-blocked", StringComparison.Ordinal), "The artifact workspace was writable.");
        Require(output.Contains("tmp-writable", StringComparison.Ordinal), "The bounded tmpfs was not writable.");
        Require(output.Contains("network-blocked", StringComparison.Ordinal), "The runtime container had outbound network access.");
        Require(!output.Contains("network-open", StringComparison.Ordinal), "The runtime container opened an outbound connection.");
        await WaitForDockerResourceRemovalAsync("container", observed.ContainerId);
        await WaitForDockerResourceRemovalAsync("volume", observed.WorkspaceVolumeName);
    });

    await CheckAsync("runtime WebSocket session reuse and cleanup", async () =>
    {
        var marker = $"reuse-probe-{Guid.NewGuid():N}";
        var reuseLanguage = languages[0] with
        {
            Source = """
            using System;
            using System.IO;
            using System.Threading.Tasks;

            var stdinPath = Environment.GetEnvironmentVariable("SHARPLABNEXT_STDIN_PATH");
            if (stdinPath is not null && File.Exists(stdinPath))
                Console.WriteLine($"stdin-present:{File.ReadAllText(stdinPath)}");
            else
                Console.WriteLine("stdin-absent");

            await Task.Delay(TimeSpan.FromSeconds(5));
            """,
            ExpectedOutput = "stdin-"
        };
        var baselineContainerIds = (await ReadManagedRuntimeContainersAsync())
            .Select(static container => container.ContainerId)
            .ToHashSet(StringComparer.Ordinal);
        RuntimeSessionDockerInspection? observedSession = null;
        using var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(OperationCommandWebSocketUri(), overallTimeout.Token);
            var firstResolution = await ResolveOverOperationWebSocketAsync(
                socket,
                reuseLanguage,
                "run",
                "dotnet-10-linux-x64",
                workspaceRevision: 1);
            var effective = firstResolution.GetProperty("EffectiveSelection");
            var referenceSetId = effective.GetProperty("ReferenceSetId").GetString()
                ?? throw new InvalidOperationException("Reuse probe reference set ID is missing.");
            var pipelineId = firstResolution.GetProperty("PipelineResolutionId").GetString()
                ?? throw new InvalidOperationException("Reuse probe pipeline resolution ID is missing.");
            var buildOptions = new
            {
                configuration = "release",
                optimize = true,
                outputKind = "console",
                allowUnsafe = false,
                emitPortablePdb = true,
                nullableContext = "project-default",
                languageVersion = reuseLanguage.LanguageVersion,
                preprocessorSymbols = Array.Empty<string>(),
                checkOverflow = false
            };
            var workspace = new
            {
                schemaVersion = 1,
                revision = 1,
                selectionRevision = 1,
                languageId = reuseLanguage.Id,
                files = new[] { new { path = reuseLanguage.FileName, version = 1, text = reuseLanguage.Source } },
                activeFile = reuseLanguage.FileName,
                sourceOrder = new[] { reuseLanguage.FileName },
                referenceSetId,
                buildOptions
            };
            var buildIdentity = Identity("reuse-build");
            var buildOperationId = await StartOverOperationWebSocketAsync(
                socket,
                "build",
                new
                {
                    buildIdentity.requestId,
                    buildIdentity.idempotencyKey,
                    pipelineResolutionId = pipelineId,
                    toolchainId = effective.GetProperty("ToolchainId").GetString(),
                    referenceSetId,
                    workspace,
                    deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(45),
                    options = buildOptions,
                    target = "artifact"
                });
            var buildExecution = await WaitForOperationAsync(buildOperationId);
            Require(ResultType(buildExecution.Result) == "build", "Reuse probe build returned the wrong result type.");
            Require(buildExecution.Result.GetProperty("Outcome").GetString() == "succeeded", "Reuse probe build failed.");
            var artifactRef = buildExecution.Result.GetProperty("ArtifactRef").GetString()
                ?? throw new InvalidOperationException("Reuse probe build returned no artifact reference.");

            var firstRunIdentity = Identity("reuse-run-first");
            var firstRunOperationId = await StartOverOperationWebSocketAsync(
                socket,
                "run",
                new
                {
                    firstRunIdentity.requestId,
                    firstRunIdentity.idempotencyKey,
                    pipelineResolutionId = pipelineId,
                    artifactRef,
                    runtimeProfileId = "dotnet-10-linux-x64",
                    options = new
                    {
                        arguments = Array.Empty<string>(),
                        stdin = marker,
                        instrumentation = "none",
                        securityPolicyId = firstResolution.GetProperty("PipelinePlan").GetProperty("SecurityPolicyId").GetString()
                    },
                    deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(60)
                });
            observedSession = await WaitForActiveRuntimeSessionAsync(baselineContainerIds);
            await RequireRuntimeSessionVolumeIdentityAsync(observedSession);
            var firstExecution = await WaitForOperationAsync(firstRunOperationId);
            Require(ResultType(firstExecution.Result) == "run", "First reused Run returned the wrong result type.");
            Require(firstExecution.Result.GetProperty("Status").GetString() == "completed", "First reused Run did not complete.");
            var firstOutput = DecodeOutput(firstExecution.Events, "stdout");
            Require(
                firstOutput.Contains($"stdin-present:{marker}", StringComparison.Ordinal),
                "The first reused Run did not observe its unique stdin workspace file.");
            Require(
                ContainerProgress(firstExecution.Events).Contains(
                    $"Created isolated container {observedSession.ContainerId}.",
                    StringComparison.Ordinal),
                "The first reused Run did not report the observed runtime container.");
            await WaitForDockerContainerStateAsync(observedSession.ContainerId, "exited");
            await WaitForDockerContainerStateAsync(observedSession.MaterializerContainerId, "exited");

            var secondResolution = await ResolveOverOperationWebSocketAsync(
                socket,
                reuseLanguage,
                "run",
                "dotnet-10-linux-x64",
                workspaceRevision: 2);
            var secondPipelineId = secondResolution.GetProperty("PipelineResolutionId").GetString()
                ?? throw new InvalidOperationException("Second reuse probe pipeline resolution ID is missing.");
            Require(secondPipelineId != pipelineId, "Workspace revision did not produce a fresh pipeline resolution.");
            var secondRunIdentity = Identity("reuse-run-second");
            var secondRunOperationId = await StartOverOperationWebSocketAsync(
                socket,
                "run",
                new
                {
                    secondRunIdentity.requestId,
                    secondRunIdentity.idempotencyKey,
                    pipelineResolutionId = secondPipelineId,
                    artifactRef,
                    runtimeProfileId = "dotnet-10-linux-x64",
                    options = new
                    {
                        arguments = Array.Empty<string>(),
                        stdin = (string?)null,
                        instrumentation = "none",
                        securityPolicyId = secondResolution.GetProperty("PipelinePlan").GetProperty("SecurityPolicyId").GetString()
                    },
                    deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(60)
                });
            var secondObservation = await WaitForActiveRuntimeSessionAsync(
                baselineContainerIds,
                observedSession);
            Require(secondObservation == observedSession, "The compatible Run did not reuse all runtime session resources.");
            var secondExecution = await WaitForOperationAsync(secondRunOperationId);
            Require(ResultType(secondExecution.Result) == "run", "Second reused Run returned the wrong result type.");
            Require(secondExecution.Result.GetProperty("Status").GetString() == "completed", "Second reused Run did not complete.");
            var secondOutput = DecodeOutput(secondExecution.Events, "stdout");
            Require(secondOutput.Contains("stdin-absent", StringComparison.Ordinal), "The second reused Run still found stdin.txt.");
            Require(!secondOutput.Contains(marker, StringComparison.Ordinal), "The second reused Run leaked first-run workspace content.");
            Require(
                ContainerProgress(secondExecution.Events).Contains(
                    $"Reused session-isolated container {observedSession.ContainerId}.",
                    StringComparison.Ordinal),
                "The second Run did not report reuse of the observed runtime container.");
            await WaitForDockerContainerStateAsync(observedSession.ContainerId, "exited");
            await WaitForDockerContainerStateAsync(observedSession.MaterializerContainerId, "exited");

            await socket.CloseOutputAsync(
                WebSocketCloseStatus.NormalClosure,
                "runtime reuse smoke complete",
                overallTimeout.Token);
            await WaitForDockerResourceRemovalAsync("container", observedSession.ContainerId, maximumAttempts: 200);
            await WaitForDockerResourceRemovalAsync("container", observedSession.MaterializerContainerId, maximumAttempts: 200);
            await WaitForDockerResourceRemovalAsync("volume", observedSession.WorkspaceVolumeName, maximumAttempts: 200);
            observedSession = null;
        }
        finally
        {
            socket.Abort();
            if (observedSession is not null)
            {
                await WaitForDockerResourceRemovalAsync("container", observedSession.ContainerId, maximumAttempts: 200);
                await WaitForDockerResourceRemovalAsync("container", observedSession.MaterializerContainerId, maximumAttempts: 200);
                await WaitForDockerResourceRemovalAsync("volume", observedSession.WorkspaceVolumeName, maximumAttempts: 200);
            }
        }
    });
}

Console.WriteLine();
var matrix = full ? "full" : security ? "security" : "quick";
Console.WriteLine($"Gateway Compose smoke: {passed} passed, {failures.Count} failed ({matrix} matrix).");
if (failures.Count > 0)
{
    foreach (var failure in failures)
        Console.Error.WriteLine($"- {failure}");
    Environment.ExitCode = 1;
}

async Task CheckAsync(string name, Func<Task> action)
{
    try
    {
        await action();
        passed++;
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{name}: {exception.Message}");
        Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
    }
}

async Task<JsonElement> ExecutePipelineAsync(LanguageCase language, string outputId, string? runtimeId) =>
    (await ExecutePipelineDetailedAsync(language, outputId, runtimeId)).Result;

async Task<PipelineExecution> ExecutePipelineDetailedAsync(
    LanguageCase language,
    string outputId,
    string? runtimeId,
    Func<string, Task>? onRuntimeOperationStarted = null,
    string buildOutputKind = "console")
{
    var resolution = await ResolveAsync(language, outputId, runtimeId);
    var pipelineId = resolution.GetProperty("PipelineResolutionId").GetString()
        ?? throw new InvalidOperationException("Pipeline resolution ID is missing.");
    var effective = resolution.GetProperty("EffectiveSelection");
    var referenceSetId = effective.GetProperty("ReferenceSetId").GetString()
        ?? throw new InvalidOperationException("Reference set ID is missing.");
    var stages = resolution.GetProperty("PipelinePlan").GetProperty("Stages").EnumerateArray().ToArray();
    Require(stages.Length > 0, "Resolved pipeline has no stages.");
    var buildOptions = new Dictionary<string, object?>
    {
        ["Configuration"] = "release",
        ["Optimize"] = true,
        ["OutputKind"] = buildOutputKind
    };
    if (!StringComparer.Ordinal.Equals(language.Id, "jsharp"))
    {
        buildOptions["AllowUnsafe"] = false;
        buildOptions["EmitPortablePdb"] = true;
        buildOptions["NullableContext"] = "project-default";
        buildOptions["LanguageVersion"] = language.LanguageVersion;
        buildOptions["PreprocessorSymbols"] = Array.Empty<string>();
        buildOptions["CheckOverflow"] = false;
    }
    var workspace = new
    {
        schemaVersion = 1,
        revision = 1,
        selectionRevision = 1,
        languageId = language.Id,
        files = new[] { new { path = language.FileName, version = 1, text = language.Source } },
        activeFile = language.FileName,
        sourceOrder = new[] { language.FileName },
        referenceSetId,
        buildOptions
    };

    if (stages[0].GetProperty("Kind").GetString() == "explain")
    {
        var explainIdentity = Identity("explain");
        return await StartAndWaitAsync("/api/v1/explanations", new
        {
            explainIdentity.requestId,
            explainIdentity.idempotencyKey,
            pipelineResolutionId = pipelineId,
            workspace,
            deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(45)
        });
    }

    var buildTarget = outputId switch
    {
        "compile-check" => "compile-check",
        "ast" => "ast",
        "generated-source" => "generated-source",
        _ => "artifact"
    };
    var buildIdentity = Identity("build");
    var buildExecution = await StartAndWaitAsync("/api/v1/builds", new
    {
        buildIdentity.requestId,
        buildIdentity.idempotencyKey,
        pipelineResolutionId = pipelineId,
        toolchainId = effective.GetProperty("ToolchainId").GetString(),
        referenceSetId,
        workspace,
        deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(45),
        options = buildOptions,
        target = buildTarget
    });
    var currentResult = buildExecution.Result;
    if (stages.Length == 1)
        return buildExecution;

    Require(ResultType(currentResult) == "build", "Artifact pipeline did not begin with a build result.");
    Require(currentResult.GetProperty("Outcome").GetString() == "succeeded", "Artifact build did not succeed.");
    var artifactRef = currentResult.GetProperty("ArtifactRef").GetString()
        ?? throw new InvalidOperationException("Artifact build returned no artifact reference.");

    for (var index = 1; index < stages.Length; index++)
    {
        var stage = stages[index];
        var kind = stage.GetProperty("Kind").GetString();
        var stageId = stage.GetProperty("Id").GetString()
            ?? throw new InvalidOperationException("Pipeline stage ID is missing.");
        var providerId = stage.GetProperty("ProviderId").GetString()
            ?? throw new InvalidOperationException("Pipeline provider ID is missing.");
        PipelineExecution execution;
        switch (kind)
        {
            case "transform":
            {
                var identity = Identity("transform");
                execution = await StartAndWaitAsync("/api/v1/artifact-transforms", new
                {
                    identity.requestId,
                    identity.idempotencyKey,
                    pipelineResolutionId = pipelineId,
                    artifactRef,
                    processorId = providerId,
                    transformId = stageId,
                    options = new
                    {
                        preservePortablePdb = true,
                        preserveSequencePoints = true,
                        rewriterProfileId = stageId == "runtime-instrumentation-v1"
                            ? "execution-flow-v1"
                            : null
                    },
                    deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(45)
                });
                Require(ResultType(execution.Result) == "artifact-transform", "Transform returned the wrong result type.");
                Require(execution.Result.GetProperty("Outcome").GetString() == "succeeded", "Transform did not succeed.");
                artifactRef = execution.Result.GetProperty("ArtifactRef").GetString()
                    ?? throw new InvalidOperationException("Transform returned no artifact reference.");
                break;
            }
            case "render":
            {
                var identity = Identity("render");
                execution = await StartAndWaitAsync("/api/v1/artifact-renders", new
                {
                    identity.requestId,
                    identity.idempotencyKey,
                    pipelineResolutionId = pipelineId,
                    artifactRef,
                    processorId = providerId,
                    outputId = stageId,
                    options = new
                    {
                        includeSequencePoints = true,
                        includeCompilerGeneratedMembers = true,
                        maxCharacters = 1_000_000
                    },
                    deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(45)
                });
                break;
            }
            case "verify":
            {
                var identity = Identity("verify");
                execution = await StartAndWaitAsync("/api/v1/verifications", new
                {
                    identity.requestId,
                    identity.idempotencyKey,
                    pipelineResolutionId = pipelineId,
                    artifactRef,
                    processorId = providerId,
                    options = new
                    {
                        verificationProfileId = providerId == "artifacts-const-generics"
                            ? "il-verify"
                            : "default",
                        includeMetadataTokens = true,
                        maxFindings = 1_000
                    },
                    deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(45)
                });
                break;
            }
            case "run":
            {
                var identity = Identity("run");
                execution = await StartAndWaitAsync("/api/v1/runs", new
                {
                    identity.requestId,
                    identity.idempotencyKey,
                    pipelineResolutionId = pipelineId,
                    artifactRef,
                    runtimeProfileId = effective.GetProperty("RuntimeId").GetString(),
                    options = new
                    {
                        arguments = Array.Empty<string>(),
                        stdin = (string?)null,
                        instrumentation = outputId == "execution-flow" ? "execution-flow" : "none",
                        securityPolicyId = resolution.GetProperty("PipelinePlan").GetProperty("SecurityPolicyId").GetString()
                    },
                    deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(60)
                }, onRuntimeOperationStarted);
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
                    runtimeProfileId = effective.GetProperty("RuntimeId").GetString(),
                    options = new
                    {
                        methodFilter = (string?)null,
                        tieringPolicyId = "tier0-diffable",
                        pgoPolicyId = "disabled",
                        providerId = "coreclr-jitdisasm",
                        securityPolicyId = resolution.GetProperty("PipelinePlan").GetProperty("SecurityPolicyId").GetString()
                    },
                    deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(60)
                });
                break;
            }
            default:
                throw new InvalidOperationException($"Unsupported pipeline stage '{kind}'.");
        }

        currentResult = execution.Result;
        if (index == stages.Length - 1)
            return execution with { BuildArtifactRef = artifactRef };
    }

    throw new InvalidOperationException("Pipeline did not produce a terminal stage.");
}

async Task<JsonElement> ResolveAsync(LanguageCase language, string outputId, string? runtimeId)
{
    using var response = await PostResolutionAsync(language, outputId, runtimeId);
    await EnsureSuccessAsync(response, $"Resolve {language.Id}/{outputId}/{runtimeId}");
    using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(overallTimeout.Token));
    return document.RootElement.Clone();
}

async Task CheckGatewayLspAsync(LanguageCase language)
{
    var resolution = await ResolveAsync(language, "compile-check", null);
    var pipelineId = resolution.GetProperty("PipelineResolutionId").GetString()
        ?? throw new InvalidOperationException("Language session resolution returned no pipeline ID.");
    var referenceSetId = resolution.GetProperty("EffectiveSelection").GetProperty("ReferenceSetId").GetString()
        ?? throw new InvalidOperationException("Language session resolution returned no reference set ID.");
    var workspace = new
    {
        schemaVersion = 1,
        revision = 1,
        selectionRevision = 1,
        languageId = language.Id,
        files = new[] { new { path = language.FileName, version = 1, text = language.Source } },
        activeFile = language.FileName,
        sourceOrder = new[] { language.FileName },
        referenceSetId,
        buildOptions = new
        {
            configuration = "release",
            optimize = true,
            outputKind = "auto",
            allowUnsafe = false,
            emitPortablePdb = true,
            nullableContext = "project-default",
            languageVersion = language.LanguageVersion,
            preprocessorSymbols = Array.Empty<string>(),
            checkOverflow = false
        }
    };
    using var openResponse = await http.PostAsJsonAsync(
        "/api/v1/language-sessions",
        new
        {
            requestId = $"smoke-lsp-{language.ToolchainId}-{Guid.NewGuid():N}",
            pipelineResolutionId = pipelineId,
            languageId = language.Id,
            toolchainId = language.ToolchainId,
            referenceSetId,
            workspace,
            lspVersion = "3.17"
        },
        json,
        overallTimeout.Token);
    await EnsureSuccessAsync(openResponse, $"Open {language.ToolchainId} language session");
    using var opened = JsonDocument.Parse(
        await openResponse.Content.ReadAsByteArrayAsync(overallTimeout.Token));
    var descriptor = opened.RootElement;
    var sessionId = descriptor.GetProperty("SessionId").GetString()
        ?? throw new InvalidOperationException("Gateway language session returned no session ID.");

    try
    {
        var webSocketUrl = descriptor.GetProperty("WebSocketUrl").GetString()
            ?? throw new InvalidOperationException("Gateway language session returned no WebSocket URL.");
        var lockedToolchain = ReadLockedComponent(language.ToolchainId);
        Require(
            descriptor.GetProperty("ToolchainId").GetString() == language.ToolchainId,
            "Gateway language session used the wrong G# profile.");
        Require(
            descriptor.GetProperty("CompilerBuildIdentity").GetString() ==
                $"{lockedToolchain.ResolvedVersion}@{lockedToolchain.Commit}",
            "Gateway language session used the wrong G# compiler identity.");
        Require(
            descriptor.GetProperty("LspVersion").GetString() == "3.17",
            "Gateway negotiated the wrong LSP version.");

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(LanguageSessionWebSocketUri(webSocketUrl), overallTimeout.Token);
        await SendLspAsync(socket, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new { processId = (int?)null, rootUri = (string?)null, capabilities = new { } }
        });
        using (var initialized = await ReceiveLspResponseAsync(socket, 1))
        {
            Require(!initialized.RootElement.TryGetProperty("error", out _), "G# LSP initialize returned an error.");
            var capabilities = initialized.RootElement.GetProperty("result").GetProperty("capabilities");
            Require(
                capabilities.GetProperty("hoverProvider").GetBoolean(),
                "G# LSP initialize did not advertise hover.");
            Require(
                capabilities.GetProperty("semanticTokensProvider").ValueKind == JsonValueKind.Object,
                "G# LSP initialize did not advertise semantic tokens.");
        }

        await SendLspAsync(socket, new { jsonrpc = "2.0", id = 2, method = "shutdown", @params = new { } });
        using (var shutdown = await ReceiveLspResponseAsync(socket, 2))
        {
            Require(!shutdown.RootElement.TryGetProperty("error", out _), "G# LSP shutdown returned an error.");
            Require(
                shutdown.RootElement.GetProperty("result").ValueKind == JsonValueKind.Null,
                "G# LSP shutdown returned a non-null result.");
        }
        await SendLspAsync(socket, new { jsonrpc = "2.0", method = "exit", @params = new { } });
        if (socket.State == WebSocketState.Open)
        {
            await socket.CloseOutputAsync(
                WebSocketCloseStatus.NormalClosure,
                "Gateway G# LSP smoke complete.",
                overallTimeout.Token);
        }
    }
    finally
    {
        using var closeResponse = await http.DeleteAsync(
            $"/api/v1/language-sessions/{Uri.EscapeDataString(sessionId)}",
            overallTimeout.Token);
        await EnsureSuccessAsync(closeResponse, $"Close {language.ToolchainId} language session");
    }
}

Uri LanguageSessionWebSocketUri(string path)
{
    var builder = new UriBuilder(new Uri(baseAddress, path))
    {
        Scheme = baseAddress.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
        Query = string.Empty,
        Fragment = string.Empty
    };
    return builder.Uri;
}

async Task SendLspAsync(ClientWebSocket socket, object message)
{
    var bytes = JsonSerializer.SerializeToUtf8Bytes(message, lspJson);
    await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, overallTimeout.Token);
}

async Task<JsonDocument> ReceiveLspResponseAsync(ClientWebSocket socket, int id)
{
    for (var attempt = 0; attempt < 32; attempt++)
    {
        using var content = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, overallTimeout.Token);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("G# LSP WebSocket closed before the expected response.");
            Require(result.MessageType == WebSocketMessageType.Text, "G# LSP returned a non-text message.");
            Require(content.Length + result.Count <= 2 * 1024 * 1024, "G# LSP response exceeded 2 MiB.");
            content.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                break;
        }

        var message = JsonDocument.Parse(content.ToArray());
        if (message.RootElement.TryGetProperty("id", out var actualId) &&
            actualId.ValueKind == JsonValueKind.Number &&
            actualId.GetInt32() == id)
        {
            return message;
        }
        message.Dispose();
    }
    throw new InvalidOperationException($"G# LSP response '{id}' was not received.");
}

Task<HttpResponseMessage> PostResolutionAsync(LanguageCase language, string outputId, string? runtimeId) =>
    http.PostAsJsonAsync("/api/v1/selections/resolve", new
    {
        languageId = language.Id,
        toolchainId = language.ToolchainId,
        referenceSetId = language.ReferenceSetId,
        outputId,
        runtimeId,
        buildMode = "release",
        catalogRevision,
        workspaceRevision = 1
    }, json, overallTimeout.Token);

async Task<PipelineExecution> StartAndWaitAsync(
    string path,
    object request,
    Func<string, Task>? onStarted = null)
{
    using var start = await http.PostAsJsonAsync(path, request, json, overallTimeout.Token);
    await EnsureSuccessAsync(start, path);
    using var handle = JsonDocument.Parse(await start.Content.ReadAsByteArrayAsync(overallTimeout.Token));
    var operationId = handle.RootElement.GetProperty("OperationId").GetString()
        ?? throw new InvalidOperationException($"{path} returned no operation ID.");
    if (onStarted is not null)
        await onStarted(operationId);

    return await WaitForOperationAsync(operationId);
}

async Task<PipelineExecution> WaitForOperationAsync(string operationId)
{
    JsonElement state = default;
    for (var attempt = 0; attempt < 600; attempt++)
    {
        using var response = await http.GetAsync($"/api/v1/operations/{operationId}", overallTimeout.Token);
        await EnsureSuccessAsync(response, $"Operation {operationId}");
        using var stateDocument = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(overallTimeout.Token));
        state = stateDocument.RootElement.Clone();
        var status = state.GetProperty("Status").GetString();
        if (status is "completed" or "failed" or "cancelled")
            break;
        await Task.Delay(100, overallTimeout.Token);
    }

    var terminalStatus = state.GetProperty("Status").GetString();
    if (terminalStatus != "completed")
    {
        var error = state.TryGetProperty("Error", out var errorElement) && errorElement.ValueKind == JsonValueKind.Object
            ? errorElement.GetProperty("PublicMessage").GetString()
            : null;
        throw new InvalidOperationException($"Operation {operationId} ended as {terminalStatus}: {error}");
    }

    using var eventResponse = await http.GetAsync(
        $"/api/v1/operations/{operationId}/events?FromSequence=0",
        overallTimeout.Token);
    await EnsureSuccessAsync(eventResponse, $"Events for {operationId}");
    var eventText = await eventResponse.Content.ReadAsStringAsync(overallTimeout.Token);
    var events = eventText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Where(static line => line.StartsWith("data: ", StringComparison.Ordinal))
        .Select(static line =>
        {
            using var document = JsonDocument.Parse(line["data: ".Length..]);
            return document.RootElement.Clone();
        })
        .ToArray();
    Require(events.Length > 0, $"Operation {operationId} returned no events.");
    var typedResults = events
        .Where(static operationEvent =>
            operationEvent.GetProperty("Payload").GetProperty("Kind").GetString() == "typed-result")
        .Select(static operationEvent => operationEvent.GetProperty("Payload").GetProperty("Result"))
        .ToArray();
    Require(typedResults.Length == 1, $"Operation {operationId} returned {typedResults.Length} typed results.");
    return new PipelineExecution(operationId, typedResults[0].Clone(), events);
}

async Task<string> ReadResultContentAsync(PipelineExecution execution)
{
    var result = execution.Result;
    var contentProperty = ResultType(result) switch
    {
        "artifact-render" => "ContentRef",
        "jit" => "RawTextRef",
        _ => string.Empty
    };
    if (contentProperty.Length == 0 ||
        !result.TryGetProperty(contentProperty, out var contentRefElement) ||
        contentRefElement.ValueKind != JsonValueKind.String)
    {
        throw new InvalidOperationException(
            $"Result contains no readable content reference: {result.GetRawText()}");
    }
    var contentRef = contentRefElement.GetString()
        ?? throw new InvalidOperationException("Content reference is null.");

    Require(
        execution.Events.Any(operationEvent =>
        {
            var payload = operationEvent.GetProperty("Payload");
            return payload.GetProperty("Kind").GetString() == "content-produced" &&
                payload.GetProperty("ContentRef").GetString() == contentRef;
        }),
        $"Operation returned {contentRef} without a matching content-produced event.");

    var digest = contentRef.StartsWith("sha256:", StringComparison.Ordinal)
        ? contentRef["sha256:".Length..]
        : throw new InvalidOperationException("Content reference has the wrong format.");
    using var response = await http.GetAsync(
        $"/api/v1/operations/{execution.OperationId}/contents/sha256/{digest}",
        overallTimeout.Token);
    await EnsureSuccessAsync(response, $"Content {contentRef}");
    return await response.Content.ReadAsStringAsync(overallTimeout.Token);
}

string DecodeOutput(IReadOnlyList<JsonElement> events, string channel)
{
    var builder = new StringBuilder();
    foreach (var operationEvent in events)
    {
        var payload = operationEvent.GetProperty("Payload");
        if (payload.GetProperty("Kind").GetString() != "output-chunk")
            continue;
        var chunk = payload.GetProperty("Chunk");
        if (chunk.GetProperty("Channel").GetString() != channel)
            continue;
        var data = chunk.GetProperty("Data").GetString() ?? string.Empty;
        builder.Append(Encoding.UTF8.GetString(Convert.FromBase64String(data)));
    }
    return builder.ToString();
}

string ContainerProgress(IReadOnlyList<JsonElement> events) => events
    .Select(static operationEvent => operationEvent.GetProperty("Payload"))
    .Where(static payload =>
        payload.GetProperty("Kind").GetString() == "progress" &&
        payload.GetProperty("Stage").GetString() == "container")
    .Select(static payload => payload.GetProperty("Message").GetString())
    .LastOrDefault(static message => message is not null) ?? string.Empty;

Uri OperationCommandWebSocketUri()
{
    var builder = new UriBuilder(baseAddress)
    {
        Scheme = baseAddress.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
        Path = "/api/v1/operations/ws",
        Query = string.Empty,
        Fragment = string.Empty
    };
    return builder.Uri;
}

async Task<JsonElement> ResolveOverOperationWebSocketAsync(
    ClientWebSocket socket,
    LanguageCase language,
    string outputId,
    string runtimeId,
    int workspaceRevision)
{
    var commandId = $"cmd_resolve_{Guid.NewGuid():N}";
    return await SendOperationCommandAsync(
        socket,
        commandId,
        expectedStatus: 200,
        new
        {
            type = "resolve-selection",
            commandId,
            request = new
            {
                languageId = language.Id,
                toolchainId = language.ToolchainId,
                referenceSetId = language.ReferenceSetId,
                outputId,
                runtimeId,
                buildMode = "release",
                catalogRevision,
                workspaceRevision
            }
        });
}

async Task<string> StartOverOperationWebSocketAsync(
    ClientWebSocket socket,
    string operation,
    object request)
{
    var commandId = $"cmd_start_{Guid.NewGuid():N}";
    var payload = await SendOperationCommandAsync(
        socket,
        commandId,
        expectedStatus: 202,
        new
        {
            type = "start",
            commandId,
            operation,
            request
        });
    return payload.GetProperty("OperationId").GetString()
        ?? throw new InvalidOperationException($"WebSocket {operation} command returned no operation ID.");
}

async Task<JsonElement> SendOperationCommandAsync(
    ClientWebSocket socket,
    string commandId,
    int expectedStatus,
    object command)
{
    var bytes = JsonSerializer.SerializeToUtf8Bytes(command, json);
    await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, overallTimeout.Token);
    using var response = await ReceiveOperationCommandAsync(socket);
    var root = response.RootElement;
    Require(root.GetProperty("Type").GetString() == "response", "Operation WebSocket returned a non-response message.");
    Require(root.GetProperty("CommandId").GetString() == commandId, "Operation WebSocket response crossed commands.");
    Require(
        root.GetProperty("Ok").GetBoolean(),
        $"Operation WebSocket command failed: {(root.TryGetProperty("Error", out var error) ? error.GetRawText() : root.GetRawText())}");
    Require(root.GetProperty("Status").GetInt32() == expectedStatus, "Operation WebSocket returned the wrong status.");
    return root.GetProperty("Payload").Clone();
}

async Task<JsonDocument> ReceiveOperationCommandAsync(ClientWebSocket socket)
{
    using var content = new MemoryStream();
    var buffer = new byte[16 * 1024];
    while (true)
    {
        var result = await socket.ReceiveAsync(buffer, overallTimeout.Token);
        if (result.MessageType == WebSocketMessageType.Close)
            throw new InvalidOperationException("Operation WebSocket closed before the command response.");
        Require(result.MessageType == WebSocketMessageType.Text, "Operation WebSocket returned a non-text message.");
        Require(content.Length + result.Count <= 2 * 1024 * 1024, "Operation WebSocket response exceeded 2 MiB.");
        content.Write(buffer, 0, result.Count);
        if (result.EndOfMessage)
            return JsonDocument.Parse(content.ToArray());
    }
}

static bool HasDenyByDefaultSeccomp(string securityOption)
{
    const string prefix = "seccomp=";
    if (!securityOption.StartsWith(prefix, StringComparison.Ordinal))
        return false;

    try
    {
        using var document = JsonDocument.Parse(securityOption[prefix.Length..]);
        return document.RootElement.TryGetProperty("defaultAction", out var defaultAction) &&
               defaultAction.GetString() == "SCMP_ACT_ERRNO";
    }
    catch (JsonException)
    {
        return false;
    }
}

async Task<RuntimeContainerInspection> InspectRuntimeContainerAsync()
{
    for (var attempt = 0; attempt < 150; attempt++)
    {
        var list = await RunDockerCommandAsync(
            "ps",
            "--filter", "label=com.sharplabnext.runtime-job=true",
            "--format", "{{.ID}}");
        Require(list.ExitCode == 0, $"Docker container listing failed: {list.StandardError}");
        foreach (var containerId in list.StandardOutput.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var inspect = await RunDockerCommandAsync("inspect", containerId);
            if (inspect.ExitCode != 0)
                continue;
            using var document = JsonDocument.Parse(inspect.StandardOutput);
            var root = document.RootElement[0];
            var config = root.GetProperty("Config");
            if (config.GetProperty("Labels").TryGetProperty("com.sharplabnext.materializer", out _))
                continue;
            var name = root.GetProperty("Name").GetString() ?? string.Empty;
            if (!name.StartsWith("/sln-run-", StringComparison.Ordinal))
                continue;

            var host = root.GetProperty("HostConfig");
            Require(config.GetProperty("User").GetString() == "1654:1654", "Runtime container did not use the sandbox user.");
            Require(config.GetProperty("NetworkDisabled").GetBoolean(), "Runtime container did not disable networking.");
            Require(host.GetProperty("NetworkMode").GetString() == "none", "Runtime container did not use NetworkMode=none.");
            Require(host.GetProperty("ReadonlyRootfs").GetBoolean(), "Runtime container root filesystem was not read-only.");
            Require(!host.GetProperty("Privileged").GetBoolean(), "Runtime container was privileged.");
            Require(host.GetProperty("Init").GetBoolean(), "Runtime container did not use an init process.");
            Require(host.GetProperty("IpcMode").GetString() == "none", "Runtime container did not use private IPC.");
            Require(host.GetProperty("Memory").GetInt64() == 268_435_456, "Runtime container memory limit was incorrect.");
            Require(host.GetProperty("MemorySwap").GetInt64() == 268_435_456, "Runtime container swap limit was incorrect.");
            Require(host.GetProperty("NanoCpus").GetInt64() == 1_000_000_000, "Runtime container CPU limit was incorrect.");
            Require(host.GetProperty("PidsLimit").GetInt64() == 64, "Runtime container PID limit was incorrect.");
            var logConfig = host.GetProperty("LogConfig");
            Require(logConfig.GetProperty("Type").GetString() == "local", "Runtime container did not use the local logging driver.");
            var logOptions = logConfig.GetProperty("Config");
            Require(logOptions.GetProperty("max-size").GetString() == "4m", "Runtime container log size limit was incorrect.");
            Require(logOptions.GetProperty("max-file").GetString() == "1", "Runtime container log file count was incorrect.");
            Require(logOptions.GetProperty("compress").GetString() == "false", "Runtime container log compression was not disabled.");
            Require(
                host.GetProperty("CapDrop").EnumerateArray().Any(static item => item.GetString() == "ALL"),
                "Runtime container did not drop all Linux capabilities.");
            Require(
                host.GetProperty("SecurityOpt").EnumerateArray().Any(static item =>
                    item.GetString()?.StartsWith("no-new-privileges", StringComparison.Ordinal) == true),
                "Runtime container did not enable no-new-privileges.");
            Require(
                host.GetProperty("SecurityOpt").EnumerateArray().Any(static item =>
                    item.GetString() is { } option && HasDenyByDefaultSeccomp(option)),
                "Runtime container did not use the deny-by-default seccomp profile.");
            var ulimits = host.GetProperty("Ulimits").EnumerateArray().ToArray();
            Require(
                ulimits.Any(static limit =>
                    limit.GetProperty("Name").GetString() == "nofile" &&
                    limit.GetProperty("Soft").GetInt64() == 256 &&
                    limit.GetProperty("Hard").GetInt64() == 256),
                "Runtime container did not apply the open-file limit.");
            Require(
                ulimits.Any(static limit =>
                    limit.GetProperty("Name").GetString() == "core" &&
                    limit.GetProperty("Soft").GetInt64() == 0 &&
                    limit.GetProperty("Hard").GetInt64() == 0),
                "Runtime container did not disable core dumps.");
            var tmpfs = host.GetProperty("Tmpfs").GetProperty("/tmp").GetString() ?? string.Empty;
            foreach (var option in new[] { "noexec", "nosuid", "nodev", "size=33554432" })
                Require(tmpfs.Contains(option, StringComparison.Ordinal), $"Runtime tmpfs omitted '{option}'.");

            var mounts = root.GetProperty("Mounts").EnumerateArray().ToArray();
            Require(
                mounts.All(static mount => mount.GetProperty("Destination").GetString() != "/var/run/docker.sock"),
                "Runtime container received the Docker socket.");
            var workspace = mounts.SingleOrDefault(static mount =>
                mount.GetProperty("Destination").GetString() == "/workspace");
            Require(workspace.ValueKind == JsonValueKind.Object, "Runtime container had no workspace mount.");
            Require(workspace.GetProperty("Type").GetString() == "volume", "Runtime workspace was not an isolated volume.");
            Require(!workspace.GetProperty("RW").GetBoolean(), "Runtime workspace mount was writable.");
            var volumeName = workspace.GetProperty("Name").GetString()
                ?? throw new InvalidOperationException("Runtime workspace volume had no name.");

            var shell = await RunDockerCommandAsync(
                "exec",
                containerId,
                "sh",
                "-c",
                """
                test "$(id -u)" = "1654" || exit 10
                if touch /usr/share/dotnet/security-exec-probe 2>/dev/null; then exit 11; fi
                if touch /workspace/security-exec-probe 2>/dev/null; then exit 12; fi
                touch /tmp/security-exec-write || exit 13
                printf '#!/bin/sh\nexit 0\n' > /tmp/security-exec-file
                chmod 700 /tmp/security-exec-file
                if /tmp/security-exec-file 2>/dev/null; then exit 14; fi
                test "$(awk '/^NoNewPrivs:/ { print $2 }' /proc/self/status)" = "1" || exit 15
                test "$(awk '/^Seccomp:/ { print $2 }' /proc/self/status)" = "2" || exit 16
                test "$(awk '/^CapEff:/ { print $2 }' /proc/self/status)" = "0000000000000000" || exit 17
                test "$(ulimit -Sn)" = "256" || exit 18
                test "$(ulimit -Hn)" = "256" || exit 19
                test "$(ulimit -c)" = "0" || exit 20
                """);
            Require(shell.ExitCode == 0, $"Runtime mount enforcement failed: {shell.StandardError}");
            return new RuntimeContainerInspection(containerId, volumeName);
        }

        await Task.Delay(100, overallTimeout.Token);
    }

    throw new InvalidOperationException("The one-shot runtime container did not become observable.");
}

async Task<IReadOnlyList<ManagedRuntimeContainer>> ReadManagedRuntimeContainersAsync()
{
    var list = await RunDockerCommandAsync(
        "ps",
        "--all",
        "--filter", "label=com.sharplabnext.runtime-job=true",
        "--format", "{{.ID}}");
    Require(list.ExitCode == 0, $"Docker container listing failed: {list.StandardError}");
    var containerIds = list.StandardOutput.Split(
        ['\r', '\n'],
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (containerIds.Length == 0)
        return [];

    var containers = new List<ManagedRuntimeContainer>();
    foreach (var containerId in containerIds)
    {
        var inspect = await RunDockerCommandAsync("container", "inspect", containerId);
        if (inspect.ExitCode != 0)
        {
            if (inspect.StandardError.Contains("no such object", StringComparison.OrdinalIgnoreCase) ||
                inspect.StandardError.Contains("no such container", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            throw new InvalidOperationException($"Docker container inspection failed: {inspect.StandardError}");
        }
        using var document = JsonDocument.Parse(inspect.StandardOutput);
        var root = document.RootElement[0];
        var config = root.GetProperty("Config");
        var labels = config.GetProperty("Labels");
        if (!labels.TryGetProperty("com.sharplabnext.runtime-job", out _))
            continue;
        var workspace = root.GetProperty("Mounts").EnumerateArray().FirstOrDefault(static mount =>
            mount.GetProperty("Destination").GetString() == "/workspace");
        containers.Add(new ManagedRuntimeContainer(
            root.GetProperty("Id").GetString()
                ?? throw new InvalidOperationException("Managed runtime container had no ID."),
            root.GetProperty("Name").GetString() ?? string.Empty,
            labels.GetProperty("com.sharplabnext.job-id").GetString()
                ?? throw new InvalidOperationException("Managed runtime container had no job ID."),
            labels.TryGetProperty("com.sharplabnext.materializer", out var materializer) &&
                materializer.GetString() == "true",
            root.GetProperty("State").GetProperty("Status").GetString() ?? string.Empty,
            workspace.ValueKind == JsonValueKind.Object
                ? workspace.GetProperty("Name").GetString()
                : null));
    }
    return containers;
}

async Task<RuntimeSessionDockerInspection> WaitForActiveRuntimeSessionAsync(
    IReadOnlySet<string> baselineContainerIds,
    RuntimeSessionDockerInspection? expected = null)
{
    for (var attempt = 0; attempt < 200; attempt++)
    {
        var containers = await ReadManagedRuntimeContainersAsync();
        var runtimes = containers.Where(container =>
            !container.Materializer &&
            container.Name.StartsWith("/sln-session-", StringComparison.Ordinal) &&
            container.State == "running" &&
            (expected is not null
                ? container.ContainerId == expected.ContainerId
                : !baselineContainerIds.Contains(container.ContainerId)));
        foreach (var runtime in runtimes)
        {
            if (runtime.WorkspaceVolumeName is null)
                continue;
            var materializers = containers.Where(container =>
                container.Materializer &&
                container.SessionId == runtime.SessionId &&
                container.State == "running" &&
                container.WorkspaceVolumeName == runtime.WorkspaceVolumeName &&
                (expected is null || container.ContainerId == expected.MaterializerContainerId)).ToArray();
            if (materializers.Length != 1)
                continue;

            var observation = new RuntimeSessionDockerInspection(
                runtime.SessionId,
                runtime.ContainerId,
                materializers[0].ContainerId,
                runtime.WorkspaceVolumeName);
            if (expected is null || observation == expected)
                return observation;
        }

        await Task.Delay(100, overallTimeout.Token);
    }

    throw new InvalidOperationException(
        expected is null
            ? "The reusable runtime and its running materializer did not become observable."
            : "The reusable runtime did not restart with the same materializer and workspace volume.");
}

async Task RequireRuntimeSessionVolumeIdentityAsync(RuntimeSessionDockerInspection session)
{
    var inspect = await RunDockerCommandAsync("volume", "inspect", session.WorkspaceVolumeName);
    Require(inspect.ExitCode == 0, $"Runtime session volume inspection failed: {inspect.StandardError}");
    using var document = JsonDocument.Parse(inspect.StandardOutput);
    var labels = document.RootElement[0].GetProperty("Labels");
    Require(
        labels.GetProperty("com.sharplabnext.runtime-job").GetString() == "workspace",
        "Runtime session volume did not have the workspace management label.");
    Require(
        labels.GetProperty("com.sharplabnext.job-id").GetString() == session.SessionId,
        "Runtime session volume did not share the container session identity.");

    var materializerInspect = await RunDockerCommandAsync(
        "container",
        "inspect",
        session.MaterializerContainerId);
    Require(
        materializerInspect.ExitCode == 0,
        $"Runtime session materializer inspection failed: {materializerInspect.StandardError}");
    using var materializerDocument = JsonDocument.Parse(materializerInspect.StandardOutput);
    var materializer = materializerDocument.RootElement[0];
    var config = materializer.GetProperty("Config");
    var host = materializer.GetProperty("HostConfig");
    Require(config.GetProperty("User").GetString() == "1654:1654", "Materializer did not use the sandbox user.");
    Require(config.GetProperty("NetworkDisabled").GetBoolean(), "Materializer did not disable networking.");
    Require(
        config.GetProperty("Entrypoint")[0].GetString() == "/bin/sh" &&
        config.GetProperty("Cmd")[1].GetString()?.Contains("rm -rf -- /workspace/", StringComparison.Ordinal) == true,
        "Materializer did not use the fixed workspace-cleanup command.");
    Require(host.GetProperty("NetworkMode").GetString() == "none", "Materializer did not use NetworkMode=none.");
    Require(host.GetProperty("ReadonlyRootfs").GetBoolean(), "Materializer root filesystem was not read-only.");
    Require(!host.GetProperty("Privileged").GetBoolean(), "Materializer was privileged.");
    Require(host.GetProperty("IpcMode").GetString() == "none", "Materializer did not use private IPC.");
    Require(host.GetProperty("PidsLimit").GetInt64() <= 16, "Materializer PID limit exceeded the helper boundary.");
    Require(host.GetProperty("LogConfig").GetProperty("Type").GetString() == "none", "Materializer logging was enabled.");
    Require(
        host.GetProperty("CapDrop").EnumerateArray().Any(static item => item.GetString() == "ALL"),
        "Materializer did not drop all Linux capabilities.");
    Require(
        host.GetProperty("SecurityOpt").EnumerateArray().Any(static item =>
            item.GetString()?.StartsWith("no-new-privileges", StringComparison.Ordinal) == true),
        "Materializer did not enable no-new-privileges.");
    Require(
        host.GetProperty("SecurityOpt").EnumerateArray().Any(static item =>
            item.GetString() is { } option && HasDenyByDefaultSeccomp(option)),
        "Materializer did not use the deny-by-default seccomp profile.");
    var workspace = materializer.GetProperty("Mounts").EnumerateArray().SingleOrDefault(static mount =>
        mount.GetProperty("Destination").GetString() == "/workspace");
    Require(workspace.ValueKind == JsonValueKind.Object, "Materializer had no workspace mount.");
    Require(workspace.GetProperty("Name").GetString() == session.WorkspaceVolumeName, "Materializer used a different workspace volume.");
    Require(workspace.GetProperty("RW").GetBoolean(), "Materializer workspace mount was not writable.");
}

async Task WaitForDockerContainerStateAsync(string containerId, string expectedState)
{
    for (var attempt = 0; attempt < 100; attempt++)
    {
        var inspect = await RunDockerCommandAsync("container", "inspect", containerId);
        if (inspect.ExitCode == 0)
        {
            using var document = JsonDocument.Parse(inspect.StandardOutput);
            if (document.RootElement[0].GetProperty("State").GetProperty("Status").GetString() == expectedState)
                return;
        }
        await Task.Delay(100, overallTimeout.Token);
    }
    throw new InvalidOperationException($"Docker container '{containerId}' did not become {expectedState}.");
}

async Task WaitForDockerResourceRemovalAsync(
    string resourceKind,
    string resourceId,
    int maximumAttempts = 100)
{
    for (var attempt = 0; attempt < maximumAttempts; attempt++)
    {
        var inspect = await RunDockerCommandAsync(resourceKind, "inspect", resourceId);
        if (inspect.ExitCode != 0)
            return;
        await Task.Delay(100, overallTimeout.Token);
    }
    throw new InvalidOperationException($"Docker {resourceKind} '{resourceId}' was not cleaned up.");
}

async Task<DockerCommandResult> RunDockerCommandAsync(params string[] arguments)
{
    var startInfo = new ProcessStartInfo("docker")
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    foreach (var argument in arguments)
        startInfo.ArgumentList.Add(argument);
    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Docker CLI could not be started.");
    var stdout = process.StandardOutput.ReadToEndAsync(overallTimeout.Token);
    var stderr = process.StandardError.ReadToEndAsync(overallTimeout.Token);
    try
    {
        await process.WaitForExitAsync(overallTimeout.Token);
    }
    catch
    {
        if (!process.HasExited)
            process.Kill(entireProcessTree: true);
        throw;
    }
    return new DockerCommandResult(process.ExitCode, await stdout, await stderr);
}

string ResultType(JsonElement result) => result.GetProperty("ResultType").GetString()
    ?? throw new InvalidOperationException("Operation result type is missing.");

(string requestId, string idempotencyKey) Identity(string kind)
{
    var requestId = $"req_{Guid.NewGuid():N}";
    return (requestId, $"{kind}:{requestId}");
}

static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation)
{
    if (response.IsSuccessStatusCode)
        return;
    var body = await response.Content.ReadAsStringAsync();
    throw new InvalidOperationException($"{operation} failed with {(int)response.StatusCode}: {body}");
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

void RequireIlVerification(JsonElement result, string context)
{
    Require(ResultType(result) == "artifact-verification", $"{context} IL Verify returned the wrong result type.");
    var outcome = result.GetProperty("Outcome").GetString();
    Require(outcome is "valid" or "findings", $"{context} IL Verify returned an infrastructure outcome.");
    Require(
        result.GetProperty("VerifierId").GetString() is { Length: > 0 },
        $"{context} IL Verify returned no verifier identity.");
    Require(
        result.GetProperty("VerifierVersion").GetString() is { Length: > 0 },
        $"{context} IL Verify returned no verifier version.");
    var findings = result.GetProperty("Findings").EnumerateArray().ToArray();
    Require(
        outcome == "valid" ? findings.Length == 0 : findings.Length > 0,
        $"{context} IL Verify outcome and findings disagree.");
    Require(
        findings.All(static finding =>
            finding.GetProperty("Code").GetString() is { Length: > 0 } &&
            finding.GetProperty("Message").GetString() is { Length: > 0 }),
        $"{context} IL Verify returned an unstructured finding.");
}

static JsonSerializerOptions CreateJsonOptions() => new(JsonSerializerDefaults.Web)
{
    PropertyNamingPolicy = PascalCaseJsonNamingPolicy.Instance,
    DictionaryKeyPolicy = null,
    TypeInfoResolver = new DefaultJsonTypeInfoResolver()
};

static JsonSerializerOptions CreateLspJsonOptions() => new(JsonSerializerDefaults.Web)
{
    TypeInfoResolver = new DefaultJsonTypeInfoResolver()
};

static LockedComponentIdentity ReadLockedComponent(string componentId)
{
    var lockPath = FindReleaseLockPath();
    using var document = JsonDocument.Parse(File.ReadAllBytes(lockPath));
    if (!document.RootElement.GetProperty("components").TryGetProperty(componentId, out var component))
        throw new InvalidOperationException($"Release lock '{lockPath}' has no component '{componentId}'.");

    return new LockedComponentIdentity(
        RequiredLockString(component, "resolvedVersion", componentId, lockPath),
        RequiredLockString(component, "commit", componentId, lockPath));
}

static string FindReleaseLockPath()
{
    if (Environment.GetEnvironmentVariable("SHARPLABNEXT_RELEASE_LOCK_PATH") is { Length: > 0 } explicitPath)
        return Path.GetFullPath(explicitPath);

    for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
    {
        foreach (var relativePath in new[] { Path.Combine("profiles", "lock.json"), "lock.json" })
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;
        }
    }

    throw new FileNotFoundException(
        "Could not find profiles/lock.json or bundle lock.json. Set SHARPLABNEXT_RELEASE_LOCK_PATH explicitly.");
}

static string RequiredLockString(JsonElement component, string propertyName, string componentId, string lockPath) =>
    component.TryGetProperty(propertyName, out var property) && property.GetString() is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException(
            $"Release lock component '{componentId}' in '{lockPath}' has no '{propertyName}'.");

sealed record LanguageCase(
    string Id,
    string ToolchainId,
    string FileName,
    string Source,
    string ExpectedOutput,
    bool SupportsAst,
    string ReferenceSetId = "net10-ref",
    string? LanguageVersion = null);

sealed record RuntimeCase(string Id, string VersionPrefix);

sealed class PascalCaseJsonNamingPolicy : JsonNamingPolicy
{
    public static readonly PascalCaseJsonNamingPolicy Instance = new();

    public override string ConvertName(string name) =>
        name.Length == 0 || !char.IsAsciiLetterLower(name[0])
            ? name
            : char.ToUpperInvariant(name[0]) + name[1..];
}

sealed record LockedComponentIdentity(string ResolvedVersion, string Commit);

sealed record RuntimeContainerInspection(string ContainerId, string WorkspaceVolumeName);

sealed record RuntimeSessionDockerInspection(
    string SessionId,
    string ContainerId,
    string MaterializerContainerId,
    string WorkspaceVolumeName);

sealed record ManagedRuntimeContainer(
    string ContainerId,
    string Name,
    string SessionId,
    bool Materializer,
    string State,
    string? WorkspaceVolumeName);

sealed record DockerCommandResult(int ExitCode, string StandardOutput, string StandardError);

sealed record PipelineExecution(
    string OperationId,
    JsonElement Result,
    IReadOnlyList<JsonElement> Events,
    string? BuildArtifactRef = null);
