using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using SharpLabNext.Contracts;
using SharpLabNext.Worker.FSharp.Compiler;
using SharpLabNext.WorkerHost;

namespace SharpLabNext.Worker.FSharp;

public sealed record LoadedFSharpReferenceSet(
    FSharpReferenceSetDefinition Definition,
    string ResolvedPath,
    IReadOnlyList<string> ReferenceAssemblyPaths,
    string FSharpCoreAssemblyPath,
    string FSharpCoreProductVersion,
    ReferenceSetAttestation Attestation);

public sealed record FSharpReferenceSetHealth(
    bool IsHealthy,
    string Message,
    IReadOnlyList<string> LoadedReferenceSetIds);

public sealed class FSharpReferenceSetProvider : IDisposable
{
    private static readonly string[] RequiredAssemblies =
    [
        "System.Runtime.dll",
        "System.Console.dll",
        "System.Collections.dll",
        "netstandard.dll"
    ];

    private readonly IReadOnlyDictionary<string, FSharpReferenceSetDefinition> _definitions;
    private readonly ConcurrentDictionary<string, LoadedFSharpReferenceSet> _loaded = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly bool _requireAttestation;

    public FSharpReferenceSetProvider(
        IEnumerable<FSharpReferenceSetDefinition> definitions,
        bool requireAttestation = false)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        _definitions = definitions.ToDictionary(static item => item.Id, StringComparer.Ordinal);
        _requireAttestation = requireAttestation;
    }

    public IReadOnlyList<ReferenceSetAttestation> Attestations => _loaded.Values
        .OrderBy(static item => item.Definition.Id, StringComparer.Ordinal)
        .Select(static item => item.Attestation)
        .ToArray();

    public async Task<LoadedFSharpReferenceSet> GetAsync(string id, CancellationToken cancellationToken)
    {
        if (!_definitions.TryGetValue(id, out var definition))
            throw new FSharpReferenceSetUnavailableException($"Reference set '{id}' is not configured for this worker.");
        if (_loaded.TryGetValue(id, out var cached))
            return cached;

        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded.TryGetValue(id, out cached))
                return cached;
            var loaded = Load(definition, _requireAttestation, cancellationToken);
            _loaded[id] = loaded;
            return loaded;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public async Task<FSharpReferenceSetHealth> CheckHealthAsync(CancellationToken cancellationToken)
    {
        if (_definitions.Count == 0)
            return new FSharpReferenceSetHealth(false, "No explicit reference sets are configured.", []);
        var loadedIds = new List<string>();
        foreach (var id in _definitions.Keys.Order(StringComparer.Ordinal))
        {
            try
            {
                await GetAsync(id, cancellationToken).ConfigureAwait(false);
                loadedIds.Add(id);
            }
            catch (FSharpReferenceSetUnavailableException exception)
            {
                return new FSharpReferenceSetHealth(false, exception.Message, loadedIds);
            }
        }
        return new FSharpReferenceSetHealth(true, $"Loaded {loadedIds.Count} explicit reference set(s).", loadedIds);
    }

    public void Dispose() => _loadLock.Dispose();

    private static LoadedFSharpReferenceSet Load(
        FSharpReferenceSetDefinition definition,
        bool requireAttestation,
        CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(definition.Path));
        if (!Directory.Exists(path))
            throw new FSharpReferenceSetUnavailableException($"Reference set '{definition.Id}' configured directory does not exist.");
        if (File.Exists(Path.Combine(path, "System.Private.CoreLib.dll")))
            throw new FSharpReferenceSetUnavailableException($"Reference set '{definition.Id}' points to runtime implementation assemblies.");
        foreach (var required in RequiredAssemblies)
        {
            if (!File.Exists(Path.Combine(path, required)))
                throw new FSharpReferenceSetUnavailableException($"Reference set '{definition.Id}' is missing '{required}'.");
        }
        ValidateReferenceAssembly(Path.Combine(path, "System.Runtime.dll"), definition.Id);
        var processRuntimeApiPath = typeof(SharpLab.Runtime.RuntimeServices).Assembly.Location;
        var attestedRuntimeApiPath = Path.Combine(path, Path.GetFileName(processRuntimeApiPath));
        if (requireAttestation && !File.Exists(attestedRuntimeApiPath))
        {
            throw new FSharpReferenceSetUnavailableException(
                $"Reference set '{definition.Id}' is missing its attested SharpLab.Runtime assembly.");
        }

        ReferenceSetAttestation attestation;
        try
        {
            attestation = ReferenceSetAttestationReader.LoadAndVerify(
                path,
                definition.Id,
                definition.TargetFramework,
                definition.FrameworkVersion,
                definition.Digest,
                requireAttestation,
                definition.AttestationPath);
        }
        catch (InvalidDataException exception)
        {
            throw new FSharpReferenceSetUnavailableException(
                $"Reference set '{definition.Id}' attestation validation failed.",
                exception);
        }
        var effectiveDefinition = definition with
        {
            TargetFramework = attestation.TargetFramework,
            FrameworkVersion = attestation.Provenance.ResolvedVersion
        };

        var runtimeApiPath = File.Exists(attestedRuntimeApiPath)
            ? attestedRuntimeApiPath
            : processRuntimeApiPath;
        var references = Directory.EnumerateFiles(path, "*.dll", SearchOption.TopDirectoryOnly)
            .Append(runtimeApiPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
        foreach (var reference in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(reference))
                throw new FSharpReferenceSetUnavailableException($"Reference set '{definition.Id}' contains a missing assembly.");
        }

        var fsharpCore = Path.GetFullPath(FSharpCompilerFacade.FSharpCoreAssemblyPath);
        if (!File.Exists(fsharpCore))
            throw new FSharpReferenceSetUnavailableException("The pinned FSharp.Core assembly is unavailable.");
        var productVersion = FileVersionInfo.GetVersionInfo(fsharpCore).ProductVersion ?? string.Empty;
        if (!productVersion.StartsWith(FSharpCompilerFacade.FSharpCorePackageVersion, StringComparison.Ordinal))
            throw new FSharpReferenceSetUnavailableException("The loaded FSharp.Core package identity is not the pinned version.");

        return new LoadedFSharpReferenceSet(
            effectiveDefinition,
            path,
            references,
            fsharpCore,
            productVersion,
            attestation);
    }

    private static void ValidateReferenceAssembly(string assemblyPath, string referenceSetId)
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            var reader = peReader.GetMetadataReader();
            var definition = reader.GetAssemblyDefinition();
            var isReference = definition.GetCustomAttributes()
                .Select(handle => GetAttributeTypeName(reader, reader.GetCustomAttribute(handle).Constructor))
                .Any(static name => name == "System.Runtime.CompilerServices.ReferenceAssemblyAttribute");
            if (!isReference)
                throw new FSharpReferenceSetUnavailableException($"Reference set '{referenceSetId}' does not contain reference assemblies.");
        }
        catch (FSharpReferenceSetUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            throw new FSharpReferenceSetUnavailableException($"Reference set '{referenceSetId}' failed validation.", exception);
        }
    }

    private static string? GetAttributeTypeName(MetadataReader reader, EntityHandle constructor)
    {
        var type = constructor.Kind switch
        {
            HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)constructor).Parent,
            HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType(),
            _ => default
        };
        return type.Kind switch
        {
            HandleKind.TypeReference => Format(reader, reader.GetTypeReference((TypeReferenceHandle)type)),
            HandleKind.TypeDefinition => Format(reader, reader.GetTypeDefinition((TypeDefinitionHandle)type)),
            _ => null
        };
    }

    private static string Format(MetadataReader reader, TypeReference type) =>
        $"{reader.GetString(type.Namespace)}.{reader.GetString(type.Name)}";

    private static string Format(MetadataReader reader, TypeDefinition type) =>
        $"{reader.GetString(type.Namespace)}.{reader.GetString(type.Name)}";
}
