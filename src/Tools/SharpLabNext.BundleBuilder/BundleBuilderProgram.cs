using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpLabNext.BundleBuilder;

public static class BundleBuilderProgram
{
    private static readonly JsonSerializerOptions OutputJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var command = BundleBuilderCommand.Parse(args);
            if (command.ImagePlanPath is not null)
            {
                var plan = await ReleaseBundleBuilder.CreateImagePlanAsync(command, CancellationToken.None);
                await WriteJsonAtomicallyAsync(command.ImagePlanPath, plan, CancellationToken.None);
                Console.WriteLine($"Release image plan written to {command.ImagePlanPath}");
                return 0;
            }
            var builder = new ReleaseBundleBuilder(new DockerCli(command.DockerCommand));
            var result = await builder.BuildAsync(command, CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(new { outputDirectory = result.OutputDirectory, releaseId = result.ReleaseId, containsImages = result.ContainsImages, hasSignature = result.HasSignature, images = result.Images.Select(static image => new { image.Id, image.SourceReference, image.ImageId }) }, OutputJsonOptions));
            return 0;
        }
        catch (BundleBuilderUsageException exception)
        {
            Console.Error.WriteLine(exception.Message);
            if (!string.Equals(exception.Message, BundleBuilderCommand.Usage, StringComparison.Ordinal))
            {
                Console.Error.WriteLine(BundleBuilderCommand.Usage);
            }
            return 64;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Bundle creation failed: {exception.Message}");
            return 1;
        }
    }

    private static async Task WriteJsonAtomicallyAsync(string path, ReleaseImagePlan plan, CancellationToken cancellationToken)
    {
        var output = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(output) ?? throw new BundleBuilderUsageException("Image plan output has no parent directory.");
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, $".{Path.GetFileName(output)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(plan, OutputJsonOptions) + Environment.NewLine, cancellationToken);
            File.Move(temporary, output, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { }
        }
    }
}
