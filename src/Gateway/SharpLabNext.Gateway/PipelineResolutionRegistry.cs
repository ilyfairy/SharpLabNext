using System.Collections.Concurrent;
using SharpLabNext.Contracts;

namespace SharpLabNext.Gateway;

public sealed class PipelineResolutionRegistry
{
    private readonly ConcurrentDictionary<string, ResolveSelectionResponse> _resolutions = new(StringComparer.Ordinal);

    public void Store(ResolveSelectionResponse resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        _resolutions[resolution.PipelineResolutionId] = resolution;
    }

    public ResolveSelectionResponse? Get(string? resolutionId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(resolutionId))
        {
            return null;
        }

        if (!_resolutions.TryGetValue(resolutionId, out var resolution))
        {
            return null;
        }

        if (resolution.ExpiresAt > now)
        {
            return resolution;
        }

        _resolutions.TryRemove(resolutionId, out _);
        return null;
    }
}
