using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using SharpLabNext.Contracts;
using SharpLabNext.LanguageWorker.Sdk;
using SharpLabNext.Worker.JSharp.TestCompiler;

namespace SharpLabNext.Worker.JSharp.Tests;

internal static class JSharpTestSettings
{
    public const string CompilerVersion = "2.0.50727.937";

    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "SharpLabNext-JSharpTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    public static JSharpWorkerSettings CreateSettings(
        string root,
        JSharpProcessLimits? limits = null) => new(
        new JSharpWorkerIdentity(
            "test-release",
            CompilerVersion,
            null,
            $"sha256:{new string('a', 64)}"),
        new JSharpReferenceSetIdentity(
            $"sha256:{new string('b', 64)}",
            $"sha256:{new string('c', 64)}",
            $"operator://test/jsharp20-ref/{new string('d', 64)}"),
        limits ?? new JSharpProcessLimits(
            1024 * 1024,
            512L * 1024 * 1024,
            100,
            25),
        Path.Combine(root, "work"),
        DotNetHostPath(),
        typeof(JSharpTestCompilerMarker).Assembly.Location);

    public static LanguageWorkerCapabilityManifest LoadManifest() =>
        LanguageWorkerCapabilityManifestSerializer.Load(Path.Combine(
            RepositoryRoot,
            "src",
            "Workers",
            "JSharp",
            "SharpLabNext.Worker.JSharp",
            "language-worker.json"));

    public static BuildRequest CreateRequest(
        BuildTarget target,
        string source = "public class Program { public static void main(String[] args) { System.Console.WriteLine(42); } }",
        DateTimeOffset? deadlineUtc = null)
    {
        var options = new BuildOptions(
            BuildConfiguration.Release,
            Optimize: true,
            BuildOutputKind.Console,
            AllowUnsafe: false,
            EmitPortablePdb: false,
            NullableContextMode.Disable,
            LanguageVersion: CompilerVersion);
        var workspace = new WorkspaceSnapshot(
            ContractSchemaVersions.WorkspaceSnapshot,
            7,
            3,
            JSharpToolchain.LanguageId,
            [new WorkspaceFile("Program.jsl", 1, source)],
            "Program.jsl",
            ["Program.jsl"],
            JSharpToolchain.ReferenceSetId,
            options);
        return new BuildRequest(
            $"request-{Guid.NewGuid():N}",
            $"idempotency-{Guid.NewGuid():N}",
            "pipeline-jsharp-test",
            JSharpToolchain.ToolchainId,
            JSharpToolchain.ReferenceSetId,
            workspace,
            deadlineUtc ?? DateTimeOffset.UtcNow.AddSeconds(30),
            options,
            target);
    }

    public static ValidatedJSharpWorkspace Validate(string source) =>
        JSharpWorkspaceValidator.Validate(
            CreateRequest(BuildTarget.Artifact, source),
            LoadManifest(),
            CompilerVersion);

    public static byte[] CreateClr2ManagedPe(
        Machine machine = Machine.Amd64,
        CorFlags flags = CorFlags.ILOnly)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(
            0,
            metadata.GetOrAddString(JSharpToolchain.OutputFileName),
            metadata.GetOrAddGuid(Guid.Parse("f210b967-cc13-4fc7-a2c8-67a793483d5d")),
            default,
            default);
        metadata.AddAssembly(
            metadata.GetOrAddString(JSharpToolchain.AssemblyName),
            new Version(1, 0, 0, 0),
            default,
            default,
            (AssemblyFlags)0,
            AssemblyHashAlgorithm.Sha256);
        metadata.AddTypeDefinition(
            TypeAttributes.NotPublic,
            default,
            metadata.GetOrAddString("<Module>"),
            default,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1));
        metadata.AddTypeDefinition(
            TypeAttributes.Public,
            default,
            metadata.GetOrAddString("Program"),
            default,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1));

        var signatureBuilder = new BlobBuilder();
        new BlobEncoder(signatureBuilder).MethodSignature().Parameters(
            0,
            static returnType => returnType.Void(),
            static _ => { });
        var signature = metadata.GetOrAddBlob(signatureBuilder);
        var bodyStream = new BlobBuilder();
        var instructions = new BlobBuilder();
        var instructionEncoder = new InstructionEncoder(instructions);
        instructionEncoder.OpCode(ILOpCode.Ret);
        var bodyOffset = new MethodBodyStreamEncoder(bodyStream).AddMethodBody(instructionEncoder);
        metadata.AddMethodDefinition(
            MethodAttributes.Public | MethodAttributes.Static,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            metadata.GetOrAddString("main"),
            signature,
            bodyOffset,
            MetadataTokens.ParameterHandle(1));

        var peBuilder = new ManagedPEBuilder(
            new PEHeaderBuilder(
                machine: machine,
                imageCharacteristics: Characteristics.ExecutableImage | Characteristics.LargeAddressAware,
                subsystem: Subsystem.WindowsCui),
            new MetadataRootBuilder(metadata, "v2.0.50727"),
            bodyStream,
            entryPoint: MetadataTokens.MethodDefinitionHandle(1),
            flags: flags);
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
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException && attempt < 9)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static string DotNetHostPath()
    {
        var fileName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var path = Path.GetFullPath(Path.Combine(
            RuntimeEnvironment.GetRuntimeDirectory(),
            "..",
            "..",
            "..",
            fileName));
        if (!File.Exists(path))
            throw new FileNotFoundException("Could not locate the dotnet host used by the J# process tests.", path);
        return path;
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

internal sealed class FakeJSharpCompilerProcess : IJSharpCompilerProcess
{
    private readonly Func<ValidatedJSharpWorkspace, CancellationToken, Task<JSharpCompilerInvocation>> _compile;

    public FakeJSharpCompilerProcess(JSharpCompilerInvocation invocation)
        : this((_, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(invocation);
        })
    {
    }

    public FakeJSharpCompilerProcess(
        Func<ValidatedJSharpWorkspace, CancellationToken, Task<JSharpCompilerInvocation>> compile)
    {
        _compile = compile;
    }

    public int CallCount { get; private set; }

    public Task<JSharpCompilerInvocation> CompileAsync(
        ValidatedJSharpWorkspace workspace,
        CancellationToken cancellationToken)
    {
        CallCount++;
        return _compile(workspace, cancellationToken);
    }
}
