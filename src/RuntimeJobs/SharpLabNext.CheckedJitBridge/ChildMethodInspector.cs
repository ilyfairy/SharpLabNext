using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace SharpLabNext.CheckedJitBridge;

internal static class ChildMethodInspector
{
    private const int MaximumMethods = 1_000;
    private const string JitGenericAttributeName = "SharpLab.Runtime.JitGenericAttribute";

    public static IReadOnlyList<ChildMethodRecord> Inspect(Assembly assembly, string? methodFilter)
    {
        if (assembly is null)
            throw new ArgumentNullException(nameof(assembly));

        var results = new List<ChildMethodRecord>();
        foreach (var type in ExpandTypes(assembly))
        {
            foreach (var declaredMethod in DeclaredMethods(type))
            {
                foreach (var method in ExpandMethods(declaredMethod))
                {
                    if (results.Count >= MaximumMethods)
                        return results;
                    if (method.IsAbstract || method.ContainsGenericParameters || (method.GetMethodImplementationFlags() & MethodImplAttributes.InternalCall) != 0)
                    {
                        continue;
                    }

                    var displayName = (type.FullName ?? type.Name) + "." + method.Name;
                    if (!MatchesFilter(displayName, methodFilter))
                        continue;

                    ChildGenericArgument[] declaringTypeArguments;
                    ChildGenericArgument[] methodArguments;
                    string methodIdentity;
                    try
                    {
                        declaringTypeArguments = GetGenericArguments(type);
                        methodArguments = method.IsGenericMethod
                            ? GetGenericArguments(method.GetGenericArguments()) : Array.Empty<ChildGenericArgument>();
                        methodIdentity = JitMethodSignatures.CreateMethodIdentity(method.MetadataToken, declaringTypeArguments, methodArguments);
                    }
                    catch (ArgumentException)
                    {
                        continue;
                    }

                    try
                    {
                        RuntimeHelpers.PrepareMethod(method.MethodHandle);
                        var address = method.MethodHandle.GetFunctionPointer();
                        results.Add(new ChildMethodRecord(methodIdentity, method.MetadataToken, Bound(displayName, 2_048), declaringTypeArguments, methodArguments, "prepared", FormatAddress(address), null));
                    }
                    catch (Exception exception)
                    {
                        results.Add(new ChildMethodRecord(methodIdentity, method.MetadataToken, Bound(displayName, 2_048), declaringTypeArguments, methodArguments, "failed", null, Bound(exception.GetType().Name + ": " + exception.Message, 4_096)));
                    }
                }
            }
        }
        return results;
    }

    internal static bool MatchesFilter(string displayName, string? methodFilter)
    {
        if (string.IsNullOrWhiteSpace(methodFilter))
            return true;
        return methodFilter.Contains('*') || methodFilter.Contains('?')
            ? MatchesWildcard(methodFilter, displayName) : displayName.Contains(methodFilter, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<Type> ExpandTypes(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            if (!type.ContainsGenericParameters)
            {
                yield return type;
                continue;
            }

            foreach (var arguments in ReadJitGenericArguments(type.GetCustomAttributesData()))
            {
                if (arguments.Length != type.GetGenericArguments().Length || arguments.Any(argument => argument.ContainsGenericParameters))
                {
                    continue;
                }

                Type? constructed = null;
                try
                {
                    constructed = type.MakeGenericType(arguments);
                }
                catch (ArgumentException) { }
                if (constructed is not null)
                    yield return constructed;
            }
        }
    }

    private static IEnumerable<MethodBase> DeclaredMethods(Type type)
    {
        const BindingFlags flags = BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.Static |
            BindingFlags.Instance |
            BindingFlags.DeclaredOnly;
        foreach (var method in type.GetMethods(flags))
            yield return method;
        foreach (var constructor in type.GetConstructors(flags))
            yield return constructor;
        if (type.TypeInitializer is not null)
            yield return type.TypeInitializer;
    }

    private static IEnumerable<MethodBase> ExpandMethods(MethodBase method)
    {
        if (method is not MethodInfo { IsGenericMethodDefinition: true } genericMethod)
        {
            if (!method.ContainsGenericParameters)
                yield return method;
            yield break;
        }

        foreach (var arguments in ReadJitGenericArguments(genericMethod.GetCustomAttributesData()))
        {
            if (arguments.Length != genericMethod.GetGenericArguments().Length || arguments.Any(argument => argument.ContainsGenericParameters))
            {
                continue;
            }

            MethodInfo? constructed = null;
            try
            {
                constructed = genericMethod.MakeGenericMethod(arguments);
            }
            catch (ArgumentException) { }
            if (constructed is not null)
                yield return constructed;
        }
    }

    private static IEnumerable<Type[]> ReadJitGenericArguments(IList<CustomAttributeData> attributes)
    {
        foreach (var attribute in attributes)
        {
            if (!string.Equals(attribute.AttributeType.FullName, JitGenericAttributeName, StringComparison.Ordinal) || attribute.ConstructorArguments.Count != 1)
            {
                continue;
            }

            if (attribute.ConstructorArguments[0].Value is not IList<CustomAttributeTypedArgument> values)
                continue;
            var types = new Type[values.Count];
            var valid = true;
            for (var index = 0; index < values.Count; index++)
            {
                types[index] = values[index].Value as Type ?? typeof(void);
                if (types[index] == typeof(void))
                    valid = false;
            }
            if (valid)
                yield return types;
        }
    }

    private static ChildGenericArgument[] GetGenericArguments(Type type) =>
        type.IsGenericType && !type.IsGenericTypeDefinition
            ? GetGenericArguments(type.GetGenericArguments()) : Array.Empty<ChildGenericArgument>();

    private static ChildGenericArgument[] GetGenericArguments(Type[] types) =>
        types.Select(JitMethodSignatures.CreateGenericArgument).ToArray();

    private static string FormatAddress(IntPtr address)
    {
        var value = IntPtr.Size == 8
            ? unchecked((ulong)address.ToInt64()) : unchecked((uint)address.ToInt32());
        return "0x" + value.ToString("x", CultureInfo.InvariantCulture);
    }

    private static bool MatchesWildcard(string pattern, string value)
    {
        var patternIndex = 0;
        var valueIndex = 0;
        var starIndex = -1;
        var starValueIndex = -1;
        while (valueIndex < value.Length)
        {
            if (patternIndex < pattern.Length && (pattern[patternIndex] == '?' || char.ToUpperInvariant(pattern[patternIndex]) == char.ToUpperInvariant(value[valueIndex])))
            {
                patternIndex++;
                valueIndex++;
                continue;
            }
            if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                starIndex = patternIndex++;
                starValueIndex = valueIndex;
                continue;
            }
            if (starIndex >= 0)
            {
                patternIndex = starIndex + 1;
                valueIndex = ++starValueIndex;
                continue;
            }
            return false;
        }
        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            patternIndex++;
        return patternIndex == pattern.Length;
    }

