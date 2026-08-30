namespace SharpLabNext.ArtifactWorker.Sdk;

public sealed class ArtifactWorkerHandlerRegistry
{
    private readonly IReadOnlyDictionary<string, IArtifactTransformHandler> _transforms;
    private readonly IReadOnlyDictionary<string, IArtifactRenderHandler> _renders;
    private readonly IReadOnlyDictionary<string, IArtifactVerificationHandler> _verifications;

    public ArtifactWorkerHandlerRegistry(ArtifactWorkerCapabilityManifest manifest, IEnumerable<IArtifactTransformHandler> transforms, IEnumerable<IArtifactRenderHandler> renders, IEnumerable<IArtifactVerificationHandler> verifications)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        _transforms = Index(transforms, static handler => handler.TransformId, "transform");
        _renders = Index(renders, static handler => handler.OutputId, "render output");
        _verifications = Index(verifications, static handler => handler.VerificationProfileId, "verification profile");
        ValidateDeclaredHandlers(manifest.TransformIds, _transforms.Keys, "transform");
        ValidateDeclaredHandlers(manifest.RenderOutputIds, _renders.Keys, "render output");
        ValidateDeclaredHandlers(manifest.VerificationProfileIds, _verifications.Keys, "verification profile");
    }

    public IArtifactTransformHandler GetTransform(string transformId) => _transforms.TryGetValue(transformId, out var handler) ? handler : throw Unsupported("transform", transformId);

    public IArtifactRenderHandler GetRender(string outputId) => _renders.TryGetValue(outputId, out var handler) ? handler : throw Unsupported("render output", outputId);

    public IArtifactVerificationHandler GetVerification(string profileId) => _verifications.TryGetValue(profileId, out var handler) ? handler : throw Unsupported("verification profile", profileId);

    private static Dictionary<string, THandler> Index<THandler>(IEnumerable<THandler> handlers, Func<THandler, string> idSelector, string description)
        where THandler : class
    {
        ArgumentNullException.ThrowIfNull(handlers);
        var result = new Dictionary<string, THandler>(StringComparer.Ordinal);
        foreach (var handler in handlers)
        {
            ArgumentNullException.ThrowIfNull(handler);
            var id = idSelector(handler);
            if (string.IsNullOrWhiteSpace(id) || !result.TryAdd(id, handler))
                throw new InvalidOperationException($"Artifact worker {description} handlers must have unique stable IDs.");
        }
        return result;
    }

    private static void ValidateDeclaredHandlers(IReadOnlyList<string> declared, IEnumerable<string> registered, string description)
    {
        var declaredSet = declared.ToHashSet(StringComparer.Ordinal);
        var registeredSet = registered.ToHashSet(StringComparer.Ordinal);
        if (!declaredSet.SetEquals(registeredSet))
        {
            throw new InvalidOperationException($"The registered artifact {description} handlers do not match the capability manifest.");
        }
    }

    private static ArtifactWorkerRequestException Unsupported(string description, string id) => new("unsupported-capability", $"The requested artifact {description} '{Limit(id)}' is not supported.", Microsoft.AspNetCore.Http.StatusCodes.Status400BadRequest, SharpLabNext.Contracts.WorkerErrorCategory.UnsupportedCapability);

    private static string Limit(string value) => value.Length <= 128 ? value : value[..128];
}
