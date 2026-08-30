using System.Reflection;
using System.Text;
using SharpLab.Runtime;

namespace SharpLabNext.CompatibilityTests;

public sealed class RuntimeApiCompatibilityTests
{
    [Fact]
    public void PublicApiMatchesApprovedSnapshot()
    {
        var assembly = typeof(RuntimeServices).Assembly;
        var actual = RenderPublicApi(assembly).Replace("\r\n", "\n", StringComparison.Ordinal);
        var approvedPath = Path.Combine(AppContext.BaseDirectory, "SharpLab.Runtime.approved.txt");
        var approved = File.ReadAllText(approvedPath).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Equal(approved.TrimEnd(), actual.TrimEnd());
    }

    [Fact]
    public void AssemblyAndLegacyGlobalTypesKeepTheirNames()
    {
        var assembly = typeof(RuntimeServices).Assembly;

        Assert.Equal("SharpLab.Runtime", assembly.GetName().Name);
        Assert.NotNull(assembly.GetType("Inspect"));
        Assert.NotNull(assembly.GetType("SharpLabObjectExtensions"));
        Assert.NotNull(assembly.GetType("SharpLab.Runtime.JitGenericAttribute"));
        Assert.NotNull(assembly.GetType("SharpLab.Runtime.NoILRewritingAttribute"));
    }

    [Fact]
    public void DumpAndInspectUseStructuredSinkWithoutWritingStdoutMarkers()
    {
        var sink = new RecordingSink();
        using var scope = RuntimeServices.PushInspectionSink(sink);

        var returned = 42.Dump();
        "value".Inspect("Custom");

        Assert.Equal(42, returned);
        Assert.Collection(
            sink.Records,
            item =>
            {
                Assert.Equal(InspectionKind.Value, item.Kind);
                Assert.Equal("Dump", item.Title);
                Assert.Equal(42, item.Value);
            },
            item =>
            {
                Assert.Equal(InspectionKind.Value, item.Kind);
                Assert.Equal("Custom", item.Title);
                Assert.Equal("value", item.Value);
            });
    }

    [Fact]
    public void MemoryGraphPreservesAllRoots()
    {
        var sink = new RecordingSink();
        using var scope = RuntimeServices.PushInspectionSink(sink);

        Inspect.MemoryGraph(1, "two", 3.0);

        var graph = Assert.Single(sink.Records);
        Assert.Equal(InspectionKind.MemoryGraph, graph.Kind);
        Assert.Equal([1, "two", 3.0], graph.Values);
    }

    [Fact]
    public void AllocationsExecutesActionAndReportsAllocatedBytes()
    {
        var sink = new RecordingSink();
        using var scope = RuntimeServices.PushInspectionSink(sink);
        var called = false;

        Inspect.Allocations(() => called = true);

        Assert.True(called);
        var allocation = Assert.Single(sink.Records);
        Assert.Equal(InspectionKind.Allocations, allocation.Kind);
        Assert.Equal("Allocations", allocation.Title);
        Assert.True(Assert.IsType<AllocationInspection>(allocation.Value).AllocatedBytes >= 0);
    }

    [Fact]
    public void CompatibilityAttributesKeepExpectedTargets()
    {
        var jitUsage = typeof(JitGenericAttribute).GetCustomAttribute<AttributeUsageAttribute>();
        var noRewriteUsage = typeof(NoILRewritingAttribute).GetCustomAttribute<AttributeUsageAttribute>();

        Assert.NotNull(jitUsage);
        Assert.True(jitUsage.AllowMultiple);
        Assert.Equal(AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Struct, jitUsage.ValidOn);
        Assert.NotNull(noRewriteUsage);
        Assert.Equal(AttributeTargets.Assembly, noRewriteUsage.ValidOn);
    }

    private sealed class RecordingSink : IInspectionSink
    {
        public List<InspectionRecord> Records { get; } = [];

        public void Write(InspectionRecord inspection) => Records.Add(inspection);
    }