    private static string Bound(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value.Substring(0, maximumLength);
}

internal sealed class ChildUserAssemblyLoader : IDisposable
{
    private readonly string _assemblyPath;
    private readonly string _artifactDirectory;
    private readonly string _helperDirectory;

    public ChildUserAssemblyLoader(string assemblyPath)
    {
        _assemblyPath = assemblyPath ?? throw new ArgumentNullException(nameof(assemblyPath));
        _artifactDirectory = Path.GetDirectoryName(assemblyPath) ?? throw new ArgumentException("The entry assembly has no parent directory.", nameof(assemblyPath));
        _helperDirectory = AppContext.BaseDirectory;
        AssemblyLoadContext.Default.Resolving += Resolve;
    }

    public Assembly Load() => AssemblyLoadContext.Default.LoadFromAssemblyPath(_assemblyPath);

    public void Dispose() => AssemblyLoadContext.Default.Resolving -= Resolve;

    private Assembly? Resolve(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName.Name) || !IsSimpleFileName(assemblyName.Name))
            return null;
        var fileName = assemblyName.Name + ".dll";
        var artifactCandidate = Path.Combine(_artifactDirectory, fileName);
        if (File.Exists(artifactCandidate))
            return context.LoadFromAssemblyPath(artifactCandidate);
        var helperCandidate = Path.Combine(_helperDirectory, fileName);
        return File.Exists(helperCandidate) ? context.LoadFromAssemblyPath(helperCandidate) : null;
    }

    private static bool IsSimpleFileName(string name) =>
        name != "." &&
        name != ".." &&
        !name.Contains(Path.DirectorySeparatorChar) &&
        !name.Contains(Path.AltDirectorySeparatorChar) &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
}

internal static class ChildNativeStreamFlusher
{
    public static void FlushAll()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var flushed = TryFlush(() => FlushUcrt(IntPtr.Zero)) |
                TryFlush(() => FlushMsvcrt(IntPtr.Zero));
            if (!flushed)
                throw new IOException("CoreCLR JIT output could not be flushed on Windows.");
            return;
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            if (FlushLibc(IntPtr.Zero) != 0)
                throw new IOException("CoreCLR JIT output could not be flushed through libc.");
            return;
        }
        throw new PlatformNotSupportedException("Checked JIT bridge supports Linux and Windows only.");
    }

    private static bool TryFlush(Func<int> flush)
    {
        try
        {
            return flush() == 0;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    [DllImport("libc", EntryPoint = "fflush", CallingConvention = CallingConvention.Cdecl)]
    private static extern int FlushLibc(IntPtr stream);

    [DllImport("ucrtbase.dll", EntryPoint = "fflush", CallingConvention = CallingConvention.Cdecl)]
    private static extern int FlushUcrt(IntPtr stream);

    [DllImport("msvcrt.dll", EntryPoint = "fflush", CallingConvention = CallingConvention.Cdecl)]
    private static extern int FlushMsvcrt(IntPtr stream);
}
