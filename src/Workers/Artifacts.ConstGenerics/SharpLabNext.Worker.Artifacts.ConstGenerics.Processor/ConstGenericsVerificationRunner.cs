using System.Collections;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using SharpLabNext.Worker.Artifacts.ConstGenerics.Protocol;

namespace SharpLabNext.Worker.Artifacts.ConstGenerics.Processing;

internal static class ConstGenericsVerificationRunner
{
    public static Task<ConstGenericsProcessorResponse> VerifyAsync(ConstGenericsProcessorRequest request, CancellationToken cancellationToken)
    {
        var verificationAssemblyPath = Path.Combine(AppContext.BaseDirectory, "ILVerification.dll");
        if (!File.Exists(verificationAssemblyPath))
            throw new InvalidOperationException("The matching ILVerification assembly is unavailable.");
        var verificationAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(verificationAssemblyPath);
        var resolverInterface = verificationAssembly.GetType("ILVerify.IResolver", throwOnError: true)!;
        using var resolverState = new VerificationResolverState(Path.GetDirectoryName(request.AssemblyPath)!, request.ReferenceRoots);
        var resolver = DispatchProxy.Create(resolverInterface, typeof(ResolverDispatchProxy));
        ((ResolverDispatchProxy)resolver).Configure(resolverState);
        var verifierType = verificationAssembly.GetType("ILVerify.Verifier", throwOnError: true)!;
        var verifier = CreateVerifier(verifierType, resolverInterface, resolver);
        SetSystemModule(verifierType, verifier, request.SystemModuleName);

        using var stream = File.OpenRead(request.AssemblyPath);
        using var peReader = new PEReader(stream, PEStreamOptions.PrefetchEntireImage);
        if (!peReader.HasMetadata)
            throw new BadImageFormatException();
        var metadata = peReader.GetMetadataReader();
        var verifyMethod = verifierType.GetMethods(BindingFlags.Public | BindingFlags.Instance).Single(method =>
                string.Equals(method.Name, "Verify", StringComparison.Ordinal) &&
                method.GetParameters() is [{ ParameterType: var parameterType }] &&
                parameterType == typeof(PEReader));
        var findings = new List<ConstGenericsProcessorFinding>();
        var truncated = false;
        try
        {
            var results = (IEnumerable)(verifyMethod.Invoke(verifier, [peReader]) ?? throw new InvalidOperationException("The verifier returned no result stream."));
            foreach (var result in results)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (findings.Count >= request.MaxFindings)
                {
                    truncated = true;
                    break;
                }
                if (result is not null)
                    findings.Add(ToFinding(result, metadata));
            }
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }

