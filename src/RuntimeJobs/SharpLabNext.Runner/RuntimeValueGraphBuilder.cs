using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using SharpLabNext.RuntimeProtocol;

internal static class RuntimeValueGraphBuilder
{
    private const int MaximumDepth = 6;
    private const int MaximumNodes = 512;
    private const int MaximumEdgesPerNode = 64;
    private const int MaximumStringCharacters = 4_096;
    private static readonly TimeSpan MaximumDuration = TimeSpan.FromMilliseconds(50);

    public static RuntimeGraphDocument Build(IEnumerable<(string Name, object? Value)> roots)
    {
        var builder = new Builder();
        return builder.Build(roots);
    }

    private sealed class Builder
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly Dictionary<object, int> _seen = new(ReferenceEqualityComparer.Instance);
        private readonly List<RuntimeGraphNode> _nodes = [];
        private int? _truncatedNodeId;
        private bool _truncated;
        private string? _reason;

        public RuntimeGraphDocument Build(IEnumerable<(string Name, object? Value)> roots)
        {
            var graphRoots = new List<RuntimeGraphRoot>();
            foreach (var (name, value) in roots)
            {
                graphRoots.Add(new RuntimeGraphRoot(SanitizeName(name), Add(value, 0)));
            }
            return new RuntimeGraphDocument(graphRoots, _nodes, _truncated, _reason);
        }

        private int Add(object? value, int depth)
        {
            if (LimitReached(depth))
            {
                return AddTruncatedTerminal();
            }
            if (value is null)
            {
                return AddTerminal("null", "null", null);
            }

            var type = value.GetType();
            if (!type.IsValueType && _seen.TryGetValue(value, out var existing))
            {
                return existing;
            }

            var id = _nodes.Count + 1;
            if (!type.IsValueType)
            {
                _seen[value] = id;
            }
            _nodes.Add(new RuntimeGraphNode(id, SafeTypeName(type), "pending", null, []));

            RuntimeGraphNode node;
            if (TryFormatScalar(value, type, out var scalar))
            {
                node = new RuntimeGraphNode(id, SafeTypeName(type), "scalar", scalar, []);
            }
            else if (value is Array array)
            {
                node = ExpandArray(id, type, array, depth);
            }
            else if (IsExactList(type) && value is IEnumerable list)
            {
                node = ExpandEnumerable(id, type, list, depth, "list");
            }
            else if (IsExactDictionary(type) && value is IEnumerable dictionary)
            {
                node = ExpandEnumerable(id, type, dictionary, depth, "dictionary");
            }
            else if (IsInspectableUserType(type))
            {
                node = ExpandFields(id, type, value, depth);
            }
            else
            {
                node = new RuntimeGraphNode(id, SafeTypeName(type), "opaque", null, []);
            }

            _nodes[id - 1] = node;
            return id;
        }

        private RuntimeGraphNode ExpandArray(int id, Type type, Array array, int depth)
        {
            var edges = new List<RuntimeGraphEdge>();
            var count = Math.Min(array.Length, MaximumEdgesPerNode);
            for (var index = 0; index < count; index++)
            {
                if (LimitReached(depth + 1))
                {
                    break;
                }
                edges.Add(new RuntimeGraphEdge($"[{index}]", Add(array.GetValue(index), depth + 1)));
            }
            if (array.Length > count)
            {
                MarkTruncated("edge-limit");
            }
            return new RuntimeGraphNode(id, SafeTypeName(type), "array", $"Length = {array.Length}", edges);
        }

        private RuntimeGraphNode ExpandEnumerable(
            int id,
            Type type,
            IEnumerable enumerable,
            int depth,
            string kind)
        {
            var edges = new List<RuntimeGraphEdge>();
            try
            {
                var index = 0;
                foreach (var item in enumerable)
                {
                    if (index >= MaximumEdgesPerNode || LimitReached(depth + 1))
                    {
                        MarkTruncated("edge-limit");
                        break;
                    }
                    edges.Add(new RuntimeGraphEdge($"[{index}]", Add(item, depth + 1)));
                    index++;
                }
            }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
            {
                edges.Add(new RuntimeGraphEdge("<enumeration-error>", AddTerminal(
                    exception.GetType().FullName ?? "System.Exception",
                    "error",
                    "Collection could not be inspected.")));
            }
            return new RuntimeGraphNode(id, SafeTypeName(type), kind, null, edges);
        }

