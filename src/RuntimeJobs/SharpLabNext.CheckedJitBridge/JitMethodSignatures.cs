using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Text;

namespace SharpLabNext.CheckedJitBridge;

internal sealed record ChildGenericArgument(
    string AssemblyName,
    string TypeName,
    bool IsValueType,
    string JitName);

internal sealed record JitMethodSignatureIdentity(
    string NameKey,
    string? HeaderKey,
    string? NamespaceShortenedNameKey = null,
    string? NamespaceShortenedHeaderKey = null);

internal static class JitMethodSignatures
{
    private const string CanonicalReferenceTypeName = "System.__Canon";

    public static ChildGenericArgument CreateGenericArgument(Type type)
    {
        if (type is null)
            throw new ArgumentNullException(nameof(type));
        if (type.ContainsGenericParameters || type == typeof(void) || type.IsByRef || type.IsPointer)
            throw new ArgumentException("A Checked JIT generic argument must be a closed runtime type.", nameof(type));

        return new ChildGenericArgument(
            type.Assembly.GetName().Name ?? string.Empty,
            type.FullName ?? type.Name,
            type.IsValueType,
            type.IsValueType ? FormatConstructedValueType(type) : CanonicalReferenceTypeName);
    }

    public static string CreateMethodIdentity(
        int metadataToken,
        IReadOnlyList<ChildGenericArgument> declaringTypeArguments,
        IReadOnlyList<ChildGenericArgument> methodArguments)
    {
        if (declaringTypeArguments is null)
            throw new ArgumentNullException(nameof(declaringTypeArguments));
        if (methodArguments is null)
            throw new ArgumentNullException(nameof(methodArguments));

        var identity = new StringBuilder("0x" + metadataToken.ToString("x8", CultureInfo.InvariantCulture));
        AppendArguments(identity, "type", declaringTypeArguments);
        AppendArguments(identity, "method", methodArguments);
        return identity.ToString();
    }

    public static string CreateStructuralIdentity(ChildMethodRecord method)
    {
        if (method is null)
            throw new ArgumentNullException(nameof(method));

        var identity = new StringBuilder(method.MetadataToken.ToString(CultureInfo.InvariantCulture));
        AppendStructuralArguments(identity, method.DeclaringTypeArguments);
        AppendStructuralArguments(identity, method.MethodArguments);
        return identity.ToString();
    }

    public static JitMethodSignatureIdentity CreateFromMetadata(
        MetadataReader reader,
        MethodDefinitionHandle methodHandle,
        IReadOnlyList<ChildGenericArgument> declaringTypeArguments,
        IReadOnlyList<ChildGenericArgument> methodArguments)
    {
        if (reader is null)
            throw new ArgumentNullException(nameof(reader));
        if (declaringTypeArguments is null)
            throw new ArgumentNullException(nameof(declaringTypeArguments));
        if (methodArguments is null)
            throw new ArgumentNullException(nameof(methodArguments));

        var definition = reader.GetMethodDefinition(methodHandle);
        var methodName = reader.GetString(definition.Name);
        var declaringType = definition.GetDeclaringType();
        if (declaringType.IsNil)
            throw new BadImageFormatException("MethodDef has no declaring type.");

        var declaringTypeName = GetTypeFullName(reader, declaringType);
        var namespaceShortenedDeclaringTypeName = GetTypeNameWithoutNamespace(reader, declaringType);
        var nameKey = declaringTypeName + ":" + methodName;
        var namespaceShortenedNameKey = string.Equals(
            declaringTypeName,
            namespaceShortenedDeclaringTypeName,
            StringComparison.Ordinal)
                ? null
                : namespaceShortenedDeclaringTypeName + ":" + methodName;
        var context = new JitGenericContext(
            declaringTypeArguments.Select(static argument => argument.JitName).ToArray(),
            methodArguments.Select(static argument => argument.JitName).ToArray());
        var signature = definition.DecodeSignature(JitSignatureTypeProvider.Instance, context);
        if (!signature.ReturnType.IsSupported || signature.ParameterTypes.Any(static type => !type.IsSupported))
            return new JitMethodSignatureIdentity(nameKey, null, namespaceShortenedNameKey, null);

        var isInstance = (definition.Attributes & MethodAttributes.Static) == 0;
        var headerKey = CreateHeader(
            declaringTypeName,
            methodName,
            context,
            signature,
            isInstance);
        var namespaceShortenedHeaderKey = namespaceShortenedNameKey is null
            ? null
            : CreateHeader(
                namespaceShortenedDeclaringTypeName,
                methodName,
                context,
                signature,
                isInstance);

        return new JitMethodSignatureIdentity(
            nameKey,
            headerKey,
            namespaceShortenedNameKey,
            namespaceShortenedHeaderKey);
    }