        var outcome = truncated
            ? ConstGenericsProcessorOutcome.LimitExceeded : findings.Count == 0
                ? ConstGenericsProcessorOutcome.Succeeded : ConstGenericsProcessorOutcome.Findings;
        return Task.FromResult(ConstGenericsProcessorEngine.Response(outcome, "ilverification-const-generics", ConstGenericsProcessorProtocol.VerificationProcessorVersion, "application/vnd.sharplabnext.il-verification+json", findings: findings, truncated: truncated, publicMessage: truncated ? "Verification findings exceeded the configured limit." : null));
    }

    private static object CreateVerifier(Type verifierType, Type resolverInterface, object resolver)
    {
        var constructors = verifierType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        var withOptions = constructors.FirstOrDefault(constructor =>
            constructor.GetParameters() is [{ ParameterType: var first }, { ParameterType: var second }] &&
            first == resolverInterface && second.Name == "VerifierOptions");
        if (withOptions is not null)
        {
            var optionsType = withOptions.GetParameters()[1].ParameterType;
            var options = Activator.CreateInstance(optionsType) ?? throw new InvalidOperationException("The verifier options could not be created.");
            SetProperty(optionsType, options, "IncludeMetadataTokensInErrorMessages", true);
            SetProperty(optionsType, options, "SanityChecks", true);
            return withOptions.Invoke([resolver, options]);
        }
        var basic = constructors.Single(constructor =>
            constructor.GetParameters() is [{ ParameterType: var first }] && first == resolverInterface);
        return basic.Invoke([resolver]);
    }

    private static void SetSystemModule(Type verifierType, object verifier, string? systemModuleName)
    {
        if (string.IsNullOrWhiteSpace(systemModuleName))
            return;
        var method = verifierType.GetMethods(BindingFlags.Public | BindingFlags.Instance).Single(candidate => string.Equals(candidate.Name, "SetSystemModuleName", StringComparison.Ordinal) && candidate.GetParameters().Length == 1);
        var parameterType = method.GetParameters()[0].ParameterType;
        object name;
        if (parameterType == typeof(AssemblyName))
        {
            name = new AssemblyName(systemModuleName);
        }
        else
        {
            var constructor = parameterType.GetConstructors(BindingFlags.Public | BindingFlags.Instance).SingleOrDefault(candidate => candidate.GetParameters() is
                [
                    { ParameterType: var first },
                    { ParameterType: var second },
                    { ParameterType: var third },
                    { ParameterType: var fourth },
                    { ParameterType: var fifth }
                ] &&
                first == typeof(string) &&
                second == typeof(Version) &&
                third == typeof(string) &&
                fourth.IsEnum &&
                fifth.IsValueType);
            if (constructor is null)
                throw new InvalidOperationException("The verifier assembly-name API is unsupported.");
            var parameters = constructor.GetParameters();
            name = constructor.Invoke(
            [
                systemModuleName,
                new Version(0, 0, 0, 0),
                string.Empty,
                Enum.ToObject(parameters[3].ParameterType, 0),
                Activator.CreateInstance(parameters[4].ParameterType)
            ]);
        }
        try
        {
            method.Invoke(verifier, [name]);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }

    private static ConstGenericsProcessorFinding ToFinding(object result, MetadataReader metadata)
    {
        var type = result.GetType();
        var code = type.GetProperty("Code")?.GetValue(result)?.ToString() ?? "VerificationError";
        var message = type.GetProperty("Message")?.GetValue(result)?.ToString() ?? "IL verification failed.";
        string? typeName = null;
        string? methodName = null;
        int? token = null;
        if (type.GetProperty("Type")?.GetValue(result) is TypeDefinitionHandle typeHandle && !typeHandle.IsNil)
        {
            var definition = metadata.GetTypeDefinition(typeHandle);
            typeName = JoinTypeName(metadata.GetString(definition.Namespace), metadata.GetString(definition.Name));
            token = MetadataTokens.GetToken(typeHandle);
        }
        if (type.GetProperty("Method")?.GetValue(result) is MethodDefinitionHandle methodHandle && !methodHandle.IsNil)
        {
            var method = metadata.GetMethodDefinition(methodHandle);
            methodName = metadata.GetString(method.Name);
            var declaringType = method.GetDeclaringType();
            if (!declaringType.IsNil)
            {
                var definition = metadata.GetTypeDefinition(declaringType);
                typeName = JoinTypeName(metadata.GetString(definition.Namespace), metadata.GetString(definition.Name));
            }
            token = MetadataTokens.GetToken(methodHandle);
        }
        return new ConstGenericsProcessorFinding(Limit(code, 128), Limit(message.Replace('\r', ' ').Replace('\n', ' '), 4_096), typeName, methodName, token, null, null);
    }

    private static void SetProperty(Type type, object instance, string name, bool value)
    {
        var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (property?.CanWrite == true && property.PropertyType == typeof(bool))
            property.SetValue(instance, value);
    }

    private static string JoinTypeName(string ns, string name) => string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";

    private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];

    private class ResolverDispatchProxy : DispatchProxy
    {
        private VerificationResolverState? _state;

        public void Configure(VerificationResolverState state) => _state = state;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (_state is null || targetMethod is null || args is null)
                throw new InvalidOperationException("The verification resolver is not initialized.");
            return targetMethod.Name switch
            {
                "ResolveAssembly" when args.Length == 1 => _state.ResolveAssembly(args[0]),
                "ResolveModule" when args.Length == 2 => _state.ResolveModule(args[1]?.ToString()),
                _ => throw new MissingMethodException(targetMethod.DeclaringType?.FullName, targetMethod.Name)
            };
        }
    }

    private sealed class VerificationResolverState : IDisposable
    {
        private const int MaximumAssemblies = 4_096;
        private readonly Dictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PEReader> _readers = new(StringComparer.OrdinalIgnoreCase);

        public VerificationResolverState(string artifactRoot, IReadOnlyList<string> referenceRoots)
        {
            Index(artifactRoot, SearchOption.AllDirectories, replaceExisting: true);
            foreach (var root in referenceRoots)
                Index(root, SearchOption.TopDirectoryOnly, replaceExisting: false);
        }

        public PEReader? ResolveAssembly(object? assemblyName)
        {
            var name = assemblyName?.GetType().GetProperty("Name")?.GetValue(assemblyName)?.ToString();
            return Resolve(name);
        }

        public PEReader? ResolveModule(string? fileName) =>
            Resolve(Path.GetFileNameWithoutExtension(fileName));

        public void Dispose()
        {
            foreach (var reader in _readers.Values)
                reader.Dispose();
            _readers.Clear();
        }

        private PEReader? Resolve(string? name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['/', '\\', '\0']) >= 0)
                return null;
            if (_readers.TryGetValue(name, out var cached))
                return cached;
            if (!_paths.TryGetValue(name, out var path))
                return null;
            var reader = new PEReader(File.OpenRead(path), PEStreamOptions.PrefetchEntireImage);
            _readers.Add(name, reader);
            return reader;
        }

        private void Index(string root, SearchOption searchOption, bool replaceExisting)
        {
            foreach (var path in Directory.EnumerateFiles(Path.GetFullPath(root), "*", searchOption).Where(static path => Path.GetExtension(path).ToLowerInvariant() is ".dll" or ".exe" or ".winmd"))
            {
                if (_paths.Count >= MaximumAssemblies)
                    throw new ProcessorLimitExceededException();
                var name = Path.GetFileNameWithoutExtension(path);
                if (replaceExisting)
                    _paths[name] = path;
                else
                    _paths.TryAdd(name, path);
            }
        }
    }
}