        private RuntimeGraphNode ExpandFields(int id, Type type, object value, int depth)
        {
            var edges = new List<RuntimeGraphEdge>();
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(static field => !field.IsStatic)
                .OrderBy(static field => field.MetadataToken)
                .Take(MaximumEdgesPerNode + 1)
                .ToArray();
            foreach (var field in fields.Take(MaximumEdgesPerNode))
            {
                if (LimitReached(depth + 1))
                {
                    break;
                }
                object? fieldValue;
                try
                {
                    fieldValue = field.GetValue(value);
                }
                catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
                {
                    fieldValue = $"<{exception.GetType().Name}>";
                }
                edges.Add(new RuntimeGraphEdge(SanitizeName(field.Name), Add(fieldValue, depth + 1)));
            }
            if (fields.Length > MaximumEdgesPerNode)
            {
                MarkTruncated("edge-limit");
            }
            return new RuntimeGraphNode(id, SafeTypeName(type), "object", null, edges);
        }

        private int AddTerminal(string typeName, string kind, string? display)
        {
            if (_nodes.Count >= MaximumNodes - 1)
            {
                MarkTruncated("node-limit");
                return AddTruncatedTerminal();
            }
            var id = _nodes.Count + 1;
            _nodes.Add(new RuntimeGraphNode(id, typeName, kind, display, []));
            return id;
        }

        private int AddTruncatedTerminal()
        {
            if (_truncatedNodeId is { } existing)
                return existing;
            var id = _nodes.Count + 1;
            _nodes.Add(new RuntimeGraphNode(id, "System.Object", "truncated", _reason, []));
            _truncatedNodeId = id;
            return id;
        }

        private bool LimitReached(int depth)
        {
            if (depth > MaximumDepth)
            {
                MarkTruncated("depth-limit");
                return true;
            }
            if (_nodes.Count >= MaximumNodes - 1)
            {
                MarkTruncated("node-limit");
                return true;
            }
            if (_stopwatch.Elapsed > MaximumDuration)
            {
                MarkTruncated("time-limit");
                return true;
            }
            return false;
        }

        private void MarkTruncated(string reason)
        {
            _truncated = true;
            _reason ??= reason;
        }
    }

    private static bool TryFormatScalar(object value, Type type, out string? display)
    {
        switch (value)
        {
            case string text:
                display = text.Length <= MaximumStringCharacters
                    ? text
                    : text[..MaximumStringCharacters] + "...";
                return true;
            case char character:
                display = character.ToString();
                return true;
            case bool boolean:
                display = boolean ? "true" : "false";
                return true;
            case DateTime dateTime:
                display = dateTime.ToString("O", CultureInfo.InvariantCulture);
                return true;
            case DateTimeOffset dateTimeOffset:
                display = dateTimeOffset.ToString("O", CultureInfo.InvariantCulture);
                return true;
            case TimeSpan timeSpan:
                display = timeSpan.ToString("c", CultureInfo.InvariantCulture);
                return true;
            case Guid guid:
                display = guid.ToString("D");
                return true;
            case Uri uri:
                display = uri.IsAbsoluteUri ? uri.GetComponents(UriComponents.AbsoluteUri, UriFormat.SafeUnescaped) : uri.OriginalString;
                return true;
            case Type reflectedType:
                display = SafeTypeName(reflectedType);
                return true;
            case IFormattable formattable when type.IsPrimitive || type.IsEnum || value is decimal:
                display = formattable.ToString(null, CultureInfo.InvariantCulture);
                return true;
            default:
                display = null;
                return false;
        }
    }

    private static bool IsInspectableUserType(Type type)
    {
        var assembly = type.Assembly;
        return assembly != typeof(object).Assembly &&
               assembly != typeof(RuntimeValueGraphBuilder).Assembly &&
               !type.IsPointer &&
               !type.IsByRefLike;
    }

    private static bool IsExactList(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>);

    private static bool IsExactDictionary(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>);

    private static string SafeTypeName(Type type) =>
        type.FullName ?? type.Name;

    private static string SanitizeName(string name)
    {
        var clean = name.Replace('\r', ' ').Replace('\n', ' ').Replace('\0', ' ');
        return clean.Length <= 256 ? clean : clean[..256];
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static ReferenceEqualityComparer Instance { get; } = new();

        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
