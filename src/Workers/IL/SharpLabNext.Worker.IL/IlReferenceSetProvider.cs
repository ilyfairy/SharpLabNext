using System.Collections.Concurrent;
using System.Collections.Immutable;
using EleCho.ILSense;
using EleCho.ILSense.Contracts;
using EleCho.ILSense.Metadata;
using EleCho.ILSense.Metadata.Index;
using SharpLabNext.Contracts;
using SharpLabNext.WorkerHost;

namespace SharpLabNext.Worker.IL;

public sealed record IlReferenceSetHealth(bool IsHealthy, string Message);

public sealed class IlReferenceSetProvider
{
    private const int MaxAssemblies = 1_024;
    private const long MaxAssemblyBytes = 64L * 1024 * 1024;
    private const long MaxTotalAssemblyBytes = 512L * 1024 * 1024;
    private readonly IReadOnlyDictionary<string, IlReferenceSetDefinition> _definitions;
    private readonly ConcurrentDictionary<string, Lazy<Task<IILMetadataCatalog>>> _catalogs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IlReferenceSetDefinition> _loadedDefinitions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ReferenceSetAttestation> _attestations = new(StringComparer.Ordinal);
    private readonly bool _requireAttestation;

    public IlReferenceSetProvider(
        IEnumerable<IlReferenceSetDefinition> definitions,
        bool requireAttestation = false)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        _definitions = definitions.ToDictionary(static item => item.Id, StringComparer.Ordinal);
        _requireAttestation = requireAttestation;
    }

    public IReadOnlyList<ReferenceSetAttestation> Attestations => _attestations.Values
        .OrderBy(static item => item.Id, StringComparer.Ordinal)
        .ToArray();

    public IlReferenceSetDefinition Get(string id)
    {
        if (_loadedDefinitions.TryGetValue(id, out var loaded))
            return loaded;
        if (!_definitions.TryGetValue(id, out var definition))
            throw new IlReferenceSetUnavailableException($"Reference set '{id}' is not configured for this worker.");
        if (!Directory.Exists(definition.Path) || !File.Exists(Path.Combine(definition.Path, "System.Runtime.dll")))
            throw new IlReferenceSetUnavailableException($"Reference set '{id}' is unavailable.");
        var attestedRuntimeApiPath = AttestedRuntimeApiPath(definition.Path);
        if (_requireAttestation && !File.Exists(attestedRuntimeApiPath))
        {
            throw new IlReferenceSetUnavailableException(
                $"Reference set '{id}' is missing its attested SharpLab.Runtime assembly.");
        }
        ReferenceSetAttestation attestation;
        try
        {
            attestation = ReferenceSetAttestationReader.LoadAndVerify(
                definition.Path,
                definition.Id,
                definition.TargetFramework,
                definition.FrameworkVersion,
                definition.Digest,
                _requireAttestation,
                definition.AttestationPath);
        }
        catch (InvalidDataException exception)
        {
            throw new IlReferenceSetUnavailableException(
                $"Reference set '{id}' attestation validation failed.",
                exception);
        }
        var effective = definition with
        {
            TargetFramework = attestation.TargetFramework,
            FrameworkVersion = attestation.Provenance.ResolvedVersion
        };
        _attestations[id] = attestation;
        return _loadedDefinitions.GetOrAdd(id, effective);
    }

    public async Task<IILMetadataCatalog> GetCatalogAsync(string id, CancellationToken cancellationToken)
    {
        var definition = Get(id);
        var lazy = _catalogs.GetOrAdd(
            id,
            _ => new Lazy<Task<IILMetadataCatalog>>(
                () => BuildCatalogAsync(definition),
                LazyThreadSafetyMode.ExecutionAndPublication));
        var task = lazy.Value;
        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch when (!cancellationToken.IsCancellationRequested && task.IsFaulted)
        {
            _catalogs.TryRemove(new KeyValuePair<string, Lazy<Task<IILMetadataCatalog>>>(id, lazy));
            throw;
        }
    }

    public IlReferenceSetHealth CheckHealth()
    {
        if (_definitions.Count == 0)
            return new IlReferenceSetHealth(false, "No IL reference-set metadata is configured.");
        foreach (var definition in _definitions.Values)
        {
            try
            {
                _ = Get(definition.Id);
            }
            catch (IlReferenceSetUnavailableException exception)
            {
                return new IlReferenceSetHealth(false, exception.Message);
            }
        }
        return new IlReferenceSetHealth(true, $"{_definitions.Count} IL reference-set definition(s) are available.");
    }

    internal static string RuntimeApiPath(string referenceSetPath)
    {
        var attestedPath = AttestedRuntimeApiPath(referenceSetPath);
        return File.Exists(attestedPath)
            ? attestedPath
            : typeof(SharpLab.Runtime.RuntimeServices).Assembly.Location;
    }

    private static async Task<IILMetadataCatalog> BuildCatalogAsync(IlReferenceSetDefinition definition)
    {
        var referenceRoot = Path.GetFullPath(definition.Path);
        var runtimeApiPath = RuntimeApiPath(referenceRoot);
        var paths = Directory.EnumerateFiles(referenceRoot, "*.dll", SearchOption.TopDirectoryOnly)
            .Append(runtimeApiPath)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        if (paths.Length == 0 || paths.Length > MaxAssemblies)
        {
            throw new IlReferenceSetUnavailableException(
                $"Reference set '{definition.Id}' contains an unsupported number of metadata assemblies.");
        }
        var files = paths.Select(static path => new FileInfo(path)).ToArray();
        if (files.Any(static file => file.Length > MaxAssemblyBytes) ||
            files.Sum(static file => file.Length) > MaxTotalAssemblyBytes)
        {
            throw new IlReferenceSetUnavailableException(
                $"Reference set '{definition.Id}' exceeds the IL metadata catalog size limits.");
        }

        var allowedRoots = paths
            .Select(Path.GetDirectoryName)
            .Where(static path => path is not null)
            .Select(static path => Path.GetFullPath(path!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        var catalog = new ILMetadataCatalog(new AssemblyCatalogOptions
        {
            MaxAssemblyBytes = MaxAssemblyBytes,
            MaxTotalAssemblyBytes = MaxTotalAssemblyBytes,
            MaxAssemblies = MaxAssemblies,
            MaxImportDuration = TimeSpan.FromMinutes(2),
            MaxConcurrentImports = 2,
            MaxPendingImports = 8,
            UnknownReferenceSetBehavior = UnknownReferenceSetBehavior.Error,
            AllowedFileRoots = allowedRoots,
            RequireFileWithinAllowedRoot = true,
            LazyMemberIndexing = false
        });
        try
        {
            var handles = await catalog.AddRangeAsync(
                paths.Select(static path => (AssemblySource)new AssemblySource.File(path, Path.GetFileName(path))),
                new AssemblyImportOptions(IncludeCompilerGeneratedMembers: true),
                CancellationToken.None).ConfigureAwait(false);
            catalog.DefineReferenceSet(definition.Id, handles, cancellationToken: CancellationToken.None);
            return catalog;
        }
        catch (Exception exception) when (exception is MetadataImportException or IOException or UnauthorizedAccessException)
        {
            throw new IlReferenceSetUnavailableException(
                $"Reference set '{definition.Id}' could not be indexed for IL language services.",
                exception);
        }
    }

    private static string AttestedRuntimeApiPath(string referenceSetPath) =>
        Path.Combine(
            Path.GetFullPath(referenceSetPath),
            Path.GetFileName(typeof(SharpLab.Runtime.RuntimeServices).Assembly.Location));
}
