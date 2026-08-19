using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using ICSharpCode.Decompiler.DebugInfo;
using SequencePoint = ICSharpCode.Decompiler.DebugInfo.SequencePoint;
using Variable = ICSharpCode.Decompiler.DebugInfo.Variable;

namespace SharpLabNext.ArtifactProcessing;

internal sealed class PortablePdbDebugInfoProvider : IDebugInfoProvider, IDisposable
{
    private readonly MetadataReaderProvider _provider;
    private readonly MetadataReader _reader;

    private PortablePdbDebugInfoProvider(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            _provider = MetadataReaderProvider.FromPortablePdbStream(
                stream,
                MetadataStreamOptions.PrefetchMetadata);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
        _reader = _provider.GetMetadataReader(MetadataReaderOptions.ApplyWindowsRuntimeProjections);
        SourceFileName = SanitizeDocumentPath(path);
    }

    public string Description => "Portable PDB";

    public string SourceFileName { get; }

    public static PortablePdbDebugInfoProvider? TryOpen(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : new PortablePdbDebugInfoProvider(path);

    public IList<SequencePoint> GetSequencePoints(MethodDefinitionHandle method)
    {
        var handle = MetadataTokens.MethodDebugInformationHandle(MetadataTokens.GetRowNumber(method));
        if (handle.IsNil)
            return [];
        var information = _reader.GetMethodDebugInformation(handle);
        var result = new List<SequencePoint>();
        foreach (var point in information.GetSequencePoints())
        {
            var documentHandle = point.Document.IsNil ? information.Document : point.Document;
            var document = documentHandle.IsNil
                ? SourceFileName
                : SanitizeDocumentPath(_reader.GetString(_reader.GetDocument(documentHandle).Name));
            result.Add(new SequencePoint
            {
                Offset = point.Offset,
                EndOffset = point.Offset,
                StartLine = point.StartLine,
                StartColumn = point.StartColumn,
                EndLine = point.EndLine,
                EndColumn = point.EndColumn,
                DocumentUrl = document
            });
        }

        for (var index = 0; index + 1 < result.Count; index++)
            result[index].EndOffset = result[index + 1].Offset;
        return result;
    }

    public IList<Variable> GetVariables(MethodDefinitionHandle method)
    {
        var variables = new Dictionary<int, string>();
        foreach (var scopeHandle in _reader.GetLocalScopes(method))
        {
            var scope = _reader.GetLocalScope(scopeHandle);
            foreach (var variableHandle in scope.GetLocalVariables())
            {
                var variable = _reader.GetLocalVariable(variableHandle);
                variables.TryAdd(variable.Index, _reader.GetString(variable.Name));
            }
        }
        return variables.Select(static pair => new Variable(pair.Key, pair.Value)).ToArray();
    }

    public bool TryGetExtraTypeInfo(
        MethodDefinitionHandle method,
        int index,
        out PdbExtraTypeInfo extraTypeInfo)
    {
        extraTypeInfo = default;
        return false;
    }

    public bool TryGetName(MethodDefinitionHandle method, int index, out string name)
    {
        var variable = GetVariables(method).FirstOrDefault(candidate => candidate.Index == index);
        name = variable.Name;
        return !string.IsNullOrEmpty(name);
    }

    public void Dispose() => _provider.Dispose();

    internal static string SanitizeDocumentPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(static segment => segment is not "." and not "..")
            .TakeLast(8)
            .ToArray();
        if (segments.Length == 0)
            return "source";
        return string.Join('/', segments);
    }
}
