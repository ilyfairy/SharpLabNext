using System.Text.Json;
using SharpLabNext.Worker.Artifacts.ConstGenerics.Processing;
using SharpLabNext.Worker.Artifacts.ConstGenerics.Protocol;

if (args is ["--describe"])
{
    var descriptor = new ConstGenericsProcessorDescriptor(ConstGenericsProcessorProtocol.Version, ConstGenericsProcessorProtocol.IlSpyCommit, ConstGenericsProcessorProtocol.RuntimeCommit, ConstGenericsProcessorProtocol.MetadataFeatureTag, ConstGenericsProcessorProtocol.CompatibilityGroup, ["il", "decompiled-csharp", "verify"]);
    Console.WriteLine(JsonSerializer.Serialize(descriptor, ConstGenericsProcessorProtocol.JsonOptions));
    return 0;
}

var parsed = ProcessorArguments.Parse(args);
ConstGenericsProcessorResponse response;
var operation = ConstGenericsProcessorOperation.Il;
try
{
    var requestInfo = new FileInfo(parsed.RequestPath);
    if (!requestInfo.Exists || requestInfo.Length is <= 0 or > ConstGenericsProcessorProtocol.MaximumRequestBytes)
        throw new InvalidDataException("The processor request is unavailable or exceeds its limit.");
    await using var requestStream = new FileStream(parsed.RequestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    var request = await JsonSerializer.DeserializeAsync<ConstGenericsProcessorRequest>(requestStream, ConstGenericsProcessorProtocol.JsonOptions) ?? throw new InvalidDataException("The processor request was empty.");
    operation = request.Operation;
    response = await ConstGenericsProcessorEngine.ExecuteAsync(request, CancellationToken.None);
}
catch (Exception exception)
{
    response = ConstGenericsProcessorEngine.ToFailureResponse(exception, operation);
}

var responseDirectory = Path.GetDirectoryName(parsed.ResponsePath);
if (!string.IsNullOrEmpty(responseDirectory))
    Directory.CreateDirectory(responseDirectory);
var temporaryResponse = parsed.ResponsePath + ".tmp";
await using (var responseStream = new FileStream(temporaryResponse, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
{
    await JsonSerializer.SerializeAsync(responseStream, response, ConstGenericsProcessorProtocol.JsonOptions);
    await responseStream.FlushAsync();
}
File.Move(temporaryResponse, parsed.ResponsePath, overwrite: true);
return response.Outcome is ConstGenericsProcessorOutcome.Succeeded or
    ConstGenericsProcessorOutcome.Findings or
    ConstGenericsProcessorOutcome.InvalidArtifact or
    ConstGenericsProcessorOutcome.LimitExceeded
    ? 0 : 1;

internal sealed record ProcessorArguments(string RequestPath, string ResponsePath)
{
    public static ProcessorArguments Parse(string[] arguments)
    {
        string? request = null;
        string? response = null;
        for (var index = 0; index < arguments.Length; index++)
        {
            switch (arguments[index])
            {
                case "--request" when index + 1 < arguments.Length:
                    request = arguments[++index];
                    break;
                case "--response" when index + 1 < arguments.Length:
                    response = arguments[++index];
                    break;
                default:
                    throw new ArgumentException("The processor command line is invalid.");
            }
        }
        if (string.IsNullOrWhiteSpace(request) || string.IsNullOrWhiteSpace(response))
            throw new ArgumentException("Both --request and --response are required.");
        return new ProcessorArguments(Path.GetFullPath(request), Path.GetFullPath(response));
    }
}
