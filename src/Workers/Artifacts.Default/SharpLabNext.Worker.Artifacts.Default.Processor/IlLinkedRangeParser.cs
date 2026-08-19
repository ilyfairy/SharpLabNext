using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SharpLabNext.ArtifactProcessing.Protocol;

namespace SharpLabNext.ArtifactProcessing;

internal static partial class IlLinkedRangeParser
{
    private const int MaximumLinkedRanges = 20_000;

    public static async Task<IlLinkedDocument> ParseAndStripAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var result = new List<ProcessorLinkedRange>();
        var filteredPath = path + ".filtered";
        var hasFinalLineBreak = HasFinalLineBreak(path);
        long charactersWritten = 0;
        try
        {
            using (var reader = new StreamReader(path))
            await using (var file = new FileStream(
                filteredPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var writer = new StreamWriter(
                file,
                new UTF8Encoding(false),
                64 * 1024,
                leaveOpen: true) { NewLine = "\n" })
            {
                var visibleLineNumber = 0;
                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    var match = SequencePointPattern().Match(line);
                    if (match.Success && result.Count < MaximumLinkedRanges)
                    {
                        result.Add(new ProcessorLinkedRange(
                            PortablePdbDebugInfoProvider.SanitizeDocumentPath(match.Groups[5].Value),
                            new ProcessorTextRange(
                                ParseCoordinate(match.Groups[1].Value),
                                ParseCoordinate(match.Groups[2].Value),
                                ParseCoordinate(match.Groups[3].Value),
                                ParseCoordinate(match.Groups[4].Value)),
                            new ProcessorTextRange(visibleLineNumber, 0, visibleLineNumber, 1)));
                    }
                    if (SequencePointCommentPattern().IsMatch(line))
                        continue;

                    await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
                    charactersWritten += line.Length + 1L;
                    visibleLineNumber++;
                }
                await writer.FlushAsync(cancellationToken);
                if (!hasFinalLineBreak && charactersWritten > 0)
                {
                    file.SetLength(file.Length - 1);
                    charactersWritten--;
                }
                await file.FlushAsync(cancellationToken);
            }

            File.Move(filteredPath, path, overwrite: true);
            return new IlLinkedDocument(result, charactersWritten);
        }
        finally
        {
            File.Delete(filteredPath);
        }
    }

    private static int ParseCoordinate(string value) =>
        Math.Max(0, int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture) - 1);

    private static bool HasFinalLineBreak(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length == 0)
            return false;
        stream.Position = stream.Length - 1;
        return stream.ReadByte() is '\r' or '\n';
    }

    [GeneratedRegex(
        @"^\s*// sequence point: \(line (\d+), col (\d+)\) to \(line (\d+), col (\d+)\) in (.+)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SequencePointPattern();

    [GeneratedRegex(@"^\s*// sequence point(?::.*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SequencePointCommentPattern();
}

internal sealed record IlLinkedDocument(
    IReadOnlyList<ProcessorLinkedRange> LinkedRanges,
    long CharactersWritten);
