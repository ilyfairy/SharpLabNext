using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using SharpLabNext.Contracts;
using SharpLabNext.WorkerHost;

namespace SharpLabNext.Worker.Roslyn;

public sealed record LoadedReferenceSet(ReferenceSetDefinition Definition, string ResolvedPath, ImmutableArray<MetadataReference> References, ImmutableArray<string> AssemblyPaths, ReferenceSetAttestation Attestation);

public sealed record ReferenceSetHealth(bool IsHealthy, string Message, IReadOnlyList<string> LoadedReferenceSetIds);

public sealed class ReferenceSetProvider : IDisposable
{
    private static readonly string[] RequiredCoreAssemblies =
    [
        "System.Runtime.dll",
        "System.Console.dll",
        "System.Collections.dll",
        "netstandard.dll"
    ];

    private readonly IReadOnlyDictionary<string, ReferenceSetDefinition> _definitions;
    private readonly ConcurrentDictionary<string, LoadedReferenceSet> _loaded = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly bool _requireAttestation;

    public ReferenceSetProvider(IEnumerable<ReferenceSetDefinition> definitions, bool requireAttestation = false)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var materialized = definitions.ToArray();
        var duplicate = materialized.GroupBy(static definition => definition.Id, StringComparer.Ordinal).FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Duplicate reference set id '{duplicate.Key}'.");
        foreach (var definition in materialized)
            definition.Validate();