    private static string RenderPublicApi(Assembly assembly)
    {
        var output = new StringBuilder();
        foreach (var type in assembly.GetExportedTypes().OrderBy(static type => type.FullName, StringComparer.Ordinal))
        {
            output.Append(TypeKind(type)).Append(' ').Append(TypeName(type)).AppendLine();
            foreach (var constructor in type.GetConstructors(DeclaredPublicMembers).OrderBy(MemberSortKey, StringComparer.Ordinal))
                output.Append("  ctor ").Append(type.Name).Append('(').Append(string.Join(", ", constructor.GetParameters().Select(ParameterSignature))).AppendLine(")");
            foreach (var field in type.GetFields(DeclaredPublicMembers).OrderBy(MemberSortKey, StringComparer.Ordinal))
            {
                output.Append("  field ");
                if (field.IsStatic)
                    output.Append("static ");
                if (field.IsLiteral)
                    output.Append("const ");
                output.Append(TypeDisplay(field.FieldType)).Append(' ').Append(field.Name);
                if (field.IsLiteral)
                    output.Append(" = ").Append(field.GetRawConstantValue());
                output.AppendLine();
            }
            foreach (var property in type.GetProperties(DeclaredPublicMembers).OrderBy(MemberSortKey, StringComparer.Ordinal))
            {
                var accessor = property.GetMethod ?? property.SetMethod;
                output.Append("  property ");
                if (accessor?.IsStatic == true)
                    output.Append("static ");
                output.Append(TypeDisplay(property.PropertyType)).Append(' ').Append(property.Name);
                var index = property.GetIndexParameters();
                if (index.Length > 0)
                    output.Append('[').Append(string.Join(", ", index.Select(ParameterSignature))).Append(']');
                output.Append(" { ");
                if (property.GetMethod?.IsPublic == true)
                    output.Append("get; ");
                if (property.SetMethod?.IsPublic == true)
                    output.Append("set; ");
                output.AppendLine("}");
            }
            foreach (var method in type.GetMethods(DeclaredPublicMembers).Where(static method => !method.IsSpecialName).OrderBy(MemberSortKey, StringComparer.Ordinal))
            {
                output.Append("  method ");
                if (method.IsStatic)
                    output.Append("static ");
                output.Append(TypeDisplay(method.ReturnType)).Append(' ').Append(method.Name);
                if (method.IsGenericMethodDefinition)
                    output.Append('<').Append(string.Join(", ", method.GetGenericArguments().Select(static argument => argument.Name))).Append('>');
                output.Append('(').Append(string.Join(", ", method.GetParameters().Select(ParameterSignature))).AppendLine(")");
            }
            output.AppendLine();
        }
        return output.ToString();
    }

    private const BindingFlags DeclaredPublicMembers =
        BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static string TypeKind(Type type) => type.IsEnum
        ? "enum" : type.IsInterface
            ? "interface" : type.IsValueType
                ? "struct" : type.IsAbstract && type.IsSealed
                    ? "static class" : type.IsSealed
                        ? "sealed class" : "class";

    private static string TypeName(Type type)
    {
        var name = type.FullName ?? type.Name;
        var tick = name.IndexOf('`');
        if (tick >= 0)
            name = name[..tick];
        if (!type.IsGenericTypeDefinition)
            return name;
        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(static argument => argument.Name))}>";
    }

    private static string MemberSortKey(MemberInfo member) => member switch
    {
        MethodBase method => $"{member.Name}({string.Join(",", method.GetParameters().Select(static parameter => TypeDisplay(parameter.ParameterType)))})",
        PropertyInfo property => $"{member.Name}:{TypeDisplay(property.PropertyType)}",
        FieldInfo field => $"{member.Name}:{TypeDisplay(field.FieldType)}",
        _ => member.Name
    };

    private static string ParameterSignature(ParameterInfo parameter)
    {
        var modifier = parameter.ParameterType.IsByRef
            ? parameter.IsOut
                ? "out " : parameter.IsIn
                    ? "in " : "ref " : string.Empty;
        var type = parameter.ParameterType.IsByRef
            ? parameter.ParameterType.GetElementType()! : parameter.ParameterType;
        var optional = parameter.HasDefaultValue
            ? $" = {FormatDefaultValue(parameter.DefaultValue)}" : string.Empty;
        return $"{modifier}{TypeDisplay(type)} {parameter.Name}{optional}";
    }

    private static string FormatDefaultValue(object? value) => value switch
    {
        null => "null",
        string text => $"\"{text}\"",
        bool boolean => boolean ? "true" : "false",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "null"
    };

    private static string TypeDisplay(Type type)
    {
        if (type.IsGenericParameter)
            return type.Name;
        if (type.IsArray)
            return $"{TypeDisplay(type.GetElementType()!)}[]";
        if (type.IsByRef)
            return TypeDisplay(type.GetElementType()!);
        if (!type.IsGenericType)
            return type.FullName ?? type.Name;
        var definition = type.GetGenericTypeDefinition();
        var name = definition.FullName ?? definition.Name;
        var tick = name.IndexOf('`');
        if (tick >= 0)
            name = name[..tick];
        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(TypeDisplay))}>";
    }
}
