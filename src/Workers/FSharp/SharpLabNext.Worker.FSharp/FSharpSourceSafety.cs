using SharpLabNext.Worker.FSharp.Compiler;

namespace SharpLabNext.Worker.FSharp;

internal static class FSharpSourceSafety
{
    private static readonly HashSet<string> RejectedDirectives = new(
        ["r", "load", "i", "cd", "time", "help", "quit"],
        StringComparer.OrdinalIgnoreCase);

    public static async Task<string?> FindRejectedDirectiveAsync(
        FSharpCompilerFacade compiler,
        FSharpProjectInput projectInput,
        string fileName,
        string sourceText,
        CancellationToken cancellationToken)
    {
        var directives = await compiler.GetHashDirectivesAsync(
            projectInput,
            fileName,
            sourceText,
            cancellationToken).ConfigureAwait(false);
        return directives.FirstOrDefault(RejectedDirectives.Contains);
    }
}