        _definitions = materialized.ToDictionary(static definition => definition.Id, StringComparer.Ordinal);
        _requireAttestation = requireAttestation;
    }

    public IReadOnlyCollection<string> ReferenceSetIds => _definitions.Keys.ToArray();

    public IReadOnlyList<ReferenceSetAttestation> Attestations => _loaded.Values.OrderBy(static item => item.Definition.Id, StringComparer.Ordinal).Select(static item => item.Attestation).ToArray();

    public async Task<LoadedReferenceSet> GetAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_definitions.TryGetValue(id, out var definition))
            throw new ReferenceSetUnavailableException($"Reference set '{id}' is not configured for this worker.");

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

    public async Task<ReferenceSetHealth> CheckHealthAsync(CancellationToken cancellationToken)
    {
        if (_definitions.Count == 0)
            return new ReferenceSetHealth(false, "No explicit reference sets are configured.", []);

        var loadedIds = new List<string>(_definitions.Count);
        foreach (var id in _definitions.Keys.Order(StringComparer.Ordinal))
        {
            try
            {
                await GetAsync(id, cancellationToken).ConfigureAwait(false);
                loadedIds.Add(id);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ReferenceSetUnavailableException exception)
            {
                return new ReferenceSetHealth(false, exception.Message, loadedIds);
            }
        }

        return new ReferenceSetHealth(true, $"Loaded {loadedIds.Count} explicit reference set(s).", loadedIds);
    }

    public void Dispose() => _loadLock.Dispose();

    private static LoadedReferenceSet Load(ReferenceSetDefinition definition, bool requireAttestation, CancellationToken cancellationToken)
    {
        var expandedPath = Environment.ExpandEnvironmentVariables(definition.Path);
        var path = Path.GetFullPath(expandedPath);
        if (!Directory.Exists(path))
        {
            throw new ReferenceSetUnavailableException($"Reference set '{definition.Id}' configured directory does not exist.");
        }

        if (File.Exists(Path.Combine(path, "System.Private.CoreLib.dll")))
        {
            throw new ReferenceSetUnavailableException($"Reference set '{definition.Id}' points to a runtime implementation directory instead of a reference bundle.");
        }

        var requiredAssemblies = definition.IsFrameworkReferenceSet
            ? ["mscorlib.dll"] : RequiredCoreAssemblies;
        foreach (var requiredAssembly in requiredAssemblies)
        {
            if (!File.Exists(Path.Combine(path, requiredAssembly)))
            {
                throw new ReferenceSetUnavailableException($"Reference set '{definition.Id}' is missing required reference assembly '{requiredAssembly}'.");
            }
        }

        if (definition.IsFrameworkReferenceSet)
        {
            ValidateFrameworkReferenceAssembly(Path.Combine(path, "mscorlib.dll"), definition);
        }
        else
        {
            ValidateReferenceAssemblyAttribute(Path.Combine(path, "System.Runtime.dll"), definition.Id);
        }
        var processRuntimeApiPath = typeof(SharpLab.Runtime.RuntimeServices).Assembly.Location;
        var attestedRuntimeApiPath = Path.Combine(path, Path.GetFileName(processRuntimeApiPath));
        if (definition.IncludeSharpLabRuntime && requireAttestation && !File.Exists(attestedRuntimeApiPath))
        {
            throw new ReferenceSetUnavailableException($"Reference set '{definition.Id}' is missing its attested SharpLab.Runtime assembly.");
        }

        ReferenceSetAttestation attestation;
        try
        {
            attestation = ReferenceSetAttestationReader.LoadAndVerify(path, definition.Id, definition.TargetFramework, definition.FrameworkVersion, definition.Digest, requireAttestation, definition.AttestationPath);
        }
        catch (InvalidDataException exception)
        {
            throw new ReferenceSetUnavailableException($"Reference set '{definition.Id}' attestation validation failed.", exception);
        }
        var effectiveDefinition = definition with { TargetFramework = attestation.TargetFramework, FrameworkVersion = attestation.Provenance.ResolvedVersion };

        var runtimeApiFileName = Path.GetFileName(processRuntimeApiPath);
        var assemblyPaths = Directory.EnumerateFiles(path, "*.dll", SearchOption.TopDirectoryOnly).Where(candidate => definition.IncludeSharpLabRuntime || !Path.GetFileName(candidate).Equals(runtimeApiFileName, StringComparison.OrdinalIgnoreCase)).Where(candidate => IsManagedAssembly(candidate, definition.Id)).Concat(definition.IncludeSharpLabRuntime ? [File.Exists(attestedRuntimeApiPath) ? attestedRuntimeApiPath : processRuntimeApiPath] : []).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToImmutableArray();
        if (assemblyPaths.Length < requiredAssemblies.Length)
        {
            throw new ReferenceSetUnavailableException($"Reference set '{definition.Id}' does not contain enough reference assemblies.");
        }

        var references = ImmutableArray.CreateBuilder<MetadataReference>(assemblyPaths.Length);
        foreach (var assemblyPath in assemblyPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                references.Add(MetadataReference.CreateFromFile(assemblyPath));
            }
            catch (Exception exception) when (exception is IOException or BadImageFormatException or UnauthorizedAccessException)
            {
                throw new ReferenceSetUnavailableException($"Reference set '{definition.Id}' contains an unreadable assembly.", exception);
            }
        }

        return new LoadedReferenceSet(effectiveDefinition, path, references.MoveToImmutable(), assemblyPaths, attestation);
    }

    private static bool IsManagedAssembly(string assemblyPath, string referenceSetId)
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            return peReader.HasMetadata && peReader.GetMetadataReader().IsAssembly;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ReferenceSetUnavailableException($"Reference set '{referenceSetId}' contains an unreadable assembly candidate.", exception);
        }
    }

    private static void ValidateReferenceAssemblyAttribute(string assemblyPath, string referenceSetId)
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            if (!peReader.HasMetadata)
                throw new BadImageFormatException("The configured assembly does not contain metadata.");

            var reader = peReader.GetMetadataReader();
            var assembly = reader.GetAssemblyDefinition();
            var isReferenceAssembly = assembly.GetCustomAttributes().Select(handle => GetAttributeTypeName(reader, reader.GetCustomAttribute(handle).Constructor)).Any(static name => name == "System.Runtime.CompilerServices.ReferenceAssemblyAttribute");
            if (!isReferenceAssembly)
            {
                throw new ReferenceSetUnavailableException($"Reference set '{referenceSetId}' does not contain reference assemblies.");
            }
        }
        catch (ReferenceSetUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            throw new ReferenceSetUnavailableException($"Reference set '{referenceSetId}' failed reference assembly validation.", exception);
        }
    }

    private static void ValidateFrameworkReferenceAssembly(string assemblyPath, ReferenceSetDefinition definition)
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            if (!peReader.HasMetadata || peReader.PEHeaders.CorHeader is null)
                throw new BadImageFormatException("The configured assembly does not contain CLR metadata.");

            var reader = peReader.GetMetadataReader();
            var assembly = reader.GetAssemblyDefinition();
            var isReferenceAssembly = assembly.GetCustomAttributes().Select(handle => GetAttributeTypeName(reader, reader.GetCustomAttribute(handle).Constructor)).Any(static name => name == "System.Runtime.CompilerServices.ReferenceAssemblyAttribute");
            if (isReferenceAssembly)
                return;

            if (!definition.IsLegacyFrameworkReferenceSet || !IsRecognizedLegacyFrameworkContract(peReader, reader, assembly))
            {
                throw new ReferenceSetUnavailableException($"Reference set '{definition.Id}' does not contain a recognized .NET Framework reference assembly.");
            }
        }
        catch (ReferenceSetUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            throw new ReferenceSetUnavailableException($"Reference set '{definition.Id}' failed reference assembly validation.", exception);
        }
    }

    private static bool IsRecognizedLegacyFrameworkContract(PEReader peReader, MetadataReader reader, AssemblyDefinition assembly)
    {
        var corHeader = peReader.PEHeaders.CorHeader!;
        if ((corHeader.Flags & CorFlags.ILOnly) == 0 || (corHeader.Flags & CorFlags.NativeEntryPoint) != 0 || (corHeader.Flags & CorFlags.StrongNameSigned) == 0 || assembly.PublicKey.IsNil || assembly.Version.Major != 2 || assembly.Version.Minor != 0 || !reader.GetString(assembly.Name).Equals("mscorlib", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return reader.TypeDefinitions.Any(handle =>
        {
            var type = reader.GetTypeDefinition(handle);
            return reader.GetString(type.Namespace) == "System" &&
                   reader.GetString(type.Name) == "Object";
        });
    }

    private static string? GetAttributeTypeName(MetadataReader reader, EntityHandle constructor)
    {
        EntityHandle typeHandle;
        switch (constructor.Kind)
        {
            case HandleKind.MemberReference:
                typeHandle = reader.GetMemberReference((MemberReferenceHandle)constructor).Parent;
                break;
            case HandleKind.MethodDefinition:
                typeHandle = reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType();
                break;
            default:
                return null;
        }

        return typeHandle.Kind switch
        {
            HandleKind.TypeReference => FormatTypeName(reader, reader.GetTypeReference((TypeReferenceHandle)typeHandle)),
            HandleKind.TypeDefinition => FormatTypeName(reader, reader.GetTypeDefinition((TypeDefinitionHandle)typeHandle)),
            _ => null
        };
    }

    private static string FormatTypeName(MetadataReader reader, TypeReference type) =>
        $"{reader.GetString(type.Namespace)}.{reader.GetString(type.Name)}";

    private static string FormatTypeName(MetadataReader reader, TypeDefinition type) =>
        $"{reader.GetString(type.Namespace)}.{reader.GetString(type.Name)}";

}

public sealed class ReferenceSetWarmupService(ReferenceSetProvider referenceSets, ILogger<ReferenceSetWarmupService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var health = await referenceSets.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        if (health.IsHealthy)
            WorkerLog.ReferenceSetPreflightSucceeded(logger, health.LoadedReferenceSetIds.Count);
        else
            WorkerLog.ReferenceSetPreflightFailed(logger, health.Message);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
