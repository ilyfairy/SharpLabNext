using SharpLabNext.Contracts;
using SharpLabNext.WorkerHost;

namespace SharpLabNext.Worker.PeachPie;

public sealed record LoadedPeachPieReferenceSet(PeachPieReferenceSetDefinition Definition, IReadOnlyList<string> ReferenceAssemblyPaths, ReferenceSetAttestation Attestation);

public sealed class PeachPieReferenceSetProvider
{
    private readonly IReadOnlyDictionary<string, LoadedPeachPieReferenceSet> _referenceSets;

    public PeachPieReferenceSetProvider(IReadOnlyList<PeachPieReferenceSetDefinition> definitions, bool requireAttestation)
    {
        var referenceSets = new Dictionary<string, LoadedPeachPieReferenceSet>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            var root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(definition.Path));
            if (!Directory.Exists(root))
                throw new InvalidDataException($"PeachPie reference set '{definition.Id}' does not exist.");
            var references = Directory.EnumerateFiles(root, "*.dll", SearchOption.TopDirectoryOnly).OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal).ToArray();
            if (references.Length == 0)
                throw new InvalidDataException($"PeachPie reference set '{definition.Id}' contains no assemblies.");
            var attestation = ReferenceSetAttestationReader.LoadAndVerify(root, definition.Id, definition.TargetFramework, definition.FrameworkVersion, definition.Digest, requireAttestation, definition.AttestationPath);
            if (!referenceSets.TryAdd(
                    definition.Id,
                    new LoadedPeachPieReferenceSet(definition with { Path = root }, references, attestation)))
            {
                throw new InvalidDataException($"Duplicate PeachPie reference set '{definition.Id}'.");
            }
        }
        _referenceSets = referenceSets;
    }

    public IReadOnlyList<ReferenceSetAttestation> Attestations => _referenceSets.Values.Select(static referenceSet => referenceSet.Attestation).ToArray();

    public LoadedPeachPieReferenceSet Get(string id) => _referenceSets.TryGetValue(id, out var referenceSet)
        ? referenceSet : throw new PeachPieReferenceSetUnavailableException($"PeachPie reference set '{id}' is unavailable.");
}
