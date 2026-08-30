#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property EnableTrimAnalyzer=false
#:property EnableAotAnalyzer=false
#:property PublishAot=false
#:property PublishTrimmed=false
#:property SelfContained=false

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

if (args.Length != 2)
    throw new ArgumentException("Usage: artifact-worker-boundary.cs <worker-base-address> <artifact-ref>");

var baseAddress = new Uri(args[0], UriKind.Absolute);
var artifactRef = args[1];
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
using var http = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(30) };
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    PropertyNamingPolicy = PascalCaseJsonNamingPolicy.Instance,
    DictionaryKeyPolicy = null,
    TypeInfoResolver = new DefaultJsonTypeInfoResolver()
};

using (var health = await http.GetAsync("/health/ready", timeout.Token))
    await EnsureSuccessAsync(health, "worker readiness");

var requestId = $"boundary-{Guid.NewGuid():N}";
using var start = await http.PostAsJsonAsync("/api/v1/artifact-renders", new { requestId, idempotencyKey = $"render:{requestId}", pipelineResolutionId = "const-generics-boundary-smoke", artifactRef, processorId = "artifacts-default", outputId = "il", options = new { includeSequencePoints = false, includeCompilerGeneratedMembers = true, maxCharacters = 1_000_000 }, deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(30) }, json, timeout.Token);
await EnsureSuccessAsync(start, "start boundary render");
using var handle = JsonDocument.Parse(await start.Content.ReadAsByteArrayAsync(timeout.Token));
var operationId = handle.RootElement.GetProperty("OperationId").GetString() ?? throw new InvalidOperationException("The artifact worker returned no operation ID.");

for (var attempt = 0; attempt < 300; attempt++)
{
    using var stateResponse = await http.GetAsync($"/api/v1/operations/{operationId}", timeout.Token);
    await EnsureSuccessAsync(stateResponse, "read boundary operation");
    using var state = JsonDocument.Parse(await stateResponse.Content.ReadAsByteArrayAsync(timeout.Token));
    var status = state.RootElement.GetProperty("Status").GetString();
    if (status == "failed")
    {
        var error = state.RootElement.GetProperty("Error");
        if (error.GetProperty("Code").GetString() != "invalid-argument")
            throw new InvalidOperationException($"The ordinary worker failed with an unexpected error: {error.GetRawText()}");
        Console.WriteLine("Ordinary artifact worker rejected the ConstGenerics artifact.");
        return;
    }
    if (status == "completed")
    {
        using var eventResponse = await http.GetAsync($"/api/v1/operations/{operationId}/events?FromSequence=0", timeout.Token);
        await EnsureSuccessAsync(eventResponse, "read boundary events");
        using var events = JsonDocument.Parse(await eventResponse.Content.ReadAsByteArrayAsync(timeout.Token));
        var results = events.RootElement.EnumerateArray().Where(static operationEvent => operationEvent.GetProperty("Payload").GetProperty("Kind").GetString() == "typed-result").Select(static operationEvent => operationEvent.GetProperty("Payload").GetProperty("Result").Clone()).ToArray();
        if (results.Length != 1)
            throw new InvalidOperationException($"The ordinary worker returned {results.Length} typed results.");
        var result = results[0];
        if (result.GetProperty("ResultType").GetString() != "artifact-render" || result.GetProperty("Outcome").GetString() != "invalid-artifact" || result.TryGetProperty("ContentRef", out var contentRef) && contentRef.ValueKind != JsonValueKind.Null || !result.GetProperty("Diagnostics").EnumerateArray().Any(static diagnostic => diagnostic.GetProperty("Code").GetString() == "invalid-artifact"))
        {
            throw new InvalidOperationException($"The ordinary worker did not reject the specialized artifact: {result.GetRawText()}");
        }
        Console.WriteLine("Ordinary artifact worker rejected the ConstGenerics artifact.");
        return;
    }
    if (status == "cancelled")
        throw new InvalidOperationException("The ordinary worker cancelled instead of rejecting the ConstGenerics artifact.");
    await Task.Delay(100, timeout.Token);
}

throw new TimeoutException("The ordinary artifact worker did not reach a terminal state.");

static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation)
{
    if (response.IsSuccessStatusCode)
        return;
    var body = await response.Content.ReadAsStringAsync();
    throw new InvalidOperationException($"{operation} failed with {(int)response.StatusCode}: {body}");
}

sealed class PascalCaseJsonNamingPolicy : JsonNamingPolicy
{
    public static readonly PascalCaseJsonNamingPolicy Instance = new();

    public override string ConvertName(string name) =>
        name.Length == 0 || !char.IsAsciiLetterLower(name[0])
            ? name : char.ToUpperInvariant(name[0]) + name[1..];
}
