using System.Globalization;

internal static class JitRichMapLog
{
    private const long MaximumFileBytes = 8 * 1024 * 1024;
    private const int MaximumMethods = 1_000;
    private const int MaximumRecords = 1_000;
    private const int MaximumNativeCodeVersions = 8;
    private const int MaximumInlineNodes = 4_096;
    private const int MaximumMapEntriesPerMethod = 20_000;
    private const int MaximumTotalMapEntries = 200_000;
    private const uint MaximumNativeOffset = 64 * 1024 * 1024;

    public static IReadOnlyDictionary<nuint, IReadOnlyList<JitRichMethodMap>> Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return Empty();

        try
        {
            var file = new FileInfo(path);
            if (file.Length is <= 0 or > MaximumFileBytes)
                return Empty();

            using var reader = new StreamReader(
                new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            if (!string.Equals(reader.ReadLine(), "SLJR1", StringComparison.Ordinal))
                return Empty();

            var methods = new Dictionary<nuint, List<JitRichMethodMap>>();
            var totalRecords = 0;
            var totalMapEntries = 0;
            while (reader.ReadLine() is { } line)
            {
                if (totalRecords >= MaximumRecords)
                    return Empty();
                var parsed = ParseLine(line);
                if (parsed is null || parsed.Points.Count > MaximumTotalMapEntries - totalMapEntries)
                    return Empty();

                if (!methods.TryGetValue(parsed.MethodHandle, out var versions))
                {
                    if (methods.Count >= MaximumMethods)
                        return Empty();
                    versions = [];
                    methods.Add(parsed.MethodHandle, versions);
                }
                if (versions.Count >= MaximumNativeCodeVersions ||
                    versions.Any(version =>
                        version.ClrInstanceId == parsed.ClrInstanceId &&
                        version.NativeVersionId == parsed.NativeVersionId &&
                        version.IlVersionId == parsed.IlVersionId))
                {
                    return Empty();
                }

                versions.Add(parsed);
                totalRecords++;
                totalMapEntries += parsed.Points.Count;
            }

            return methods.ToDictionary(
                static pair => pair.Key,
                static pair => (IReadOnlyList<JitRichMethodMap>)pair.Value.ToArray());
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            ArgumentException)
        {
            return Empty();
        }
    }

    internal static JitRichMethodMap? ParseLine(string line)
    {
        if (line.Length is <= 0 or > 1_000_000)
            return null;

        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 7 ||
            !TryParseNonZeroHexField(fields[0], "method=", out var methodHandle) ||
            !TryParseUShortField(fields[1], "clr=", out var clrInstanceId) ||
            !TryParseHexField(fields[2], "nativeversion=", out var nativeVersionId) ||
            !TryParseHexField(fields[3], "ilversion=", out var ilVersionId) ||
            !TryParseDecimalField(fields[4], "inline=", out var inlineNodeCount) ||
            inlineNodeCount is <= 0 or > MaximumInlineNodes ||
            !TryParseDecimalField(fields[5], "count=", out var count) ||
            count is <= 0 or > MaximumMapEntriesPerMethod ||
            fields.Length != count + 6)
        {
            return null;
        }

        var points = new JitRichIlPoint[count];
        uint previousNativeOffset = 0;
        for (var index = 0; index < count; index++)
        {
            var parts = fields[index + 6].Split(':');
            if (parts.Length != 4 ||
                !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var nativeOffset) ||
                nativeOffset > MaximumNativeOffset ||
                (index > 0 && nativeOffset < previousNativeOffset) ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ilOffset) ||
                !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var inlinee) ||
                inlinee < 0 || inlinee >= inlineNodeCount ||
                !byte.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var source) ||
                source > 0x1f)
            {
                return null;
            }

            points[index] = new JitRichIlPoint(nativeOffset, ilOffset, inlinee, source);
            previousNativeOffset = nativeOffset;
        }

        return new JitRichMethodMap(
            methodHandle,
            clrInstanceId,
            nativeVersionId,
            ilVersionId,
            inlineNodeCount,
            points);
    }

    private static Dictionary<nuint, IReadOnlyList<JitRichMethodMap>> Empty() =>
        new Dictionary<nuint, IReadOnlyList<JitRichMethodMap>>();

    private static bool TryParseNonZeroHexField(string field, string prefix, out nuint value)
    {
        value = 0;
        return field.StartsWith(prefix, StringComparison.Ordinal) &&
            nuint.TryParse(
                field.AsSpan(prefix.Length),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out value) &&
            value != 0;
    }

    private static bool TryParseHexField(string field, string prefix, out ulong value)
    {
        value = 0;
        return field.StartsWith(prefix, StringComparison.Ordinal) &&
            ulong.TryParse(
                field.AsSpan(prefix.Length),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out value);
    }

    private static bool TryParseUShortField(string field, string prefix, out ushort value)
    {
        value = 0;
        return field.StartsWith(prefix, StringComparison.Ordinal) &&
            ushort.TryParse(
                field.AsSpan(prefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out value);
    }

    private static bool TryParseDecimalField(string field, string prefix, out int value)
    {
        value = 0;
        return field.StartsWith(prefix, StringComparison.Ordinal) &&
            int.TryParse(
                field.AsSpan(prefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out value);
    }
}

internal sealed record JitRichMethodMap(
    nuint MethodHandle,
    ushort ClrInstanceId,
    ulong NativeVersionId,
    ulong IlVersionId,
    int InlineNodeCount,
    IReadOnlyList<JitRichIlPoint> Points);

internal sealed record JitRichIlPoint(
    uint NativeOffset,
    int IlOffset,
    int Inlinee,
    byte Source);
