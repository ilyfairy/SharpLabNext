using System.Globalization;

internal static class JitNativeMapLog
{
    private const long MaximumFileBytes = 8 * 1024 * 1024;
    private const int MaximumMethods = 1_000;
    private const int MaximumRecords = 1_000;
    private const int MaximumNativeCodeVersions = 8;
    private const int MaximumMapEntriesPerMethod = 20_000;
    private const int MaximumTotalMapEntries = 200_000;
    private const uint MaximumNativeOffset = 64 * 1024 * 1024;

    public static IReadOnlyDictionary<nuint, IReadOnlyList<JitNativeMethodMap>> Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new Dictionary<nuint, IReadOnlyList<JitNativeMethodMap>>();

        try
        {
            var file = new FileInfo(path);
            if (file.Length is <= 0 or > MaximumFileBytes)
                return new Dictionary<nuint, IReadOnlyList<JitNativeMethodMap>>();

            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            if (!string.Equals(reader.ReadLine(), "SLJM1", StringComparison.Ordinal))
                return new Dictionary<nuint, IReadOnlyList<JitNativeMethodMap>>();

            var methods = new Dictionary<nuint, List<JitNativeMethodMap>>();
            var totalRecords = 0;
            var totalMapEntries = 0;
            while (totalRecords < MaximumRecords && reader.ReadLine() is { } line)
            {
                var parsed = ParseLine(line);
                if (parsed is null || parsed.Ranges.Count > MaximumTotalMapEntries - totalMapEntries)
                    continue;

                if (!methods.TryGetValue(parsed.MethodHandle, out var versions))
                {
                    if (methods.Count >= MaximumMethods)
                        continue;
                    versions = [];
                    methods.Add(parsed.MethodHandle, versions);
                }
                if (versions.Count >= MaximumNativeCodeVersions || versions.Any(version => version.NativeCodeStart == parsed.NativeCodeStart))
                    continue;

                versions.Add(parsed);
                totalRecords++;
                totalMapEntries += parsed.Ranges.Count;
            }
            return methods.ToDictionary(static pair => pair.Key, static pair => (IReadOnlyList<JitNativeMethodMap>)pair.Value.ToArray());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return new Dictionary<nuint, IReadOnlyList<JitNativeMethodMap>>();
        }
    }

    internal static JitNativeMethodMap? ParseLine(string line)
    {
        if (line.Length is <= 0 or > 1_000_000)
            return null;

        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 5 || !TryParseHexField(fields[0], "handle=", out var methodHandle) || !TryParseHexField(fields[1], "token=", out var metadataTokenValue) || metadataTokenValue > int.MaxValue || (metadataTokenValue & 0xff000000) != 0x06000000 || !TryParseHexField(fields[2], "native=", out var nativeCodeStart) || !TryParseDecimalField(fields[3], "count=", out var count) || count is <= 0 or > MaximumMapEntriesPerMethod || fields.Length != count + 4)
        {
            return null;
        }

        var ranges = new JitNativeIlRange[count];
        for (var index = 0; index < count; index++)
        {
            var parts = fields[index + 4].Split(':');
            if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var ilOffset) || !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var nativeStart) || !uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var nativeEnd) || nativeEnd < nativeStart || nativeEnd > MaximumNativeOffset)
            {
                return null;
            }
            ranges[index] = new JitNativeIlRange(ilOffset, nativeStart, nativeEnd);
        }

        var orderedRanges = ranges.OrderBy(static range => range.NativeStart).ThenBy(static range => range.NativeEnd).ToArray();
        uint previousNativeEnd = 0;
        var hasPreviousRange = false;
        foreach (var range in orderedRanges)
        {
            if (range.NativeStart == range.NativeEnd)
                continue;
            if (hasPreviousRange && range.NativeStart < previousNativeEnd)
                return null;
            previousNativeEnd = range.NativeEnd;
            hasPreviousRange = true;
        }

        return new JitNativeMethodMap(methodHandle, (int)metadataTokenValue, nativeCodeStart, orderedRanges);
    }

    private static bool TryParseHexField(string field, string prefix, out nuint value)
    {
        value = 0;
        return field.StartsWith(prefix, StringComparison.Ordinal) &&
            nuint.TryParse(field.AsSpan(prefix.Length), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value) &&
            value != 0;
    }

    private static bool TryParseDecimalField(string field, string prefix, out int value)
    {
        value = 0;
        return field.StartsWith(prefix, StringComparison.Ordinal) &&
            int.TryParse(field.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}

internal sealed record JitNativeMethodMap(nuint MethodHandle, int MetadataToken, nuint NativeCodeStart, IReadOnlyList<JitNativeIlRange> Ranges);

internal sealed record JitNativeIlRange(int IlOffset, uint NativeStart, uint NativeEnd);
