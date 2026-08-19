using System.Text;
using System.Text.Json;
using Mobius.ILasm.Core;
using Mobius.ILasm.interfaces;
using Mono.ILASM;
using SharpLabNext.Worker.IL.Compiler;

return await IlCompilerProgram.RunAsync(args).ConfigureAwait(false);

internal static class IlCompilerProgram
{
    public static async Task<int> RunAsync(string[] arguments)
    {
        if (arguments is ["--describe"])
        {
            var assemblyVersion = typeof(Driver).Assembly.GetName().Version?.ToString() ?? "unknown";
            Console.Write(JsonSerializer.Serialize(
                new IlCompilerDescriptor(
                    IlCompilerProtocol.Version,
                    "Mobius.ILasm",
                    IlCompilerProtocol.PackageVersion,
                    assemblyVersion),
                IlCompilerProtocol.JsonOptions));
            return 0;
        }

        if (arguments is not ["--compile", var requestPath, var responsePath, var outputPath])
            return 2;

        try
        {
            var requestInfo = new FileInfo(requestPath);
            if (!requestInfo.Exists || requestInfo.Length is <= 0 or > IlCompilerProtocol.MaxRequestBytes)
                return 2;

            await using var requestStream = new FileStream(
                requestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var request = await JsonSerializer.DeserializeAsync<IlCompilerRequest>(
                    requestStream,
                    IlCompilerProtocol.JsonOptions)
                .ConfigureAwait(false);
            if (request is null || request.ProtocolVersion != IlCompilerProtocol.Version ||
                request.MaxPeBytes is <= 0 or > IlCompilerProtocol.MaxPeBytes ||
                request.Sources.Count is <= 0 or > IlCompilerProtocol.MaxSources ||
                request.Target is not ("dll" or "exe"))
            {
                return 2;
            }

            var response = Assemble(request, outputPath);
            var responseBytes = JsonSerializer.SerializeToUtf8Bytes(response, IlCompilerProtocol.JsonOptions);
            if (responseBytes.Length > IlCompilerProtocol.MaxResponseBytes)
                return 3;
            await File.WriteAllBytesAsync(responsePath, responseBytes).ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception) when (exception is not (StackOverflowException or OutOfMemoryException))
        {
            try
            {
                var response = new IlCompilerResponse(
                    IlCompilerProtocol.Version,
                    false,
                    [new IlCompilerDiagnostic(
                        IlCompilerDiagnosticSeverity.Error,
                        "ILASM999",
                        Limit(exception.Message, 4_096),
                        null,
                        null,
                        null,
                        null,
                        null)],
                    "compiler-exception");
                var bytes = JsonSerializer.SerializeToUtf8Bytes(response, IlCompilerProtocol.JsonOptions);
                await File.WriteAllBytesAsync(responsePath, bytes).ConfigureAwait(false);
                return 0;
            }
            catch
            {
                return 3;
            }
        }
    }

    private static IlCompilerResponse Assemble(IlCompilerRequest request, string outputPath)
    {
        var source = CompositeIlSource.Create(request.Sources);
        var logger = new BoundedCompilerLogger(source, IlCompilerProtocol.MaxDiagnostics);
        if (ContainsExternalResourceDirective(source.Text))
        {
            logger.Error("Manifest resource directives are disabled because Mobius.ILasm 0.1.0 resolves resource paths from the process filesystem.");
            return new IlCompilerResponse(
                IlCompilerProtocol.Version,
                false,
                logger.Diagnostics,
                "external-resource-disabled");
        }
        var target = request.Target == "exe" ? Driver.Target.Exe : Driver.Target.Dll;
        var driver = new Driver(logger, target, showParser: false, debuggingInfo: false, showTokens: false);
        using var output = new CappedMemoryStream(request.MaxPeBytes);
        bool succeeded;
        try
        {
            succeeded = driver.Assemble([source.Text], output) && !logger.HasErrors;
        }
        catch (Exception exception) when (exception is not (StackOverflowException or OutOfMemoryException))
        {
            logger.Error($"Assembler exception: {Limit(exception.Message, 4_096)}");
            succeeded = false;
        }

        if (succeeded && output.Length > 0)
        {
            using var file = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            output.Position = 0;
            output.CopyTo(file);
        }
        else
        {
            succeeded = false;
            if (logger.Diagnostics.Count == 0)
                logger.Error("The assembler did not produce a managed PE image.");
        }

        return new IlCompilerResponse(
            IlCompilerProtocol.Version,
            succeeded,
            logger.Diagnostics,
            succeeded ? null : "assembly-failed");
    }

    private static string Limit(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];

    private static bool ContainsExternalResourceDirective(string text)
    {
        var inString = false;
        var inLineComment = false;
        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            if (inLineComment)
            {
                if (current == '\n')
                    inLineComment = false;
                continue;
            }
            if (current == '"' && (index == 0 || text[index - 1] != '\\'))
            {
                inString = !inString;
                continue;
            }
            if (inString)
                continue;
            if (current == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                inLineComment = true;
                index++;
                continue;
            }
            if (current != '.')
                continue;
            const string directive = ".mresource";
            if (index + directive.Length <= text.Length &&
                text.AsSpan(index, directive.Length).Equals(directive, StringComparison.OrdinalIgnoreCase) &&
                (index + directive.Length == text.Length ||
                 !IsDirectiveIdentifierCharacter(text[index + directive.Length])))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsDirectiveIdentifierCharacter(char value) =>
        char.IsLetterOrDigit(value) || value is '_' or '.' or '$' or '?' or '@';

    private sealed class BoundedCompilerLogger(CompositeIlSource source, int maximumDiagnostics) : ILogger
    {
        private readonly List<IlCompilerDiagnostic> _diagnostics = [];

        public List<IlCompilerDiagnostic> Diagnostics => _diagnostics;
        public bool HasErrors { get; private set; }

        public void Info(string message)
        {
        }

        public void Error(string message) => Add(IlCompilerDiagnosticSeverity.Error, null, message);

        public void Error(Location location, string message) =>
            Add(IlCompilerDiagnosticSeverity.Error, location, message);

        public void Warning(string message) => Add(IlCompilerDiagnosticSeverity.Warning, null, message);

        public void Warning(Location location, string message) =>
            Add(IlCompilerDiagnosticSeverity.Warning, location, message);

        private void Add(IlCompilerDiagnosticSeverity severity, Location? location, string message)
        {
            if (severity == IlCompilerDiagnosticSeverity.Error)
                HasErrors = true;
            if (_diagnostics.Count >= maximumDiagnostics)
                return;
            var mapped = location is null ? null : source.Map(location.line, location.column);
            _diagnostics.Add(new IlCompilerDiagnostic(
                severity,
                severity == IlCompilerDiagnosticSeverity.Warning ? "ILASMW001" : "ILASM001",
                Limit(message, 8_192),
                mapped?.Path,
                mapped?.Line,
                mapped?.Character,
                mapped?.Line,
                mapped is null ? null : mapped.Character + 1));
        }
    }

    private sealed record CompositeIlSource(string Text, IReadOnlyList<SourceSegment> Segments)
    {
        public static CompositeIlSource Create(IReadOnlyList<IlCompilerSource> sources)
        {
            var text = new StringBuilder();
            var segments = new List<SourceSegment>(sources.Count);
            var nextStartLine = 0;
            foreach (var source in sources)
            {
                if (source.Path.Length is 0 or > 240 || source.Text.Length > 2 * 1024 * 1024)
                    throw new InvalidDataException("IL compiler source input exceeds a child-process limit.");
                var normalized = source.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
                var lineCount = normalized.Count(static character => character == '\n') + 1;
                segments.Add(new SourceSegment(source.Path, nextStartLine, lineCount));
                text.Append(normalized);
                if (normalized.Length == 0 || normalized[^1] != '\n')
                {
                    text.Append('\n');
                    nextStartLine += lineCount;
                }
                else
                {
                    // A trailing newline already starts the next physical line. The
                    // source's final empty logical line and the next source share that
                    // coordinate, so later segments must win during reverse lookup.
                    nextStartLine += lineCount - 1;
                }
            }
            return new CompositeIlSource(text.ToString(), segments);
        }

        public MappedLocation Map(int line, int character)
        {
            var safeLine = Math.Max(0, line);
            var segment = Segments.LastOrDefault(item => item.StartLine <= safeLine) ?? Segments[0];
            var localLine = Math.Clamp(safeLine - segment.StartLine, 0, Math.Max(0, segment.LineCount - 1));
            return new MappedLocation(segment.Path, localLine, Math.Max(0, character));
        }
    }

    private sealed record SourceSegment(string Path, int StartLine, int LineCount);
    private sealed record MappedLocation(string Path, int Line, int Character);

    private sealed class CappedMemoryStream(int maximumBytes) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacity(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacity(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            EnsureCapacity(1);
            base.WriteByte(value);
        }

        public override void SetLength(long value)
        {
            if (value > maximumBytes)
                throw new IOException("The assembled PE exceeds the configured size limit.");
            base.SetLength(value);
        }

        private void EnsureCapacity(int additionalBytes)
        {
            if (Position + additionalBytes > maximumBytes)
                throw new IOException("The assembled PE exceeds the configured size limit.");
        }
    }
}