    private static string CreateHeader(
        string declaringTypeName,
        string methodName,
        JitGenericContext context,
        MethodSignature<JitSignatureType> signature,
        bool isInstance)
    {
        var header = new StringBuilder(declaringTypeName);
        AppendJitArguments(header, context.DeclaringTypeArguments);
        header.Append(':').Append(methodName);
        AppendJitArguments(header, context.MethodArguments);
        header.Append('(');
        for (var index = 0; index < signature.ParameterTypes.Length; index++)
        {
            if (index > 0)
                header.Append(',');
            header.Append(signature.ParameterTypes[index].Text);
        }
        header.Append(')');

        if (isInstance && string.Equals(methodName, ".ctor", StringComparison.Ordinal))
        {
            header.Append(":this");
        }
        else
        {
            header.Append(':').Append(signature.ReturnType.Text);
            if (isInstance)
                header.Append(":this");
        }

        return header.ToString();
    }

    public static bool TryParseHeader(string value, out JitMethodSignatureIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            identity = new JitMethodSignatureIdentity(string.Empty, null);
            return false;
        }

        var headerKey = RemoveCompilationQualifier(value.Trim());
        var parameterStart = headerKey.IndexOf('(');
        if (parameterStart <= 0)
        {
            identity = new JitMethodSignatureIdentity(string.Empty, null);
            return false;
        }

        var prefix = headerKey.Substring(0, parameterStart);
        var separator = FindTopLevelSeparator(prefix);
        if (separator <= 0 || separator == prefix.Length - 1)
        {
            identity = new JitMethodSignatureIdentity(string.Empty, null);
            return false;
        }

        var declaringType = RemoveTrailingInstantiation(prefix.Substring(0, separator));
        var method = RemoveTrailingInstantiation(prefix.Substring(separator + 1));
        if (declaringType.Length == 0 || method.Length == 0)
        {
            identity = new JitMethodSignatureIdentity(string.Empty, null);
            return false;
        }

