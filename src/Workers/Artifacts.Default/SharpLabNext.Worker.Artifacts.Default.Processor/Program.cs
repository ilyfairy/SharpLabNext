using System.Text.Json;
using SharpLabNext.ArtifactProcessing;
using SharpLabNext.ArtifactProcessing.Protocol;

var parsed = ProcessorArguments.Parse(args);
ProcessorResponse response;
try
{
    await using var requestStream = new FileStream(
        parsed.RequestPath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 16 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
    var request = await JsonSerializer.DeserializeAsync<ProcessorRequest>(
        requestStream,
        ProcessorProtocol.JsonOptions)
        ?? throw new InvalidDataException("The processor request was empty.");
    response = await ProcessorEngine.ExecuteAsync(request, CancellationToken.None);
}
catch (Exception exception)
{
    response = ProcessorEngine.ToFailureResponse(exception);
}

var responseDirectory = Path.GetDirectoryName(parsed.ResponsePath);
if (!string.IsNullOrEmpty(responseDirectory))
    Directory.CreateDirectory(responseDirectory);
var temporaryResponse = parsed.ResponsePath + ".tmp";
await using (var responseStream = new FileStream(
    temporaryResponse,
    FileMode.Create,
    FileAccess.Write,
    FileShare.None,
    bufferSize: 16 * 1024,
    FileOptions.Asynchronous | FileOptions.WriteThrough))
{
    await JsonSerializer.SerializeAsync(responseStream, response, ProcessorProtocol.JsonOptions);
    await responseStream.FlushAsync();
}
File.Move(temporaryResponse, parsed.ResponsePath, overwrite: true);
return response.Outcome is ProcessorOutcome.Succeeded or ProcessorOutcome.Findings or ProcessorOutcome.InvalidArtifact
    or ProcessorOutcome.LimitExceeded
    ? 0
    : 1;

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
