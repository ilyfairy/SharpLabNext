using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpLabNext.Catalog;
using SharpLabNext.Contracts;
using SharpLabNext.PipelineResolver;
using Resolver = SharpLabNext.PipelineResolver.PipelineResolver;

namespace SharpLabNext.CompatibilityCli;

public static class CompatibilityCliProgram
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var command = CompatibilityCommand.Parse(args);
            var catalogTask = CatalogLoader.LoadCatalogAsync(command.CatalogPath);
            var lockTask = CatalogLoader.LoadReleaseLockAsync(command.LockPath);
            await Task.WhenAll(catalogTask, lockTask);
            var catalog = await catalogTask;
            var releaseLock = await lockTask;
            if (command.Kind == CompatibilityCommandKind.Resolve)
            {
                return await ResolveAsync(command, catalog);
            }

            var report = CompatibilityAuditor.Audit(catalog, releaseLock, DateTimeOffset.UtcNow);
            var content = command.Format == CompatibilityOutputFormat.Markdown
                ? ToMarkdown(report) : JsonSerializer.Serialize(report, JsonOptions) + Environment.NewLine;
            await WriteOutputAsync(command.OutputPath, content);
            return report.IsValid ? 0 : 2;
        }
        catch (CompatibilityUsageException exception)
        {
            Console.Error.WriteLine(exception.Message);
            if (!string.Equals(exception.Message, CompatibilityCommand.Usage, StringComparison.Ordinal))
            {
                Console.Error.WriteLine(CompatibilityCommand.Usage);
            }
            return 64;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Compatibility validation failed: {exception.Message}");
            return 1;
        }
    }

    public static string ToMarkdown(CompatibilityAuditReport report)
    {
        var builder = new StringBuilder();
        builder.Append("# SharpLabNext Compatibility Report ").AppendLine(report.ReleaseId);
        builder.AppendLine();
        builder.Append("Catalog: `").Append(report.CatalogRevision).AppendLine("`");
        builder.Append("Status: **").Append(report.IsValid ? "valid" : "invalid").AppendLine("**");
        builder.AppendLine();
        builder.AppendLine("## Issues");
        builder.AppendLine();
        if (report.Issues.Count == 0)
        {
            builder.AppendLine("None.");
        }
        else
        {
            foreach (var issue in report.Issues)
                builder.Append("- ").AppendLine(issue);
        }

        builder.AppendLine();
        builder.AppendLine("## Matrix");
        builder.AppendLine();
        builder.AppendLine("| Language | Toolchain | API | Output | Runtime | Result |");
        builder.AppendLine("| --- | --- | --- | --- | --- | --- |");
        foreach (var entry in report.Matrix)
            builder.Append("| ").Append(entry.LanguageId).Append(" | ").Append(entry.ToolchainId).Append(" | ").Append(entry.ReferenceSetId).Append(" | ").Append(entry.OutputId).Append(" | ").Append(entry.RuntimeId ?? "-").Append(" | ").Append(entry.Disposition.ToString().ToLowerInvariant()).AppendLine(" |");

        return builder.ToString().ReplaceLineEndings("\n");
    }

    private static async Task<int> ResolveAsync(CompatibilityCommand command, CatalogDocument catalog)
    {
        try
        {
            var response = Resolver.Resolve(catalog, new ResolveSelectionRequest(command.LanguageId!, command.ToolchainId, command.ReferenceSetId, command.OutputId!, command.RuntimeId, command.BuildMode, catalog.Revision, 1), DateTimeOffset.UtcNow);
            await WriteOutputAsync(command.OutputPath, JsonSerializer.Serialize(response, JsonOptions) + Environment.NewLine);
            return 0;
        }
        catch (SelectionResolutionException exception)
        {
            var error = new { exception.Code, field = exception.Field, exception.Value, exception.Message };
            await WriteOutputAsync(command.OutputPath, JsonSerializer.Serialize(error, JsonOptions) + Environment.NewLine, useStandardError: command.OutputPath is null);
            return 2;
        }
    }

    private static async Task WriteOutputAsync(string? path, string content, bool useStandardError = false)
    {
        if (path is null)
        {
            if (useStandardError)
            {
                Console.Error.Write(content);
            }
            else
            {
                Console.Write(content);
            }
            return;
        }

        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Output path has no parent directory.");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, content);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }
}
