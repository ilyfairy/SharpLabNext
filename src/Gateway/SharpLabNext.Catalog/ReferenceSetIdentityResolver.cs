namespace SharpLabNext.Catalog;

public static class ReferenceSetIdentityResolver
{
    public static IReadOnlyDictionary<string, string> ResolveExpectedDigests(CatalogDocument catalog, ReleaseLockDocument releaseLock)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(releaseLock);

        var errors = new List<string>();
        if (!string.Equals(catalog.ReleaseId, releaseLock.ReleaseId, StringComparison.Ordinal))
            errors.Add("Catalog and release lock release IDs do not match.");

        var hostedReferenceSetIds = catalog.Toolchains.Where(static toolchain => toolchain.Availability.IsSelectable).SelectMany(static toolchain => toolchain.AllowedReferenceSetIds).ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var referenceSet in catalog.ReferenceSets)
        {
            // A blocked matrix entry is outside the release closure unless a
            // selectable worker already advertises that it hosts the reference
            // set. Hosted content remains locked and attested while its matching
            // runtime is still waiting for promotion.
            if (!referenceSet.Availability.IsSelectable && !hostedReferenceSetIds.Contains(referenceSet.Id))
            {
                continue;
            }

            if (!releaseLock.Components.TryGetValue(referenceSet.Id, out var component))
            {
                errors.Add($"Reference set '{referenceSet.Id}' is missing from the release lock.");
                continue;
            }
            if (!string.Equals(component.Kind, "reference-set", StringComparison.Ordinal))
            {
                errors.Add($"Release lock component '{referenceSet.Id}' is not a reference set.");
                continue;
            }

            var expected = ResolveLockedDigest(component, referenceSet.Id, errors);
            if (expected is null)
                continue;
            if (!string.Equals(referenceSet.Digest, expected, StringComparison.Ordinal))
            {
                errors.Add($"Catalog reference set '{referenceSet.Id}' digest '{referenceSet.Digest}' does not match the release lock digest '{expected}'.");
                continue;
            }
            result.Add(referenceSet.Id, expected);
        }

        if (errors.Count > 0)
            throw new CatalogValidationException(errors);
        return result;
    }

    public static string ResolveLockedDigest(LockedComponent component, string id)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var errors = new List<string>();
        var digest = ResolveLockedDigest(component, id, errors);
        if (digest is null)
            throw new CatalogValidationException(errors);
        return digest;
    }

    private static string? ResolveLockedDigest(LockedComponent component, string id, List<string> errors)
    {
        if (!string.IsNullOrWhiteSpace(component.Package))
        {
            if (string.IsNullOrWhiteSpace(component.PackageContentHash))
            {
                errors.Add($"Package reference set '{id}' must carry packageContentHash in the release lock.");
                return null;
            }
            return component.PackageContentHash;
        }

        if (string.IsNullOrWhiteSpace(component.Digest))
        {
            errors.Add($"Source-built reference set '{id}' must carry digest in the release lock.");
            return null;
        }
        return component.Digest;
    }
}
