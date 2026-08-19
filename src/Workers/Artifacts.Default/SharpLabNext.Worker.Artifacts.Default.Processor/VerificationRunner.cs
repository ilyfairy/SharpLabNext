using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using ILVerify;
using SharpLabNext.ArtifactProcessing.Protocol;

namespace SharpLabNext.ArtifactProcessing;

internal static class VerificationRunner
{
    public static Task<ProcessorResponse> VerifyAsync(
        ProcessorRequest request,
        CancellationToken cancellationToken)
    {
        using var assemblyResolver = new BoundedAssemblyResolver(
            Path.GetDirectoryName(request.AssemblyPath)!,
            request.ReferenceRoots);
        using var resolver = new VerificationResolver(assemblyResolver.Paths);
        using var stream = File.OpenRead(request.AssemblyPath);
        using var peReader = new PEReader(stream, PEStreamOptions.PrefetchEntireImage);
        if (!peReader.HasMetadata)
            throw new BadImageFormatException();
        var metadata = peReader.GetMetadataReader();
        var verifier = new Verifier(resolver, new VerifierOptions
        {
            IncludeMetadataTokensInErrorMessages = request.IncludeMetadataTokens,
            SanityChecks = true
        });
        if (!string.IsNullOrWhiteSpace(request.SystemModuleName))
            verifier.SetSystemModuleName(AssemblyNameInfo.Parse(request.SystemModuleName));

        var findings = new List<ProcessorFinding>();
        var truncated = false;
        foreach (var finding in verifier.Verify(peReader))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (findings.Count >= request.MaxFindings)
            {
                truncated = true;
                break;
            }
            findings.Add(ToFinding(finding, metadata));
        }

        var outcome = truncated
            ? ProcessorOutcome.LimitExceeded
            : findings.Count == 0 ? ProcessorOutcome.Succeeded : ProcessorOutcome.Findings;
        return Task.FromResult(new ProcessorResponse(
            ProcessorProtocol.Version,
            outcome,
            "microsoft-ilverification",
            ProcessorProtocol.IlVerificationVersion,
            "application/vnd.sharplabnext.il-verification+json",
            0,
            [],
            findings,
            truncated,
            truncated ? "Verification findings exceeded the configured limit." : null));
    }

    private static ProcessorFinding ToFinding(VerificationResult result, MetadataReader metadata)
    {
        string? typeName = null;
        string? methodName = null;
        int? token = null;
        if (!result.Type.IsNil)
        {
            var type = metadata.GetTypeDefinition(result.Type);
            typeName = JoinTypeName(metadata.GetString(type.Namespace), metadata.GetString(type.Name));
            token = MetadataTokens.GetToken(result.Type);
        }
        if (!result.Method.IsNil)
        {
            var method = metadata.GetMethodDefinition(result.Method);
            methodName = metadata.GetString(method.Name);
            var declaringType = method.GetDeclaringType();
            if (!declaringType.IsNil)
            {
                var type = metadata.GetTypeDefinition(declaringType);
                typeName = JoinTypeName(metadata.GetString(type.Namespace), metadata.GetString(type.Name));
            }
            token = MetadataTokens.GetToken(result.Method);
        }
        return new ProcessorFinding(
            result.Code.ToString(),
            SanitizeMessage(result.Message),
            typeName,
            methodName,
            token,
            null,
            null);
    }

    private static string JoinTypeName(string ns, string name) =>
        string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";

    private static string SanitizeMessage(string message)
    {
        var singleLine = message.Replace('\r', ' ').Replace('\n', ' ');
        return singleLine.Length <= 4_096 ? singleLine : singleLine[..4_096];
    }

    private sealed class VerificationResolver(IReadOnlyDictionary<string, string> paths) : IResolver, IDisposable
    {
        private readonly Dictionary<string, PEReader> _readers = new(StringComparer.OrdinalIgnoreCase);

        public PEReader ResolveAssembly(AssemblyNameInfo assemblyName) => Resolve(assemblyName.Name);

        public PEReader ResolveModule(AssemblyNameInfo referencingAssembly, string fileName) =>
            Resolve(Path.GetFileNameWithoutExtension(fileName));

        public void Dispose()
        {
            foreach (var reader in _readers.Values)
                reader.Dispose();
            _readers.Clear();
        }

        private PEReader Resolve(string simpleName)
        {
            if (_readers.TryGetValue(simpleName, out var cached))
                return cached;
            if (!paths.TryGetValue(simpleName, out var path))
                return null!;
            var reader = new PEReader(File.OpenRead(path), PEStreamOptions.PrefetchEntireImage);
            _readers.Add(simpleName, reader);
            return reader;
        }
    }
}
