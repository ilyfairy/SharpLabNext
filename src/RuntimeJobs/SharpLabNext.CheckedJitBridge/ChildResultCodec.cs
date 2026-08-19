using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;

namespace SharpLabNext.CheckedJitBridge;

internal sealed record ChildMethodRecord(
    string Method,
    int MetadataToken,
    string DisplayName,
    IReadOnlyList<ChildGenericArgument> DeclaringTypeArguments,
    IReadOnlyList<ChildGenericArgument> MethodArguments,
    string Status,
    string? Address,
    string? Error);

internal sealed record ChildErrorRecord(
    string TypeName,
    string Message,
    string? StackTrace);

internal sealed record ChildResultEnvelope(
    string Magic,
    string Nonce,
    string AssemblyName,
    IReadOnlyList<ChildMethodRecord> Methods,
    ChildErrorRecord? FatalError)
{
    public const string ProtocolMagic = "SLNCJ2";
}

internal sealed record ValidatedChildResult(
    string AssemblyName,
    IReadOnlyList<JitMethodResult> Methods,
    ChildErrorRecord? FatalError);

internal static class ChildResultCodec
{
    private const int MaximumPayloadBytes = 512 * 1024;
    private const int MaximumMethods = 1_000;
    private const int MaximumGenericInstancesPerMethod = 8;
    private const int MaximumTextLength = 4_096;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        MaxDepth = 16,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow
    };

    public static byte[] Serialize(ChildResultEnvelope envelope)
    {
        if (envelope is null)
            throw new ArgumentNullException(nameof(envelope));
        return JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
    }

    public static ValidatedChildResult ParseAndValidate(
        byte[] payload,
        string assemblyPath,
        string expectedNonce)
    {
        if (payload is null)
            throw new ArgumentNullException(nameof(payload));
        if (payload.Length == 0 || payload.Length > MaximumPayloadBytes)
            throw new InvalidDataException("Checked JIT child metadata is empty or exceeds the bridge limit.");
        if (!BridgePathValidation.IsLowerHexNonce(expectedNonce))
            throw new ArgumentException("Expected Checked JIT child nonce is invalid.", nameof(expectedNonce));

        ChildResultEnvelope envelope;
        try
        {
            using var document = JsonDocument.Parse(payload);
            ValidateJsonShape(document.RootElement);
            envelope = JsonSerializer.Deserialize<ChildResultEnvelope>(payload, JsonOptions)
                ?? throw new InvalidDataException("Checked JIT child metadata is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Checked JIT child metadata is not valid JSON.", exception);
        }

        if (!string.Equals(envelope.Magic, ChildResultEnvelope.ProtocolMagic, StringComparison.Ordinal) ||
            !string.Equals(envelope.Nonce, expectedNonce, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Checked JIT child metadata identity is invalid.");
        }
        if (envelope.Methods is null || envelope.Methods.Count > MaximumMethods)
            throw new InvalidDataException("Checked JIT child method count exceeds the bridge limit.");
        ValidateError(envelope.FatalError);

        using var metadata = ManagedAssemblyMetadata.Open(assemblyPath);
        if (!string.Equals(envelope.AssemblyName, metadata.AssemblyName, StringComparison.Ordinal))
            throw new InvalidDataException("Checked JIT child assembly identity does not match the user PE.");

        var identities = new HashSet<string>(StringComparer.Ordinal);
        var tokenCounts = new Dictionary<int, int>();
        var methods = new List<JitMethodResult>(envelope.Methods.Count);
        foreach (var record in envelope.Methods)
        {
            if (record is null)
                throw new InvalidDataException("Checked JIT child method metadata contains a null record.");
            if (!identities.Add(JitMethodSignatures.CreateStructuralIdentity(record)))
                throw new InvalidDataException("Checked JIT child method metadata contains a duplicate identity.");
            tokenCounts.TryGetValue(record.MetadataToken, out var tokenCount);
            if (tokenCount >= MaximumGenericInstancesPerMethod)
            {
                throw new InvalidDataException(
                    "Checked JIT child method metadata exceeds the generic instance limit.");
            }
            tokenCounts[record.MetadataToken] = tokenCount + 1;

            var signatureIdentity = metadata.ValidateMethod(record);
            methods.Add(new JitMethodResult(
                record.Method,
                record.MetadataToken,
                record.DisplayName,
                record.Status,
                record.Address,
                record.Error,
                signatureIdentity));
        }

        return new ValidatedChildResult(envelope.AssemblyName, methods, envelope.FatalError);
    }

    private static void ValidateError(ChildErrorRecord? error)
    {
        if (error is null)
            return;
        ValidateBoundedText(error.TypeName, nameof(error.TypeName), allowEmpty: false);
        ValidateBoundedText(error.Message, nameof(error.Message), allowEmpty: true);
        if (error.StackTrace is not null)
            ValidateBoundedText(error.StackTrace, nameof(error.StackTrace), allowEmpty: true);
    }

    private static void ValidateBoundedText(string value, string name, bool allowEmpty)
    {
        if (value is null || (!allowEmpty && value.Length == 0) || value.Length > MaximumTextLength)
            throw new InvalidDataException($"Checked JIT child {name} is invalid.");
        if (value.Any(character => character == '\0'))
            throw new InvalidDataException($"Checked JIT child {name} contains a null character.");
    }

    private static void ValidateJsonShape(JsonElement root)
    {
        ValidateObjectProperties(root, "Magic", "Nonce", "AssemblyName", "Methods", "FatalError");
        var methods = root.GetProperty("Methods");
        if (methods.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Checked JIT child Methods must be an array.");
        foreach (var method in methods.EnumerateArray())
        {
            ValidateObjectProperties(
                method,
                "Method",
                "MetadataToken",
                "DisplayName",
                "DeclaringTypeArguments",
                "MethodArguments",
                "Status",
                "Address",
                "Error");
            ValidateGenericArgumentsShape(method.GetProperty("DeclaringTypeArguments"));
            ValidateGenericArgumentsShape(method.GetProperty("MethodArguments"));
        }

        var fatalError = root.GetProperty("FatalError");
        if (fatalError.ValueKind != JsonValueKind.Null)
            ValidateObjectProperties(fatalError, "TypeName", "Message", "StackTrace");
    }

    private static void ValidateGenericArgumentsShape(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Checked JIT child generic arguments must be an array.");
        foreach (var argument in arguments.EnumerateArray())
        {
            ValidateObjectProperties(
                argument,
                "AssemblyName",
                "TypeName",
                "IsValueType",
                "JitName");
        }
    }

    private static void ValidateObjectProperties(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Checked JIT child metadata contains a non-object record.");
        var actual = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != expected.Length || actual.Except(expected, StringComparer.Ordinal).Any())
            throw new InvalidDataException("Checked JIT child metadata contains unknown or missing properties.");
    }
}

