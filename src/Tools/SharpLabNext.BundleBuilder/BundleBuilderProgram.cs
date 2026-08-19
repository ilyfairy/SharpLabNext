using System.Text.Json;

namespace SharpLabNext.BundleBuilder;

public static class BundleBuilderProgram
{
    private static readonly JsonSerializerOptions OutputJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var command = BundleBuilderCommand.Parse(args);
            var builder = new ReleaseBundleBuilder(new DockerCli(command.DockerCommand));
            var result = await builder.BuildAsync(command, CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                outputDirectory = result.OutputDirectory,
                releaseId = result.ReleaseId,
                containsImages = result.ContainsImages,
                hasSignature = result.HasSignature,
                images = result.Images.Select(static image => new
                {
                    image.Id,
                    image.SourceReference,
                    image.ImageId
                })
            }, OutputJsonOptions));
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
}
