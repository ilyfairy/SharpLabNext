using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using SharpLabNext.Contracts;
using SharpLabNext.LanguageWorker.Sdk;

namespace SharpLabNext.Worker.CppCli.Tests;

internal static class CppCliTestSettings
{
    public const string CompilerVersion = "19.51.36248";

    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "SharpLabNext-CppCliTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "cl"), "test compiler marker");
        return root;
    }

    public static CppCliWorkerSettings CreateSettings(string root) => new(
        new CppCliWorkerIdentity("test-release", CompilerVersion, null, $"sha256:{new string('a', 64)}"),
        new CppCliReferenceSetIdentity(
            $"sha256:{new string('b', 64)}",
            $"sha256:{new string('c', 64)}",
            $"docker://codex/msvc-wine@sha256:{new string('d', 64)}"),
        new CppCliProcessLimits(1024 * 1024, 1024L * 1024 * 1024, 100),
        Path.Combine(root, "work"),
        Path.Combine(root, "cl"));

    public static LanguageWorkerCapabilityManifest LoadManifest() =>
        LanguageWorkerCapabilityManifestSerializer.Load(Path.Combine(RepositoryRoot, "src", "Workers", "CppCli", "SharpLabNext.Worker.CppCli", "language-worker.json"));

    public static BuildRequest CreateRequest(BuildTarget target, string source = "using namespace System; int main() { Console::WriteLine(42); return 0; }")
    {
        var options = new BuildOptions(BuildConfiguration.Release, Optimize: true, BuildOutputKind.Console, AllowUnsafe: false, EmitPortablePdb: false, NullableContextMode.Disable, LanguageVersion: CompilerVersion);
        var workspace = new WorkspaceSnapshot(ContractSchemaVersions.WorkspaceSnapshot, 7, 3, CppCliToolchain.LanguageId, [new WorkspaceFile("Program.cpp", 1, source)], "Program.cpp", ["Program.cpp"], CppCliToolchain.ReferenceSetId, options);
        return new BuildRequest($"request-{Guid.NewGuid():N}", $"idempotency-{Guid.NewGuid():N}", "pipeline-cppcli-test", CppCliToolchain.ToolchainId, CppCliToolchain.ReferenceSetId, workspace, DateTimeOffset.UtcNow.AddSeconds(30), options, target);
    }

    public static byte[] CreateMixedModePe()
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString(CppCliToolchain.OutputFileName), metadata.GetOrAddGuid(Guid.Parse("318d0bae-3e4e-4d6c-9316-df9f365d9112")), default, default);
        metadata.AddAssembly(metadata.GetOrAddString(CppCliToolchain.AssemblyName), new Version(1, 0, 0, 0), default, default, (AssemblyFlags)0, AssemblyHashAlgorithm.Sha256);
        metadata.AddTypeDefinition(TypeAttributes.NotPublic, default, metadata.GetOrAddString("<Module>"), default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));

        var peBuilder = new ManagedPEBuilder(new PEHeaderBuilder(machine: Machine.Amd64, imageCharacteristics: Characteristics.ExecutableImage | Characteristics.LargeAddressAware, subsystem: Subsystem.WindowsCui), new MetadataRootBuilder(metadata), new BlobBuilder(), flags: (CorFlags)0);
        var image = new BlobBuilder();
        peBuilder.Serialize(image);
        return image.ToArray();
    }

    public static void DeleteRoot(string root)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && attempt < 9)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SharpLabNext.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("The SharpLabNext repository root could not be located.");
    }
}

internal sealed class FakeCppCliCompilerProcess(CppCliCompilerInvocation invocation) : ICppCliCompilerProcess
{
    public int CallCount { get; private set; }

    public Task<CppCliCompilerInvocation> CompileAsync(ValidatedCppCliWorkspace workspace, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;
        return Task.FromResult(invocation);
    }
}