internal sealed class ManagedAssemblyMetadata : IDisposable
{
    private readonly FileStream _stream;
    private readonly PEReader _peReader;
    private readonly MetadataReader _reader;

    private ManagedAssemblyMetadata(FileStream stream, PEReader peReader, MetadataReader reader)
    {
        _stream = stream;
        _peReader = peReader;
        _reader = reader;
        AssemblyName = reader.GetString(reader.GetAssemblyDefinition().Name);
    }

    public string AssemblyName { get; }

    public static ManagedAssemblyMetadata Open(string assemblyPath)
    {
        assemblyPath = BridgePathValidation.ValidateAssemblyPath(assemblyPath);
        var stream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var peReader = new PEReader(stream, PEStreamOptions.PrefetchMetadata);
            if (!peReader.HasMetadata)
                throw new BadImageFormatException("User entry assembly does not contain managed metadata.");
            var reader = peReader.GetMetadataReader(MetadataReaderOptions.ApplyWindowsRuntimeProjections);
            if (!reader.IsAssembly)
                throw new BadImageFormatException("User entry assembly metadata is not an assembly.");
            return new ManagedAssemblyMetadata(stream, peReader, reader);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public JitMethodSignatureIdentity ValidateMethod(ChildMethodRecord record)
    {
        if ((record.MetadataToken & unchecked((int)0xff000000)) != 0x06000000)
            throw new InvalidDataException("Checked JIT child metadata token is not a MethodDef.");
        var row = record.MetadataToken & 0x00ffffff;
        if (row <= 0 || row > _reader.GetTableRowCount(TableIndex.MethodDef))
            throw new InvalidDataException("Checked JIT child MethodDef token is outside the user PE.");

        var handle = MetadataTokens.MethodDefinitionHandle(row);
        var definition = _reader.GetMethodDefinition(handle);
        var methodName = _reader.GetString(definition.Name);
        var typeHandle = definition.GetDeclaringType();
        if (typeHandle.IsNil)
            throw new InvalidDataException("Checked JIT child MethodDef has no declaring type.");
        var typeName = GetTypeFullName(typeHandle);
        var typeDefinition = _reader.GetTypeDefinition(typeHandle);
        var declaringTypeIsGeneric = typeDefinition.GetGenericParameters().Count > 0;

        ValidateGenericArguments(
            record.DeclaringTypeArguments,
            typeDefinition.GetGenericParameters().Count,
            "declaring type");
        ValidateGenericArguments(
            record.MethodArguments,
            definition.GetGenericParameters().Count,
            "method");

        var expectedIdentity = JitMethodSignatures.CreateMethodIdentity(
            record.MetadataToken,
            record.DeclaringTypeArguments,
            record.MethodArguments);
        if (!string.Equals(record.Method, expectedIdentity, StringComparison.Ordinal) || record.Method.Length > 4_096)
            throw new InvalidDataException("Checked JIT child method identity does not match its MethodDef.");

        var expectedDisplayName = typeName + "." + methodName;
        var displayNameIsValid = string.Equals(record.DisplayName, expectedDisplayName, StringComparison.Ordinal) ||
            (declaringTypeIsGeneric &&
             record.DisplayName.StartsWith(typeName + "[", StringComparison.Ordinal) &&
             record.DisplayName.EndsWith("." + methodName, StringComparison.Ordinal) &&
             record.DisplayName.Length <= 2_048);
        if (!displayNameIsValid)
            throw new InvalidDataException("Checked JIT child display name does not match its MethodDef.");

        if (record.Status == "prepared")
        {
            if (!IsAddress(record.Address) || record.Error is not null)
                throw new InvalidDataException("Checked JIT prepared method metadata is invalid.");
        }
        else if (record.Status == "failed")
        {
            if (record.Address is not null || string.IsNullOrEmpty(record.Error) || record.Error.Length > 4_096)
                throw new InvalidDataException("Checked JIT failed method metadata is invalid.");
        }
        else
        {
            throw new InvalidDataException("Checked JIT child method status is invalid.");
        }

        return JitMethodSignatures.CreateFromMetadata(
            _reader,
            handle,
            record.DeclaringTypeArguments,
            record.MethodArguments);
    }

    public void Dispose()
    {
        _peReader.Dispose();
        _stream.Dispose();
    }

    private string GetTypeFullName(TypeDefinitionHandle handle) =>
        JitMethodSignatures.GetTypeFullName(_reader, handle);

    private static void ValidateGenericArguments(
        IReadOnlyList<ChildGenericArgument>? arguments,
        int expectedCount,
        string description)
    {
        if (arguments is null || arguments.Count != expectedCount || arguments.Count > 32)
            throw new InvalidDataException($"Checked JIT child {description} arguments are invalid.");

        foreach (var argument in arguments)
        {
            if (argument is null ||
                !IsBoundedText(argument.AssemblyName, 256) ||
                !IsBoundedText(argument.TypeName, 2_048) ||
                !IsBoundedText(argument.JitName, 2_048) ||
                argument.AssemblyName.Contains('/') ||
                argument.AssemblyName.Contains('\\') ||
                (!argument.IsValueType &&
                 !string.Equals(argument.JitName, "System.__Canon", StringComparison.Ordinal)) ||
                (argument.IsValueType &&
                 string.Equals(argument.JitName, "System.__Canon", StringComparison.Ordinal)))
            {
                throw new InvalidDataException($"Checked JIT child {description} argument identity is invalid.");
            }
        }
    }

    private static bool IsBoundedText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        !value.Any(char.IsControl);

    private static bool IsAddress(string? value)
    {
        if (value is null || value.Length < 3 || value.Length > 34 ||
            !value.StartsWith("0x", StringComparison.Ordinal))
        {
            return false;
        }
        for (var index = 2; index < value.Length; index++)
        {
            var character = value[index];
            if (!((character >= '0' && character <= '9') ||
                  (character >= 'a' && character <= 'f') ||
                  (character >= 'A' && character <= 'F')))
            {
                return false;
            }
        }
        return value.Skip(2).Any(character => character != '0');
    }
}
