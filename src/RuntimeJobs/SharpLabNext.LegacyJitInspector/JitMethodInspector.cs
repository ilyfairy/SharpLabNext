using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace SharpLabNext.LegacyJitInspector
{
    internal static class JitMethodInspector
    {
        private const int MaximumMethods = 1_000;
        private const string JitGenericAttributeName = "SharpLab.Runtime.JitGenericAttribute";

        public static List<JitMethodResult> Inspect(Assembly assembly, string methodFilter)
        {
            if (assembly == null)
                throw new ArgumentNullException(nameof(assembly));

            var results = new List<JitMethodResult>();
            foreach (Type type in ExpandTypes(assembly))
            {
                foreach (MethodBase declaredMethod in DeclaredMethods(type))
                {
                    foreach (MethodBase method in ExpandMethods(declaredMethod))
                    {
                        if (results.Count >= MaximumMethods)
                            return results;
                        if (method.IsAbstract || method.ContainsGenericParameters || (method.GetMethodImplementationFlags() & MethodImplAttributes.InternalCall) != 0)
                        {
                            continue;
                        }

                        string displayName = GetDisplayName(type, method);
                        if (!MatchesFilter(displayName, methodFilter))
                            continue;

                        try
                        {
                            RuntimeHelpers.PrepareMethod(method.MethodHandle);
                            IntPtr address = method.MethodHandle.GetFunctionPointer();
                            results.Add(new JitMethodResult(GetMethodIdentity(method), method.MetadataToken, displayName, "prepared", FormatAddress(address), null));
                        }
                        catch (Exception exception)
                        {
                            results.Add(new JitMethodResult(GetMethodIdentity(method), method.MetadataToken, displayName, "failed", null, exception.GetType().Name + ": " + exception.Message));
                        }
                    }
                }
            }

            return results;
        }

        internal static bool MatchesFilter(string displayName, string methodFilter)
        {
            if (string.IsNullOrWhiteSpace(methodFilter))
                return true;
            return methodFilter.IndexOf('*') >= 0 || methodFilter.IndexOf('?') >= 0
                ? MatchesWildcard(methodFilter, displayName) : displayName.IndexOf(methodFilter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static IEnumerable<Type> ExpandTypes(Assembly assembly)
        {
            foreach (Type type in assembly.GetTypes())
            {
                if (!type.ContainsGenericParameters)
                {
                    yield return type;
                    continue;
                }

                foreach (Type[] arguments in ReadJitGenericArguments(type.GetCustomAttributesData()))
                {
                    if (arguments.Length != type.GetGenericArguments().Length || arguments.Any(argument => argument.ContainsGenericParameters))
                    {
                        continue;
                    }

                    Type constructed = null;
                    try
                    {
                        constructed = type.MakeGenericType(arguments);
                    }
                    catch (ArgumentException) { }

                    if (constructed != null)
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
            foreach (MethodInfo method in type.GetMethods(flags))
                yield return method;
            foreach (ConstructorInfo constructor in type.GetConstructors(flags))
                yield return constructor;
            if (type.TypeInitializer != null)
                yield return type.TypeInitializer;
        }

        private static IEnumerable<MethodBase> ExpandMethods(MethodBase method)
        {
            var genericMethod = method as MethodInfo;
            if (genericMethod == null || !genericMethod.IsGenericMethodDefinition)
            {
                if (!method.ContainsGenericParameters)
                    yield return method;
                yield break;
            }

            foreach (Type[] arguments in ReadJitGenericArguments(genericMethod.GetCustomAttributesData()))
            {
                if (arguments.Length != genericMethod.GetGenericArguments().Length || arguments.Any(argument => argument.ContainsGenericParameters))
                {
                    continue;
                }

                MethodInfo constructed = null;
                try
                {
                    constructed = genericMethod.MakeGenericMethod(arguments);
                }
                catch (ArgumentException) { }

                if (constructed != null)
                    yield return constructed;
            }
        }

        private static IEnumerable<Type[]> ReadJitGenericArguments(IList<CustomAttributeData> attributes)
        {
            foreach (CustomAttributeData attribute in attributes)
            {
                if (!string.Equals(attribute.AttributeType.FullName, JitGenericAttributeName, StringComparison.Ordinal) || attribute.ConstructorArguments.Count != 1)
                {
                    continue;
                }

                var values = attribute.ConstructorArguments[0].Value as IList<CustomAttributeTypedArgument>;
                if (values == null)
                    continue;
                var types = new Type[values.Count];
                bool valid = true;
                for (int index = 0; index < values.Count; index++)
                {
                    types[index] = values[index].Value as Type;
                    if (types[index] == null)
                        valid = false;
                }
                if (valid)
                    yield return types;
            }
        }

        private static string GetMethodIdentity(MethodBase method)
        {
            string token = "0x" + method.MetadataToken.ToString("x8", CultureInfo.InvariantCulture);
            if (!method.IsGenericMethod || method.IsGenericMethodDefinition)
                return token;

            string arguments = string.Join(",", method.GetGenericArguments().Select(argument => argument.FullName ?? argument.Name));
            return token + "[" + arguments + "]";
        }

        private static string GetDisplayName(Type type, MethodBase method)
        {
            return (type.FullName ?? type.Name) + "." + method.Name;
        }

        private static string FormatAddress(IntPtr address)
        {
            ulong value = IntPtr.Size == 8
                ? unchecked((ulong)address.ToInt64()) : unchecked((uint)address.ToInt32());
            return "0x" + value.ToString("x", CultureInfo.InvariantCulture);
        }

        private static bool MatchesWildcard(string pattern, string value)
        {
            int patternIndex = 0;
            int valueIndex = 0;
            int starIndex = -1;
            int starValueIndex = -1;
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
    }
}