        identity = new JitMethodSignatureIdentity(declaringType + ":" + method, headerKey);
        return true;
    }

    internal static string GetTypeFullName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var definition = reader.GetTypeDefinition(handle);
        var name = reader.GetString(definition.Name);
        var declaring = definition.GetDeclaringType();
        if (!declaring.IsNil)
            return GetTypeFullName(reader, declaring) + "+" + name;
        var @namespace = reader.GetString(definition.Namespace);
        return @namespace.Length == 0 ? name : @namespace + "." + name;
    }

    private static string GetTypeNameWithoutNamespace(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var definition = reader.GetTypeDefinition(handle);
        var name = reader.GetString(definition.Name);
        var declaring = definition.GetDeclaringType();
        return declaring.IsNil
            ? name
            : GetTypeNameWithoutNamespace(reader, declaring) + "+" + name;
    }

    private static string GetTypeFullName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var reference = reader.GetTypeReference(handle);
        var name = reader.GetString(reference.Name);
        if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
            return GetTypeFullName(reader, (TypeReferenceHandle)reference.ResolutionScope) + "+" + name;
        var @namespace = reader.GetString(reference.Namespace);
        return @namespace.Length == 0 ? name : @namespace + "." + name;
    }

    private static string FormatConstructedValueType(Type type)
    {
        var primitive = GetPrimitiveName(type);
        if (primitive is not null)
            return primitive;
        if (type.IsEnum)
            return type.FullName ?? type.Name;
        if (!type.IsGenericType)
            return type.FullName ?? type.Name;

        var definition = type.GetGenericTypeDefinition();
        var name = definition.FullName ?? definition.Name;
        var arguments = type.GetGenericArguments()
            .Select(static argument => argument.IsValueType
                ? FormatConstructedValueType(argument)
                : CanonicalReferenceTypeName);
        return name + "[" + string.Join(",", arguments) + "]";
    }

    private static string? GetPrimitiveName(Type type)
    {
        if (type == typeof(bool)) return "bool";
        if (type == typeof(byte)) return "ubyte";
        if (type == typeof(sbyte)) return "byte";
        if (type == typeof(char)) return "char";
        if (type == typeof(short)) return "short";
        if (type == typeof(ushort)) return "ushort";
        if (type == typeof(int)) return "int";
        if (type == typeof(uint)) return "uint";
        if (type == typeof(long)) return "long";
        if (type == typeof(ulong)) return "ulong";
        if (type == typeof(IntPtr)) return "nint";
        if (type == typeof(UIntPtr)) return "nuint";
        if (type == typeof(float)) return "float";
        if (type == typeof(double)) return "double";
        return null;
    }

    private static void AppendArguments(
        StringBuilder identity,
        string kind,
        IReadOnlyList<ChildGenericArgument> arguments)
    {
        if (arguments.Count == 0)
            return;
        identity.Append('[').Append(kind).Append('=');
        for (var index = 0; index < arguments.Count; index++)
        {
            if (index > 0)
                identity.Append(',');
            identity.Append(arguments[index].AssemblyName)
                .Append('/')
                .Append(arguments[index].TypeName);
        }
        identity.Append(']');
    }

    private static void AppendStructuralArguments(
        StringBuilder identity,
        IReadOnlyList<ChildGenericArgument>? arguments)
    {
        if (arguments is null)
        {
            identity.Append("|null");
            return;
        }

        identity.Append('|').Append(arguments.Count);
        foreach (var argument in arguments)
        {
            if (argument is null)
            {
                identity.Append("|null");
                continue;
            }

            AppendLengthPrefixed(identity, argument.AssemblyName);
            AppendLengthPrefixed(identity, argument.TypeName);
        }
    }

    private static void AppendLengthPrefixed(StringBuilder identity, string? value)
    {
        if (value is null)
        {
            identity.Append("|-1:");
            return;
        }
        identity.Append('|').Append(value.Length).Append(':').Append(value);
    }

    private static void AppendJitArguments(StringBuilder text, IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
            return;
        text.Append('[').Append(string.Join(",", arguments)).Append(']');
    }

    private static int FindTopLevelSeparator(string value)
    {
        var bracketDepth = 0;
        for (var index = 0; index < value.Length; index++)
        {
            switch (value[index])
            {
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    if (bracketDepth == 0)
                        return -1;
                    bracketDepth--;
                    break;
                case ':' when bracketDepth == 0:
                    return index;
            }
        }
        return -1;
    }

    private static string RemoveTrailingInstantiation(string value)
    {
        if (!value.EndsWith(']'))
            return value;

        var depth = 0;
        for (var index = value.Length - 1; index >= 0; index--)
        {
            if (value[index] == ']')
            {
                depth++;
                continue;
            }
            if (value[index] != '[')
                continue;
            depth--;
            if (depth == 0)
                return value.Substring(0, index);
            if (depth < 0)
                return value;
        }
        return value;
    }

    private static string RemoveCompilationQualifier(string value)
    {
        if (!value.EndsWith(')'))
            return value;
        var start = value.LastIndexOf(" (", StringComparison.Ordinal);
        if (start < 0)
            return value;
        var qualifier = value.Substring(start + 2, value.Length - start - 3);
        if (!qualifier.Contains("Tier", StringComparison.OrdinalIgnoreCase) &&
            !qualifier.Contains("FullOpts", StringComparison.OrdinalIgnoreCase) &&
            !qualifier.Contains("MinOpts", StringComparison.OrdinalIgnoreCase) &&
            !qualifier.Contains("OSR", StringComparison.OrdinalIgnoreCase) &&
            !qualifier.Contains("Instrumented", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }
        return value.Substring(0, start);
    }

    private readonly record struct JitGenericContext(
        IReadOnlyList<string> DeclaringTypeArguments,
        IReadOnlyList<string> MethodArguments);

    private readonly record struct JitSignatureType(string? Text)
    {
        public bool IsSupported => Text is not null;

        public static JitSignatureType Unsupported => new(null);
    }

    private sealed class JitSignatureTypeProvider : ISignatureTypeProvider<JitSignatureType, JitGenericContext>
    {
        public static JitSignatureTypeProvider Instance { get; } = new();

        public JitSignatureType GetArrayType(JitSignatureType elementType, ArrayShape shape)
        {
            if (!elementType.IsSupported || shape.Rank <= 0)
                return JitSignatureType.Unsupported;
            return new JitSignatureType(elementType.Text + "[" + new string(',', shape.Rank - 1) + "]");
        }

        public JitSignatureType GetByReferenceType(JitSignatureType elementType) =>
            elementType.IsSupported ? new JitSignatureType("byref") : JitSignatureType.Unsupported;

        public JitSignatureType GetFunctionPointerType(MethodSignature<JitSignatureType> signature) =>
            JitSignatureType.Unsupported;

        public JitSignatureType GetGenericInstantiation(
            JitSignatureType genericType,
            ImmutableArray<JitSignatureType> typeArguments)
        {
            if (!genericType.IsSupported || typeArguments.Any(static type => !type.IsSupported))
                return JitSignatureType.Unsupported;
            return new JitSignatureType(
                genericType.Text + "[" + string.Join(",", typeArguments.Select(static type => type.Text)) + "]");
        }

        public JitSignatureType GetGenericMethodParameter(JitGenericContext genericContext, int index) =>
            GetGenericParameter(genericContext.MethodArguments, index);

        public JitSignatureType GetGenericTypeParameter(JitGenericContext genericContext, int index) =>
            GetGenericParameter(genericContext.DeclaringTypeArguments, index);

        public JitSignatureType GetModifiedType(
            JitSignatureType modifier,
            JitSignatureType unmodifiedType,
            bool isRequired) => unmodifiedType;

        public JitSignatureType GetPinnedType(JitSignatureType elementType) => elementType;

        public JitSignatureType GetPointerType(JitSignatureType elementType) =>
            elementType.IsSupported ? new JitSignatureType("ptr") : JitSignatureType.Unsupported;

        public JitSignatureType GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
        {
            PrimitiveTypeCode.Boolean => new JitSignatureType("bool"),
            PrimitiveTypeCode.Byte => new JitSignatureType("ubyte"),
            PrimitiveTypeCode.SByte => new JitSignatureType("byte"),
            PrimitiveTypeCode.Char => new JitSignatureType("char"),
            PrimitiveTypeCode.Int16 => new JitSignatureType("short"),
            PrimitiveTypeCode.UInt16 => new JitSignatureType("ushort"),
            PrimitiveTypeCode.Int32 => new JitSignatureType("int"),
            PrimitiveTypeCode.UInt32 => new JitSignatureType("uint"),
            PrimitiveTypeCode.Int64 => new JitSignatureType("long"),
            PrimitiveTypeCode.UInt64 => new JitSignatureType("ulong"),
            PrimitiveTypeCode.IntPtr => new JitSignatureType("nint"),
            PrimitiveTypeCode.UIntPtr => new JitSignatureType("nuint"),
            PrimitiveTypeCode.Single => new JitSignatureType("float"),
            PrimitiveTypeCode.Double => new JitSignatureType("double"),
            PrimitiveTypeCode.Object => new JitSignatureType("System.Object"),
            PrimitiveTypeCode.String => new JitSignatureType("System.String"),
            PrimitiveTypeCode.TypedReference => new JitSignatureType("refany"),
            PrimitiveTypeCode.Void => new JitSignatureType("void"),
            _ => JitSignatureType.Unsupported
        };

        public JitSignatureType GetSZArrayType(JitSignatureType elementType) =>
            elementType.IsSupported
                ? new JitSignatureType(elementType.Text + "[]")
                : JitSignatureType.Unsupported;

        public JitSignatureType GetTypeFromDefinition(
            MetadataReader reader,
            TypeDefinitionHandle handle,
            byte rawTypeKind) => new(GetTypeFullName(reader, handle));

        public JitSignatureType GetTypeFromReference(
            MetadataReader reader,
            TypeReferenceHandle handle,
            byte rawTypeKind) => new(GetTypeFullName(reader, handle));

        public JitSignatureType GetTypeFromSpecification(
            MetadataReader reader,
            JitGenericContext genericContext,
            TypeSpecificationHandle handle,
            byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        private static JitSignatureType GetGenericParameter(IReadOnlyList<string> arguments, int index) =>
            index >= 0 && index < arguments.Count
                ? new JitSignatureType(arguments[index])
                : JitSignatureType.Unsupported;
    }
}
