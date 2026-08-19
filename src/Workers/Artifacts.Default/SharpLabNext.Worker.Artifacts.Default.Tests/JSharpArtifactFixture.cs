using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace SharpLabNext.ArtifactWorker.Tests;

internal static class JSharpArtifactFixture
{
    public static byte[] CreateManagedPe(
        Machine machine = Machine.Amd64,
        CorFlags flags = CorFlags.ILOnly,
        string metadataVersion = "v2.0.50727")
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(
            0,
            metadata.GetOrAddString("SharpLabNext.User.exe"),
            metadata.GetOrAddGuid(Guid.Parse("75f75724-6599-44b9-b96e-9b63848e52d5")),
            default,
            default);
        metadata.AddAssembly(
            metadata.GetOrAddString("SharpLabNext.User"),
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
        var bodyStream = new BlobBuilder();
        var instructions = new BlobBuilder();
        var instructionEncoder = new InstructionEncoder(instructions);
        instructionEncoder.OpCode(ILOpCode.Ret);
        var bodyOffset = new MethodBodyStreamEncoder(bodyStream).AddMethodBody(instructionEncoder);
        metadata.AddMethodDefinition(
            MethodAttributes.Public | MethodAttributes.Static,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            metadata.GetOrAddString("main"),
            metadata.GetOrAddBlob(signatureBuilder),
            bodyOffset,
            MetadataTokens.ParameterHandle(1));

        var peBuilder = new ManagedPEBuilder(
            new PEHeaderBuilder(
                machine: machine,
                imageCharacteristics: Characteristics.ExecutableImage | Characteristics.LargeAddressAware,
                subsystem: Subsystem.WindowsCui),
            new MetadataRootBuilder(metadata, metadataVersion),
            bodyStream,
            entryPoint: MetadataTokens.MethodDefinitionHandle(1),
            flags: flags);
        var image = new BlobBuilder();
        peBuilder.Serialize(image);
        return image.ToArray();
    }
}
