using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace SharpLabNext.BundleBuilder;

public static class DependencyInventory
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public static async Task<(LicensePolicy Policy, IReadOnlyList<DependencyComponent> Components)> LoadAsync(
        string repositoryRoot,
        string policyPath,
        CancellationToken cancellationToken = default)
    {
        await using var policyStream = File.OpenRead(policyPath);
        var policy = await JsonSerializer.DeserializeAsync<LicensePolicy>(
            policyStream,
            JsonOptions,
            cancellationToken)
            ?? throw new BundleValidationException("License policy is empty.");
        ValidatePolicy(policy);

        var components = new Dictionary<string, DependencyComponent>(StringComparer.OrdinalIgnoreCase);
        await ReadNpmAsync(repositoryRoot, policy, components, cancellationToken);
        ReadNuGet(repositoryRoot, policy, components);
        await ReadVendoredAsync(repositoryRoot, components, cancellationToken);
        var result = components.Values
            .OrderBy(static item => item.PackageManager, StringComparer.Ordinal)
            .ThenBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Version, StringComparer.Ordinal)
            .ToArray();
        foreach (var component in result)
        {
            ValidateLicense(component, policy);
        }

        return (policy, result);
    }

    private static async Task ReadVendoredAsync(
        string repositoryRoot,
        Dictionary<string, DependencyComponent> components,
        CancellationToken cancellationToken)
    {
        var inventoryPath = Path.Combine(repositoryRoot, "deploy", "security", "inventory.json");
        await using var stream = File.OpenRead(inventoryPath);
        var inventory = await JsonSerializer.DeserializeAsync<ReviewedDependencyInventory>(
            stream,
            JsonOptions,
            cancellationToken)
            ?? throw new BundleValidationException("The reviewed vendored dependency inventory is empty.");
        if (inventory.SchemaVersion != 1 || inventory.Components.Count == 0)
        {
            throw new BundleValidationException("The reviewed vendored dependency inventory is unsupported or empty.");
        }

        foreach (var component in inventory.Components)
        {
            ValidateVendoredIdentity(component);
            var sourcePath = ResolveRepositoryFile(repositoryRoot, component.SourcePath, component.Id);
            await using (var source = File.OpenRead(sourcePath))
            {
                var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(source, cancellationToken));
                if (!string.Equals(actual, component.Sha256, StringComparison.Ordinal))
                {
                    throw new BundleValidationException(
                        $"Vendored dependency '{component.Id}' does not match its reviewed SHA-256 identity.");
                }
            }

            var licensePath = ResolveRepositoryFile(repositoryRoot, component.LicensePath, component.Id);
            var licenseText = await File.ReadAllTextAsync(licensePath, cancellationToken);
            if (!licenseText.Contains("Apache License", StringComparison.Ordinal) ||
                !licenseText.Contains("END OF TERMS AND CONDITIONS", StringComparison.Ordinal) ||
                licenseText.Length < 10_000)
            {
                throw new BundleValidationException(
                    $"Vendored dependency '{component.Id}' does not include the complete reviewed license text.");
            }

            Add(components, new DependencyComponent(
                component.PackageManager,
                component.Name,
                component.Version,
                $"sha256-{Convert.ToBase64String(Convert.FromHexString(component.Sha256))}",
                component.License,
                component.SourceUri,
                Direct: true,
                Optional: false));
        }
    }

    private static void ValidateVendoredIdentity(ReviewedDependencyComponent component)
    {
        if (!IsStableId(component.Id) ||
            !IsStableId(component.PackageManager) ||
            string.IsNullOrWhiteSpace(component.Name) ||
            string.IsNullOrWhiteSpace(component.Version) ||
            !IsCommit(component.Commit) ||
            !IsSha256(component.Sha256) ||
            string.IsNullOrWhiteSpace(component.License) ||
            !Uri.TryCreate(component.SourceUri, UriKind.Absolute, out var sourceUri) ||
            !string.Equals(sourceUri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(component.SelectedBy.Name) ||
            string.IsNullOrWhiteSpace(component.SelectedBy.Version) ||
            !IsCommit(component.SelectedBy.Commit))
        {
            throw new BundleValidationException(
                $"Vendored dependency '{component.Id}' has an invalid reviewed identity.");
        }
    }

    private static string ResolveRepositoryFile(string repositoryRoot, string relativePath, string componentId)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathRooted(relativePath) ||
            relativePath.Contains('\\'))
        {
            throw new BundleValidationException(
                $"Vendored dependency '{componentId}' has an unsafe repository path.");
        }

        var root = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            throw new BundleValidationException(
                $"Vendored dependency '{componentId}' references a missing or unsafe repository file.");
        }

        return path;
    }

    private static bool IsStableId(string value) =>
        value.Length is > 0 and <= 128 &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static bool IsCommit(string value) =>
        value.Length is 40 or 64 &&
        value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsSha256(string value) =>
        value.Length == 64 &&
        value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static async Task ReadNpmAsync(
        string repositoryRoot,
        LicensePolicy policy,
        Dictionary<string, DependencyComponent> components,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(repositoryRoot, "frontend", "package-lock.json");
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("packages", out var packages) ||
            packages.ValueKind != JsonValueKind.Object)
        {
            throw new BundleValidationException("frontend/package-lock.json has no packages map.");
        }

        var directNames = ReadNpmDirectNames(packages);
        foreach (var package in packages.EnumerateObject())
        {
            const string marker = "node_modules/";
            var markerIndex = package.Name.LastIndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                continue;
            }

            var name = package.Name[(markerIndex + marker.Length)..];
            var value = package.Value;
            var version = OptionalString(value, "version")
                ?? throw new BundleValidationException($"npm package '{name}' has no exact version.");
            var rawLicense = OptionalString(value, "license")
                ?? throw new BundleValidationException($"npm package '{name}@{version}' has no license metadata.");
            var license = ResolveLicense(policy, $"npm:{name}@{version}", rawLicense);
            Add(components, new DependencyComponent(
                "npm",
                name,
                version,
                OptionalString(value, "integrity"),
                license,
                OptionalString(value, "resolved"),
                directNames.Contains(name),
                OptionalBoolean(value, "optional")));
        }
    }

    private static HashSet<string> ReadNpmDirectNames(JsonElement packages)
    {
        if (!packages.TryGetProperty(string.Empty, out var root) || root.ValueKind != JsonValueKind.Object)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var propertyName in new[] { "dependencies", "devDependencies", "optionalDependencies" })
        {
            if (!root.TryGetProperty(propertyName, out var dependencies) ||
                dependencies.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var dependency in dependencies.EnumerateObject())
            {
                result.Add(dependency.Name);
            }
        }

        return result;
    }

    private static void ReadNuGet(
        string repositoryRoot,
        LicensePolicy policy,
        Dictionary<string, DependencyComponent> components)
    {
        var globalPackages = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrWhiteSpace(globalPackages))
        {
            globalPackages = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget",
                "packages");
        }

        foreach (var path in EnumerateLockFiles(repositoryRoot))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("dependencies", out var frameworks) ||
                frameworks.ValueKind != JsonValueKind.Object)
            {
                throw new BundleValidationException($"NuGet lock '{path}' has no dependencies map.");
            }

            foreach (var framework in frameworks.EnumerateObject())
            {
                foreach (var package in framework.Value.EnumerateObject())
                {
                    var type = OptionalString(package.Value, "type");
                    if (string.Equals(type, "Project", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var version = OptionalString(package.Value, "resolved");
                    if (version is null)
                    {
                        continue;
                    }

                    var overrideKey = $"nuget:{package.Name}@{version}";
                    var license = policy.Overrides.TryGetValue(overrideKey, out var configured)
                        ? configured
                        : ReadNuGetLicense(globalPackages, package.Name, version);
                    license = ResolveLicense(policy, overrideKey, license);
                    Add(components, new DependencyComponent(
                        "nuget",
                        package.Name,
                        version,
                        OptionalString(package.Value, "contentHash"),
                        license,
                        $"https://api.nuget.org/v3-flatcontainer/{package.Name.ToLowerInvariant()}/{version.ToLowerInvariant()}/{package.Name.ToLowerInvariant()}.{version.ToLowerInvariant()}.nupkg",
                        string.Equals(type, "Direct", StringComparison.OrdinalIgnoreCase),
                        Optional: false));
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateLockFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var path in Directory.EnumerateFiles(directory)
                         .Where(path => IsNuGetLockFileName(Path.GetFileName(path)))
                         .OrderBy(static path => path, StringComparer.Ordinal))
            {
                yield return path;
            }

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(child);
                if (name is ".git" or "node_modules" or "bin" or "obj" or "artifacts" or ".vs")
                {
                    continue;
                }
                pending.Push(child);
            }
        }
    }

    private static bool IsNuGetLockFileName(string fileName) =>
        string.Equals(fileName, "packages.lock.json", StringComparison.Ordinal) ||
        fileName.StartsWith("packages.", StringComparison.Ordinal) &&
        fileName.EndsWith(".lock.json", StringComparison.Ordinal);

    private static string ReadNuGetLicense(string globalPackages, string name, string version)
    {
        var directory = Path.Combine(globalPackages, name.ToLowerInvariant(), version.ToLowerInvariant());
        var nuspecPath = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.nuspec", SearchOption.TopDirectoryOnly).FirstOrDefault()
            : null;
        if (nuspecPath is null)
        {
            throw new BundleValidationException(
                $"NuGet package '{name}@{version}' is not restored; its license cannot be audited.");
        }

        var document = XDocument.Load(nuspecPath, LoadOptions.None);
        var metadata = document.Descendants().FirstOrDefault(static element => element.Name.LocalName == "metadata")
            ?? throw new BundleValidationException($"NuGet package '{name}@{version}' has an invalid nuspec.");
        var license = metadata.Elements().FirstOrDefault(static element => element.Name.LocalName == "license");
        if (license is not null &&
            string.Equals(license.Attribute("type")?.Value, "expression", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(license.Value))
        {
            return license.Value.Trim();
        }

        throw new BundleValidationException(
            $"NuGet package '{name}@{version}' has no SPDX license expression; add a reviewed policy override.");
    }

    private static void Add(
        Dictionary<string, DependencyComponent> components,
        DependencyComponent component)
    {
        var key = $"{component.PackageManager}\0{component.Name}\0{component.Version}";
        if (components.TryGetValue(key, out var existing))
        {
            if (!string.Equals(existing.License, component.License, StringComparison.Ordinal))
            {
                throw new BundleValidationException(
                    $"Dependency '{component.Name}@{component.Version}' has conflicting license metadata.");
            }
            components[key] = existing with
            {
                Direct = existing.Direct || component.Direct,
                Optional = existing.Optional && component.Optional
            };
            return;
        }

        components.Add(key, component);
    }

    private static string ResolveLicense(LicensePolicy policy, string componentKey, string rawLicense)
    {
        if (policy.Overrides.TryGetValue(componentKey, out var overridden))
        {
            return overridden;
        }
        return policy.LicenseAliases.TryGetValue(rawLicense, out var normalized) ? normalized : rawLicense;
    }

    private static void ValidatePolicy(LicensePolicy policy)
    {
        if (policy.SchemaVersion != 1 || policy.AllowedLicenses.Count == 0 || policy.DeniedPrefixes.Count == 0)
        {
            throw new BundleValidationException("License policy is unsupported or empty.");
        }
    }

    private static void ValidateLicense(DependencyComponent component, LicensePolicy policy)
    {
        var allowed = policy.AllowedLicenses.ToHashSet(StringComparer.Ordinal);
        var identifiers = LicenseIdentifiers(component.License).ToArray();
        if (identifiers.Length == 0)
        {
            throw new BundleValidationException(
                $"Dependency '{component.Name}@{component.Version}' has an invalid license expression.");
        }

        foreach (var identifier in identifiers)
        {
            if (policy.DeniedPrefixes.Any(prefix => identifier.StartsWith(prefix, StringComparison.Ordinal)))
            {
                throw new BundleValidationException(
                    $"Dependency '{component.Name}@{component.Version}' uses denied license '{identifier}'.");
            }
            if (!allowed.Contains(identifier))
            {
                throw new BundleValidationException(
                    $"Dependency '{component.Name}@{component.Version}' uses unapproved license '{identifier}'.");
            }
        }
    }

    private static IEnumerable<string> LicenseIdentifiers(string expression)
    {
        var token = new StringBuilder();
        foreach (var character in string.Concat(expression, " "))
        {
            if (char.IsAsciiLetterOrDigit(character) || character is '.' or '+' or '-')
            {
                token.Append(character);
                continue;
            }

            if (token.Length == 0)
            {
                continue;
            }
            var value = token.ToString();
            token.Clear();
            if (value is not ("AND" or "OR" or "WITH"))
            {
                yield return value;
            }
        }
    }

    private static string? OptionalString(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool OptionalBoolean(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.True;

    private sealed record ReviewedDependencyInventory
    {
        public required int SchemaVersion { get; init; }

        public required IReadOnlyList<ReviewedDependencyComponent> Components { get; init; }
    }

    private sealed record ReviewedDependencyComponent
    {
        public required string Id { get; init; }

        public required string PackageManager { get; init; }

        public required string Name { get; init; }

        public required string Version { get; init; }

        public required string Commit { get; init; }

        public required string SourceUri { get; init; }

        public required string SourcePath { get; init; }

        public required string Sha256 { get; init; }

        public required string License { get; init; }

        public required string LicensePath { get; init; }

        public required ReviewedDependencySelector SelectedBy { get; init; }
    }

    private sealed record ReviewedDependencySelector
    {
        public required string Name { get; init; }

        public required string Version { get; init; }

        public required string Commit { get; init; }
    }
}
