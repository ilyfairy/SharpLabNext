using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using SharpLabNext.Catalog;

namespace SharpLabNext.ProfileUpdater;

public sealed record CandidateReleaseMaterial(
    string WorkspaceRoot,
    string LockPath,
    string CatalogPath,
    string VersionsPath,
    IReadOnlyList<CandidateRuntimeProfileMaterial> RuntimeProfiles,
    string ValidationComposePath,
    string ValidationEndpointsPath);

public sealed record CandidateRuntimeProfileMaterial(
    string Id,
    string RelativePath,
    string Path);

public static class CandidateReleaseMaterializer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async Task<CandidateReleaseMaterial> WriteAsync(
        string workspaceRoot,
        CatalogDocument catalogTemplate,
        ReleaseLockDocument candidate,
        string candidateDigest,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        var material = Locate(workspaceRoot);
        var catalog = CreateCatalog(catalogTemplate, candidate, candidateDigest);
        ValidateIdentityClosure(candidate, catalog);
        await AtomicFile.WriteAllBytesAsync(material.LockPath, Serialize(candidate), cancellationToken);
        await AtomicFile.WriteAllBytesAsync(material.CatalogPath, Serialize(catalog), cancellationToken);
        await AtomicFile.WriteAllBytesAsync(
            material.VersionsPath,
            Encoding.UTF8.GetBytes(CreateVersionsProps(candidate)),
            cancellationToken);
        foreach (var runtimeProfile in material.RuntimeProfiles)
        {
            var template = await File.ReadAllTextAsync(runtimeProfile.Path, cancellationToken);
            await AtomicFile.WriteAllBytesAsync(
                runtimeProfile.Path,
                Encoding.UTF8.GetBytes(CreateRuntimeProfile(template, runtimeProfile.Id, candidate)),
                cancellationToken);
        }
        var validation = CreateValidationEndpoints(candidateDigest);
        await AtomicFile.WriteAllBytesAsync(
            material.ValidationComposePath,
            Encoding.UTF8.GetBytes(CreateValidationCompose(validation, candidateDigest)),
            cancellationToken);
        await AtomicFile.WriteAllBytesAsync(
            material.ValidationEndpointsPath,
            Serialize(validation),
            cancellationToken);
        return material;
    }

    public static CandidateReleaseMaterial Locate(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        var root = Path.GetFullPath(workspaceRoot);
        return new CandidateReleaseMaterial(
            root,
            Path.Combine(root, "profiles", "lock.json"),
            Path.Combine(root, "profiles", "catalog", "catalog.json"),
            Path.Combine(root, "profiles", "versions.props"),
            DiscoverRuntimeProfiles(root),
            Path.Combine(root, "artifacts", "profile-candidate", "compose.validation.yaml"),
            Path.Combine(root, "artifacts", "profile-candidate", "endpoints.json"));
    }

    public static CatalogDocument CreateCatalog(
        CatalogDocument template,
        ReleaseLockDocument candidate,
        string candidateDigest)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(candidate);
        var roslynStable = Component(candidate, "roslyn-stable");
        var roslynStableNetFx48 = Component(candidate, "roslyn-stable-netfx48");
        var roslynMain = Component(candidate, "roslyn-main");
        var roslynConstGenerics = Component(candidate, "roslyn-const-generics");
        var fsharp = Component(candidate, "fsharp-stable");
        var gsharp = Component(candidate, "gsharp-stable");
        var peachPie = Component(candidate, "peachpie-stable");
        var jsharp = Component(candidate, "vjc-jsharp20");
        var mobius = Component(candidate, "mobius-ilasm-stable");
        var dotnet10 = Component(candidate, "dotnet-10-linux-x64");
        var dotnet11 = Component(candidate, "dotnet-11-preview-linux-x64");
        var constGenericsRuntime = Component(candidate, "const-generics-linux-x64");
        var netFx48Runtime = Component(candidate, "wine-netfx48-linux-x64");
        var jsharpRuntime = Component(candidate, "wine-jsharp20-linux-x64");
        var net10 = Component(candidate, "net10-ref");
        var net11 = Component(candidate, "net11-preview-ref");
        var constGenericsReference = Component(candidate, "const-generics-ref");
        var jsharpReference = Component(candidate, "jsharp20-ref");
        var defaultArtifacts = Component(candidate, "artifacts-default");
        var constGenericsArtifacts = Component(candidate, "artifacts-const-generics");

        var toolchains = template.Toolchains.Select(toolchain => toolchain.Id switch
        {
            "roslyn-stable" => toolchain with
            {
                DisplayName = $"Roslyn Stable {roslynStable.ResolvedVersion}",
                ResolvedVersion = roslynStable.ResolvedVersion
            },
            "roslyn-stable-netfx48" => toolchain with
            {
                DisplayName = $"Roslyn Stable {roslynStableNetFx48.ResolvedVersion} / .NET Framework 4.8",
                ResolvedVersion = roslynStableNetFx48.ResolvedVersion
            },
            "roslyn-main" => toolchain with
            {
                DisplayName = $"Roslyn Main {roslynMain.ResolvedVersion} ({RequiredCommit(roslynMain, "roslyn-main")[..12]})",
                ResolvedVersion = roslynMain.ResolvedVersion
            },
            "roslyn-const-generics" => toolchain with
            {
                DisplayName = $"Roslyn Const Generics {roslynConstGenerics.ResolvedVersion}",
                ResolvedVersion = roslynConstGenerics.ResolvedVersion
            },
            "fsharp-stable" => toolchain with
            {
                DisplayName = $"F# Stable {fsharp.ResolvedVersion}",
                ResolvedVersion = fsharp.ResolvedVersion
            },
            "gsharp-stable" => toolchain with
            {
                DisplayName = $"G# Stable {gsharp.ResolvedVersion}",
                ResolvedVersion = gsharp.ResolvedVersion
            },
            "peachpie-stable" => toolchain with
            {
                DisplayName = $"PeachPie Stable {peachPie.ResolvedVersion}",
                ResolvedVersion = peachPie.ResolvedVersion
            },
            "vjc-jsharp20" => toolchain with
            {
                DisplayName = $"Visual J# {jsharp.ResolvedVersion}",
                ResolvedVersion = jsharp.ResolvedVersion
            },
            "mobius-ilasm-stable" => toolchain with
            {
                DisplayName = $"Mobius ILAsm Stable {mobius.ResolvedVersion}",
                ResolvedVersion = mobius.ResolvedVersion
            },
            _ => toolchain
        }).ToArray();
        var referenceSets = template.ReferenceSets.Select(referenceSet =>
        {
            var resolved = candidate.Components.TryGetValue(referenceSet.Id, out var component) &&
                           string.Equals(component.Kind, "reference-set", StringComparison.Ordinal)
                ? referenceSet with
                {
                    Digest = ReferenceSetIdentityResolver.ResolveLockedDigest(component, referenceSet.Id)
                }
                : referenceSet;
            return referenceSet.Id switch
            {
                "net10-ref" => resolved with { DisplayName = ".NET 10" },
                "net11-preview-ref" => resolved with { DisplayName = ".NET Main" },
                "const-generics-ref" => resolved with { DisplayName = "Const Generics" },
                "netfx48-managed-ref" => resolved with
                {
                    DisplayName = ".NET Framework 4.8 Reference Assemblies"
                },
                "jsharp20-ref" => resolved with
                {
                    DisplayName = "Visual J# 2.0 / CLR 2.0 Reference Assemblies"
                },
                _ => resolved
            };
        }).ToArray();
        var runtimes = template.Runtimes.Select(runtime => runtime.Id switch
        {
            "dotnet-10-linux-x64" => runtime with
            {
                DisplayName = ".NET 10",
                ResolvedVersion = dotnet10.ResolvedVersion,
                RuntimeCommit = dotnet10.Commit ?? runtime.RuntimeCommit,
                JitVersion = dotnet10.JitCommit is null ? runtime.JitVersion : dotnet10.ResolvedVersion,
                JitCommit = dotnet10.JitCommit ?? runtime.JitCommit,
                RuntimeImageId = dotnet10.ImageId ?? runtime.RuntimeImageId
            },
            "dotnet-11-preview-linux-x64" => runtime with
            {
                DisplayName = ".NET Main",
                ResolvedVersion = dotnet11.ResolvedVersion,
                RuntimeCommit = dotnet11.Commit ?? runtime.RuntimeCommit,
                JitVersion = dotnet11.JitCommit is null ? runtime.JitVersion : dotnet11.ResolvedVersion,
                JitCommit = dotnet11.JitCommit ?? runtime.JitCommit,
                RuntimeImageId = dotnet11.ImageId ?? runtime.RuntimeImageId
            },
            "const-generics-linux-x64" => runtime with
            {
                DisplayName = $"Const Generics Runtime {constGenericsRuntime.ResolvedVersion}",
                ResolvedVersion = constGenericsRuntime.ResolvedVersion,
                RuntimeCommit = constGenericsRuntime.Commit ?? runtime.RuntimeCommit,
                JitVersion = constGenericsRuntime.JitCommit is null ? runtime.JitVersion : constGenericsRuntime.ResolvedVersion,
                JitCommit = constGenericsRuntime.JitCommit ?? runtime.JitCommit,
                RuntimeImageId = constGenericsRuntime.ImageId ?? runtime.RuntimeImageId
            },
            "wine-netfx48-linux-x64" => runtime with
            {
                ResolvedVersion = netFx48Runtime.ResolvedVersion,
                RuntimeImageId = netFx48Runtime.ImageId ?? runtime.RuntimeImageId
            },
            "wine-jsharp20-linux-x64" => runtime with
            {
                DisplayName = "Visual J# 2.0 / CLR 2.0 / Wine 9.0",
                ResolvedVersion = jsharpRuntime.ResolvedVersion,
                RuntimeCommit = jsharpRuntime.Commit ?? runtime.RuntimeCommit,
                JitVersion = jsharpRuntime.JitCommit is null ? runtime.JitVersion : jsharpRuntime.ResolvedVersion,
                JitCommit = jsharpRuntime.JitCommit ?? runtime.JitCommit,
                RuntimeImageId = jsharpRuntime.ImageId ?? runtime.RuntimeImageId
            },
            _ => runtime
        }).ToArray();
        var processors = template.ArtifactProcessors.Select(processor => processor.Id switch
        {
            "artifacts-default" => processor with
            {
                DisplayName = $"ILSpy {Component(candidate, "ilspy").ResolvedVersion} and ILVerify {Component(candidate, "dotnet-ilverify").ResolvedVersion}",
                ResolvedVersion = defaultArtifacts.ResolvedVersion
            },
            "il-assembler" => processor with
            {
                DisplayName = $"Public IL Assembler {mobius.ResolvedVersion}",
                ResolvedVersion = mobius.ResolvedVersion
            },
            "artifacts-const-generics" => processor with
            {
                DisplayName = $"Const Generics Artifact Processor {constGenericsArtifacts.ResolvedVersion}",
                ResolvedVersion = constGenericsArtifacts.ResolvedVersion
            },
            _ => processor
        }).ToArray();

        return template with
        {
            Revision = $"{candidate.ReleaseId}-h{DigestHex(candidateDigest)[..12]}",
            ReleaseId = candidate.ReleaseId,
            Toolchains = toolchains,
            ReferenceSets = referenceSets,
            Runtimes = runtimes,
            ArtifactProcessors = processors
        };
    }

    public static string CreateVersionsProps(ReleaseLockDocument candidate)
    {
        var roslynMain = Component(candidate, "roslyn-main");
        var constGenericsIlSpy = Component(candidate, "const-generics-ilspy-source");
        var constGenericsRuntime = Component(candidate, "const-generics-runtime-source");
        var constGenericsVerificationRevision = ConstGenericsVerificationRevision(
            candidate,
            constGenericsIlSpy,
            constGenericsRuntime);
        var properties = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["RoslynStableVersion"] = Version(candidate, "roslyn-stable"),
            ["RoslynMainVersion"] = roslynMain.ResolvedVersion,
            ["RoslynMainCommit"] = RequiredCommit(roslynMain, "roslyn-main"),
            ["RoslynMainArchiveUrl"] = Required(roslynMain.SourceUri, "roslyn-main.sourceUri"),
            ["RoslynMainArchiveSha256"] = DigestHex(Required(roslynMain.Digest, "roslyn-main.digest")),
            ["FSharpCompilerServiceVersion"] = Version(candidate, "fsharp-stable"),
            ["FSharpCoreVersion"] = Version(candidate, "fsharp-core"),
            ["PeachPieVersion"] = Version(candidate, "peachpie-stable"),
            ["PeachPieCommit"] = RequiredCommit(Component(candidate, "peachpie-stable"), "peachpie-stable"),
            ["ILSpyVersion"] = Version(candidate, "ilspy"),
            ["ILVerificationVersion"] = Version(candidate, "dotnet-ilverify"),
            ["MobiusILAsmVersion"] = Version(candidate, "mobius-ilasm-stable"),
            ["DotNet10RuntimeVersion"] = Version(candidate, "dotnet-10-linux-x64"),
            ["DotNet10RuntimeCommit"] = RequiredCommit(Component(candidate, "dotnet-10-linux-x64"), "dotnet-10-linux-x64"),
            ["DotNet10JitCommit"] = RequiredJitCommit(Component(candidate, "dotnet-10-linux-x64"), "dotnet-10-linux-x64"),
            ["DotNet11RuntimeVersion"] = Version(candidate, "dotnet-11-preview-linux-x64"),
            ["DotNet11RuntimeCommit"] = RequiredCommit(Component(candidate, "dotnet-11-preview-linux-x64"), "dotnet-11-preview-linux-x64"),
            ["DotNet11JitCommit"] = RequiredJitCommit(Component(candidate, "dotnet-11-preview-linux-x64"), "dotnet-11-preview-linux-x64"),
            ["Net10ReferencePackVersion"] = Version(candidate, "net10-ref"),
            ["Net11ReferencePackVersion"] = Version(candidate, "net11-preview-ref"),
            ["ConstGenericsIlSpyCommit"] = RequiredCommit(constGenericsIlSpy, "const-generics-ilspy-source"),
            ["ConstGenericsRuntimeCommit"] = RequiredCommit(constGenericsRuntime, "const-generics-runtime-source"),
            ["ConstGenericsReferenceVersion"] = Version(candidate, "const-generics-ref"),
            ["ConstGenericsIlSpyProcessorVersion"] = "$(ConstGenericsIlSpyCommit)",
            ["ConstGenericsVerificationProcessorVersion"] =
                $"$(ConstGenericsRuntimeCommit)+{constGenericsVerificationRevision}"
        };
        var document = new XDocument(
            new XElement("Project",
                new XElement("PropertyGroup", properties.Select(pair => new XElement(pair.Key, pair.Value)))));
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            OmitXmlDeclaration = true,
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace
        };
        var builder = new StringBuilder();
        using (var writer = XmlWriter.Create(builder, settings))
            document.Save(writer);
        return builder.Append('\n').ToString();
    }

    public static string CreateRuntimeProfile(
        string templateJson,
        string expectedId,
        ReleaseLockDocument candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedId);
        ArgumentNullException.ThrowIfNull(candidate);
        var profile = JsonNode.Parse(templateJson) as JsonObject
            ?? throw new ProfileUpdateValidationException(
                $"Runtime profile '{expectedId}' must be a JSON object.");
        var id = RequiredJsonString(profile, "id", expectedId);
        if (!string.Equals(id, expectedId, StringComparison.Ordinal))
        {
            throw new ProfileUpdateValidationException(
                $"Runtime profile '{expectedId}' contains unexpected ID '{id}'.");
        }

        var runtime = Component(candidate, id);
        if (!string.Equals(runtime.Kind, "runtime", StringComparison.Ordinal))
        {
            throw new ProfileUpdateValidationException(
                $"Candidate lock component '{id}' must have kind 'runtime', actual '{runtime.Kind}'.");
        }
        var runtimeVersion = Required(runtime.ResolvedVersion, $"{id}.resolvedVersion");
        var image = RetagImage(RequiredJsonString(profile, "image", id), candidate.ReleaseId);
        profile["image"] = image;
        profile["runtimeVersion"] = runtimeVersion;
        if (!string.IsNullOrWhiteSpace(runtime.Commit))
            profile["runtimeCommit"] = runtime.Commit;
        if (!string.IsNullOrWhiteSpace(runtime.JitCommit))
        {
            profile["jitVersion"] = runtimeVersion;
            profile["jitCommit"] = runtime.JitCommit;
        }
        profile["runtimeImageId"] = image;
        UpdateCoreClrFrameworkVersion(profile, runtimeVersion);
        return profile.ToJsonString(JsonOptions) + "\n";
    }

    private static List<CandidateRuntimeProfileMaterial> DiscoverRuntimeProfiles(string workspaceRoot)
    {
        var runtimeDirectory = Path.Combine(workspaceRoot, "profiles", "runtimes");
        if (!Directory.Exists(runtimeDirectory))
        {
            throw new ProfileUpdateValidationException(
                $"Runtime profile directory '{runtimeDirectory}' does not exist.");
        }

        var profiles = new List<CandidateRuntimeProfileMaterial>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(runtimeDirectory, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal))
        {
            JsonObject profile;
            try
            {
                profile = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                    ?? throw new JsonException("The root value is not an object.");
            }
            catch (JsonException exception)
            {
                throw new ProfileUpdateValidationException(
                    $"Runtime profile '{path}' is invalid JSON: {exception.Message}");
            }

            var id = RequiredJsonString(profile, "id", Path.GetFileName(path));
            var fileId = Path.GetFileNameWithoutExtension(path);
            if (!string.Equals(id, fileId, StringComparison.Ordinal))
            {
                throw new ProfileUpdateValidationException(
                    $"Runtime profile file '{Path.GetFileName(path)}' contains ID '{id}'.");
            }
            if (!ids.Add(id))
                throw new ProfileUpdateValidationException($"Runtime profile ID '{id}' is duplicated.");

            profiles.Add(new CandidateRuntimeProfileMaterial(
                id,
                Path.Combine("profiles", "runtimes", Path.GetFileName(path)),
                Path.GetFullPath(path)));
        }

        if (profiles.Count == 0)
            throw new ProfileUpdateValidationException("No active runtime profiles were found.");
        return profiles;
    }

    private static void UpdateCoreClrFrameworkVersion(JsonObject profile, string runtimeVersion)
    {
        if (profile["acceptedFrameworks"] is not JsonArray frameworks)
            return;

        foreach (var framework in frameworks.OfType<JsonObject>())
        {
            if (framework["name"] is JsonValue name &&
                name.TryGetValue<string>(out var value) &&
                string.Equals(value, "Microsoft.NETCore.App", StringComparison.Ordinal) &&
                framework.ContainsKey("exactVersion"))
            {
                framework["exactVersion"] = runtimeVersion;
            }
        }
    }

    public static void ValidateIdentityClosure(ReleaseLockDocument candidate, CatalogDocument catalog)
    {
        RequireEqual(candidate.ReleaseId, catalog.ReleaseId, "catalog.releaseId");
        RequireToolchain(candidate, catalog, "roslyn-stable", "roslyn-stable");
        RequireDerivedToolchain(candidate, "roslyn-stable", "roslyn-stable-netfx48");
        RequireToolchain(candidate, catalog, "roslyn-stable-netfx48", "roslyn-stable-netfx48");
        RequireToolchain(candidate, catalog, "roslyn-main", "roslyn-main");
        RequireToolchain(candidate, catalog, "roslyn-const-generics", "roslyn-const-generics");
        RequireToolchain(candidate, catalog, "fsharp-stable", "fsharp-stable");
        RequireToolchain(candidate, catalog, "gsharp-stable", "gsharp-stable");
        // The G# worker carries its legacy compiler side-by-side with the
        // selectable stable toolchain.  It is therefore a locked worker
        // dependency, not necessarily a catalog toolchain.  Validate the
        // component unconditionally and compare it to a catalog entry (or a
        // legacy alias) when a catalog chooses to expose one.
        RequireOptionalToolchain(candidate, catalog, "gsharp-legacy-0.3.8", "gsharp-legacy-0.3.8");
        RequireToolchain(candidate, catalog, "peachpie-stable", "peachpie-stable");
        RequireToolchain(candidate, catalog, "msvc-cppcli-netfx48", "msvc-cppcli-netfx48");
        RequireToolchain(candidate, catalog, "vjc-jsharp20", "vjc-jsharp20");
        RequireToolchain(candidate, catalog, "mobius-ilasm-stable", "mobius-ilasm-stable");
        RequireToolchain(candidate, catalog, "minilang-stable", "minilang-stable");
        RequireRuntime(candidate, catalog, "dotnet-10-linux-x64");
        RequireRuntime(candidate, catalog, "dotnet-11-preview-linux-x64");
        RequireRuntime(candidate, catalog, "const-generics-linux-x64");
        RequireRuntime(candidate, catalog, "wine-netfx48-linux-x64");
        RequireRuntime(candidate, catalog, "wine-jsharp20-linux-x64");
        ValidateRuntimeIdentityClosure(candidate, catalog);
        RequireProcessor(candidate, catalog, "artifacts-default", "artifacts-default");
        RequireProcessor(candidate, catalog, "artifacts-const-generics", "artifacts-const-generics");
        RequireProcessor(candidate, catalog, "mobius-ilasm-stable", "il-assembler");
        _ = ReferenceSetIdentityResolver.ResolveExpectedDigests(catalog, candidate);
    }

    public static CandidateValidationEndpoints CreateValidationEndpoints(string candidateDigest)
    {
        var offset = Convert.ToInt32(DigestHex(candidateDigest)[..3], 16) % 500 * 20;
        var basePort = 20000 + offset;
        return new CandidateValidationEndpoints
        {
            Gateway = $"http://127.0.0.1:{basePort}",
            Services = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["roslyn-stable"] = $"http://127.0.0.1:{basePort + 1}",
                ["roslyn-stable-netfx48"] = $"http://127.0.0.1:{basePort + 14}",
                ["roslyn-main"] = $"http://127.0.0.1:{basePort + 2}",
                ["fsharp-stable"] = $"http://127.0.0.1:{basePort + 3}",
                ["mobius-ilasm-stable"] = $"http://127.0.0.1:{basePort + 4}",
                ["artifacts-default"] = $"http://127.0.0.1:{basePort + 5}",
                ["runtime-supervisor"] = $"http://127.0.0.1:{basePort + 6}",
                ["roslyn-const-generics"] = $"http://127.0.0.1:{basePort + 7}",
                ["minilang-stable"] = $"http://127.0.0.1:{basePort + 8}",
                ["artifacts-const-generics"] = $"http://127.0.0.1:{basePort + 9}",
                ["il-assembler"] = $"http://127.0.0.1:{basePort + 10}",
                ["gsharp-stable"] = $"http://127.0.0.1:{basePort + 11}",
                ["peachpie-stable"] = $"http://127.0.0.1:{basePort + 12}",
                ["msvc-cppcli-netfx48"] = $"http://127.0.0.1:{basePort + 13}",
                ["vjc-jsharp20"] = $"http://127.0.0.1:{basePort + 15}"
            }
        };
    }

    public static string CreateValidationCompose(
        CandidateValidationEndpoints endpoints,
        string candidateDigest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateDigest);
        var services = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["worker-roslyn-stable"] = endpoints.Services["roslyn-stable"],
            ["worker-roslyn-netfx48"] = endpoints.Services["roslyn-stable-netfx48"],
            ["worker-roslyn-main"] = endpoints.Services["roslyn-main"],
            ["worker-roslyn-const-generics"] = endpoints.Services["roslyn-const-generics"],
            ["worker-fsharp"] = endpoints.Services["fsharp-stable"],
            ["worker-gsharp"] = endpoints.Services["gsharp-stable"],
            ["worker-peachpie"] = endpoints.Services["peachpie-stable"],
            ["worker-cppcli"] = endpoints.Services["msvc-cppcli-netfx48"],
            ["worker-jsharp"] = endpoints.Services["vjc-jsharp20"],
            ["worker-il"] = endpoints.Services["mobius-ilasm-stable"],
            ["worker-minilang"] = endpoints.Services["minilang-stable"],
            ["worker-artifacts-default"] = endpoints.Services["artifacts-default"],
            ["worker-artifacts-const-generics"] = endpoints.Services["artifacts-const-generics"],
            ["worker-artifacts-il-assembler"] = endpoints.Services["il-assembler"],
            ["runtime-supervisor"] = endpoints.Services["runtime-supervisor"]
        };
        var builder = new StringBuilder("# Generated for candidate identity validation only.\nservices:\n");
        foreach (var service in services)
        {
            var port = new Uri(service.Value).Port;
            builder.Append("  ").Append(service.Key).Append(":\n")
                .Append("    ports:\n")
                .Append("      - \"127.0.0.1:").Append(port).Append(":8080\"\n");
            if (service.Key == "runtime-supervisor")
            {
                builder.Append("    environment:\n")
                    .Append("      RuntimeSupervisor__ResourceScope: \"")
                    .Append(candidateDigest)
                    .Append("\"\n");
            }
        }
        return builder.ToString();
    }

    private static void RequireToolchain(
        ReleaseLockDocument candidate,
        CatalogDocument catalog,
        string componentId,
        string catalogId) => RequireEqual(
            Version(candidate, componentId),
            catalog.Toolchains.Single(item => item.Id == catalogId).ResolvedVersion,
            $"catalog.toolchains[{catalogId}].resolvedVersion");

    private static void RequireOptionalToolchain(
        ReleaseLockDocument candidate,
        CatalogDocument catalog,
        string componentId,
        string catalogId)
    {
        var component = Component(candidate, componentId);
        if (!string.Equals(component.Kind, "toolchain", StringComparison.Ordinal))
        {
            throw new ProfileUpdateValidationException(
                $"Candidate lock component '{componentId}' must have kind 'toolchain', actual '{component.Kind}'.");
        }
        if (string.IsNullOrWhiteSpace(component.ResolvedVersion))
        {
            throw new ProfileUpdateValidationException(
                $"Candidate lock component '{componentId}' must declare a resolved version.");
        }

        var toolchain = catalog.Toolchains.SingleOrDefault(item =>
                string.Equals(item.Id, catalogId, StringComparison.Ordinal))
            ?? catalog.Toolchains.SingleOrDefault(item =>
                item.LegacyAliases.Any(alias => string.Equals(alias, catalogId, StringComparison.Ordinal)));
        if (toolchain is not null)
        {
            RequireEqual(
                component.ResolvedVersion,
                toolchain.ResolvedVersion,
                $"catalog.toolchains[{catalogId}].resolvedVersion");
        }
    }

    private static void RequireDerivedToolchain(
        ReleaseLockDocument candidate,
        string sourceComponentId,
        string derivedComponentId)
    {
        var source = Component(candidate, sourceComponentId);
        var derived = Component(candidate, derivedComponentId);
        var expected = source with { PatchDigest = null, ImageId = null };
        if (derived != expected)
        {
            throw new ProfileUpdateValidationException(
                $"Candidate derived toolchain '{derivedComponentId}' must copy the complete input identity of '{sourceComponentId}'.");
        }
    }

    private static void RequireRuntime(
        ReleaseLockDocument candidate,
        CatalogDocument catalog,
        string id) => RequireEqual(
            Version(candidate, id),
            catalog.Runtimes.Single(item => item.Id == id).ResolvedVersion,
            $"catalog.runtimes[{id}].resolvedVersion");

    /// <summary>
    /// Verifies that every runtime which could be selected by a candidate has
    /// a closed identity between the catalog, the release lock, and the
    /// eventual runtime image. Matrix-generated profiles deliberately use a
    /// <c>payload-sha512:</c> marker while they are blocked; that marker is a
    /// payload provenance hint, not a Git commit and must never cross the
    /// promotion boundary as <c>RuntimeCommit</c> or <c>JitCommit</c>.
    /// </summary>
    private static void ValidateRuntimeIdentityClosure(
        ReleaseLockDocument candidate,
        CatalogDocument catalog)
    {
        foreach (var runtime in catalog.Runtimes.Where(static runtime => runtime.Availability.IsSelectable))
        {
            if (!candidate.Components.TryGetValue(runtime.Id, out var component))
            {
                throw new ProfileUpdateValidationException(
                    $"Selectable runtime '{runtime.Id}' has no corresponding lock component; " +
                    "a matrix payload must be materialized into the release lock before promotion.");
            }

            if (!string.Equals(component.Kind, "runtime", StringComparison.Ordinal))
            {
                throw new ProfileUpdateValidationException(
                    $"Selectable runtime '{runtime.Id}' is backed by lock component kind '{component.Kind}', " +
                    "not 'runtime'.");
            }

            if (!RequiresCommitIdentity(runtime))
            {
                // Framework/Wine and Mono runtimes are operator payloads. Their
                // identity is the immutable image/archive digest, never a
                // CoreCLR Git commit (and never a payload-sha512 marker).
                if (runtime.RuntimeCommit is not null || runtime.JitCommit is not null)
                {
                    throw new ProfileUpdateValidationException(
                        $"Selectable operator runtime '{runtime.Id}' must not declare RuntimeCommit/JitCommit.");
                }

                if (!IsSha256(component.Digest))
                {
                    throw new ProfileUpdateValidationException(
                        $"Selectable operator runtime '{runtime.Id}' must have an immutable sha256 lock digest; " +
                        "a payload-sha512 marker is not an operator image identity.");
                }

                continue;
            }

            var expectedRuntimeCommit = RequiredCommit(component, $"{runtime.Id}.commit");
            var expectedJitCommit = RequiredJitCommit(component, $"{runtime.Id}.jitCommit");
            RequireCommitIdentity(expectedRuntimeCommit, $"{runtime.Id}.commit");
            RequireCommitIdentity(expectedJitCommit, $"{runtime.Id}.jitCommit");
            RequireCommitIdentity(runtime.RuntimeCommit, $"catalog.runtimes[{runtime.Id}].runtimeCommit");
            RequireCommitIdentity(runtime.JitCommit, $"catalog.runtimes[{runtime.Id}].jitCommit");
            RequireEqual(
                expectedRuntimeCommit,
                runtime.RuntimeCommit!,
                $"catalog.runtimes[{runtime.Id}].runtimeCommit",
                ignoreCase: true);
            RequireEqual(
                expectedJitCommit,
                runtime.JitCommit!,
                $"catalog.runtimes[{runtime.Id}].jitCommit",
                ignoreCase: true);
        }
    }

    private static bool RequiresCommitIdentity(RuntimeManifest runtime) =>
        !string.Equals(runtime.Family, "netfx-clr-wine", StringComparison.Ordinal) &&
        !string.Equals(runtime.Family, "mono", StringComparison.Ordinal);

    private static void RequireCommitIdentity(string? value, string field)
    {
        if (!IsCommit(value))
        {
            throw new ProfileUpdateValidationException(
                $"Runtime identity '{field}' must be a 40- or 64-character hexadecimal commit; " +
                "payload-sha512 markers must be materialized into the lock before promotion.");
        }
    }

    private static bool IsCommit(string? value) =>
        value is { Length: 40 or 64 } &&
        value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsSha256(string? value) =>
        value is { Length: 71 } &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value[7..].All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void RequireProcessor(
        ReleaseLockDocument candidate,
        CatalogDocument catalog,
        string componentId,
        string catalogId) => RequireEqual(
            Version(candidate, componentId),
            catalog.ArtifactProcessors.Single(item => item.Id == catalogId).ResolvedVersion,
            $"catalog.artifactProcessors[{catalogId}].resolvedVersion");

    private static void RequireEqual(
        string expected,
        string actual,
        string field,
        bool ignoreCase = false)
    {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(expected, actual, comparison))
            throw new ProfileUpdateValidationException(
                $"Candidate identity mismatch for {field}: expected '{expected}', actual '{actual}'.");
    }

    private static LockedComponent Component(ReleaseLockDocument candidate, string id) =>
        candidate.Components.TryGetValue(id, out var component)
            ? component
            : throw new ProfileUpdateValidationException($"Candidate lock is missing component '{id}'.");

    private static string Version(ReleaseLockDocument candidate, string id) => Component(candidate, id).ResolvedVersion;

    private static string RequiredCommit(LockedComponent component, string id) =>
        Required(component.Commit, $"{id}.commit");

    private static string RequiredJitCommit(LockedComponent component, string id) =>
        Required(component.JitCommit, $"{id}.jitCommit");

    private static string ConstGenericsVerificationRevision(
        ReleaseLockDocument candidate,
        LockedComponent ilSpySource,
        LockedComponent runtimeSource)
    {
        var ilSpyCommit = RequiredCommit(ilSpySource, "const-generics-ilspy-source");
        var runtimeCommit = RequiredCommit(runtimeSource, "const-generics-runtime-source");
        var componentVersion = Version(candidate, "artifacts-const-generics");
        var prefix = $"{ilSpyCommit[..12]}-{runtimeCommit[..12]}-";
        if (!componentVersion.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ProfileUpdateValidationException(
                "Candidate lock component 'artifacts-const-generics' does not match its ILSpy and runtime source commits.");
        }
        var revision = componentVersion[prefix.Length..];
        if (revision.Length == 0 ||
            !(char.IsAsciiLetterLower(revision[0]) || char.IsAsciiDigit(revision[0])) ||
            revision.Any(static character =>
                !(char.IsAsciiLetterLower(character) ||
                  char.IsAsciiDigit(character) ||
                  character is '.' or '-')))
        {
            throw new ProfileUpdateValidationException(
                "Candidate lock component 'artifacts-const-generics' has an invalid processor revision.");
        }
        return revision;
    }

    private static string PackageDigest(LockedComponent component, string id) =>
        !string.IsNullOrWhiteSpace(component.PackageContentHash)
            ? component.PackageContentHash
            : !string.IsNullOrWhiteSpace(component.Sha512)
                ? $"sha512:{component.Sha512}"
                : throw new ProfileUpdateValidationException(
                    $"Candidate lock component '{id}' has no package digest.");

    private static string Required(string? value, string field) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ProfileUpdateValidationException($"Candidate lock field '{field}' is required.");

    private static string RequiredJsonString(
        JsonObject document,
        string property,
        string profileId) =>
        document[property] is JsonValue value &&
        value.TryGetValue<string>(out var text) &&
        !string.IsNullOrWhiteSpace(text)
            ? text
            : throw new ProfileUpdateValidationException(
                $"Runtime profile '{profileId}' field '{property}' is required.");

    private static string RetagImage(string image, string releaseId)
    {
        var tag = Required(releaseId, "releaseId");
        if (image.Contains('@', StringComparison.Ordinal))
        {
            throw new ProfileUpdateValidationException(
                "ConstGenerics development runtime profile must use a repository tag, not a digest.");
        }
        var lastSlash = image.LastIndexOf('/');
        var lastColon = image.LastIndexOf(':');
        var repository = lastColon > lastSlash ? image[..lastColon] : image;
        return $"{repository}:{tag}";
    }

    private static string DigestHex(string digest)
    {
        if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71)
        {
            throw new ProfileUpdateValidationException($"'{digest}' is not a SHA-256 digest.");
        }
        foreach (var character in digest.AsSpan(7))
        {
            if (!char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character))
                throw new ProfileUpdateValidationException($"'{digest}' is not a SHA-256 digest.");
        }
        return digest[7..];
    }

    private static byte[] Serialize<T>(T value) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions) + "\n");
}

public sealed record CandidateValidationEndpoints
{
    public required string Gateway { get; init; }
    public required IReadOnlyDictionary<string, string> Services { get; init; }
}
