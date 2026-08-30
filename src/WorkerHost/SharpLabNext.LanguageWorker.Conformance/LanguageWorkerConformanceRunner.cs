using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Contracts;
using SharpLabNext.LanguageWorker.Sdk;

namespace SharpLabNext.LanguageWorker.Conformance;

public sealed class LanguageWorkerConformanceRunner(HttpClient httpClient, Func<Uri, CancellationToken, Task<WebSocket>> connectWebSocketAsync)
{
    private static readonly JsonSerializerOptions JsonOptions = ContractJson.CreateSerializerOptions();
    private static readonly JsonSerializerOptions LspJsonOptions = ContractJson.CreateLspSerializerOptions();

    public async Task<LanguageWorkerConformanceReport> VerifyAsync(LanguageWorkerConformanceScenario scenario, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var passed = new List<string>(6);
        await VerifyHealthAsync(scenario.ExpectedIdentity, scenario.ExpectedWorkerImageId, scenario.ExpectedManifest.ToolchainIds, scenario.ExpectedManifest.SupportedReferenceSetIds, cancellationToken).ConfigureAwait(false);
        passed.Add("health-and-identity");
        await VerifyCapabilitiesAsync(scenario.ExpectedIdentity, scenario.ExpectedManifest, cancellationToken).ConfigureAwait(false);
        passed.Add("capability-manifest");
        await VerifyCompileCheckAsync(scenario, cancellationToken).ConfigureAwait(false);
        passed.Add("compile-check");
        await VerifyArtifactAsync(scenario, cancellationToken).ConfigureAwait(false);
        passed.Add("artifact-envelope");
        await VerifyLspAsync(scenario, cancellationToken).ConfigureAwait(false);
        passed.Add("lsp-lifecycle");
        await VerifyRejectedToolchainAsync(scenario, cancellationToken).ConfigureAwait(false);
        passed.Add("request-isolation");
        return new LanguageWorkerConformanceReport(passed);
    }

    private async Task VerifyHealthAsync(ServiceIdentity expected, string expectedWorkerImageId, IReadOnlyList<string> expectedToolchainIds, IReadOnlyList<string> expectedReferenceSetIds, CancellationToken cancellationToken)
    {
        await RequireSuccessAsync("health-and-identity", "/health/live", cancellationToken).ConfigureAwait(false);
        var health = await GetAsync<HealthResponse>("health-and-identity", "/health/ready", cancellationToken).ConfigureAwait(false);
        Require("health-and-identity", health.Status == HealthStatus.Healthy, "The worker did not report healthy readiness.");
        var descriptor = await GetAsync<WorkerDescriptor>("health-and-identity", "/api/v1/worker/describe", cancellationToken).ConfigureAwait(false);
        var actual = descriptor.Service;
        Require("health-and-identity", actual.Id == expected.Id, "The worker ID differs from the expected identity.");
        Require("health-and-identity", actual.Kind == ServiceKind.ToolchainWorker, "The service is not a toolchain worker.");
        Require("health-and-identity", actual.ReleaseId == expected.ReleaseId, "The release identity differs.");
        Require("health-and-identity", actual.Protocol == expected.Protocol, "The worker protocol differs.");
        Require("health-and-identity", descriptor.WorkerKind == WorkerKind.Toolchain, "The descriptor worker kind differs.");
        Require("health-and-identity", descriptor.WorkerImageId == expectedWorkerImageId, "The worker image identity differs.");
        Require("health-and-identity", descriptor.NegotiatedProtocol == expected.Protocol, "The negotiated protocol differs.");
        Require("health-and-identity", descriptor.SupportedProtocolVersions.Contains(expected.Protocol), "The supported protocol list is incomplete.");
        Require("health-and-identity", descriptor.ProfileIds.SequenceEqual(expectedToolchainIds, StringComparer.Ordinal), "The toolchain profiles differ from the capability manifest.");
        Require("health-and-identity", descriptor.ReferenceSets is not null, "The worker omitted reference-set attestations.");
        foreach (var referenceSetId in expectedReferenceSetIds)
        {
            Require("health-and-identity", descriptor.ReferenceSets!.Any(item => item.Id == referenceSetId && !string.IsNullOrWhiteSpace(item.Digest) && item.ContentDigest.StartsWith("sha256:", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(item.Provenance.Kind) && !string.IsNullOrWhiteSpace(item.Provenance.ResolvedVersion)), $"Reference set '{referenceSetId}' is not attested by the worker descriptor.");
        }
        foreach (var capability in expected.Capabilities)
        {
            Require("health-and-identity", descriptor.Capabilities.Any(item => item.Id == capability && item.Available && item.ProfileIds.SequenceEqual(expectedToolchainIds, StringComparer.Ordinal)), $"Capability '{capability}' is missing or unavailable in the worker descriptor.");
        }

    }

    private async Task VerifyCapabilitiesAsync(ServiceIdentity identity, LanguageWorkerCapabilityManifest expected, CancellationToken cancellationToken)
    {
        var actual = await GetAsync<LanguageWorkerCapabilityManifest>("capability-manifest", "/api/v1/worker/capabilities", cancellationToken).ConfigureAwait(false);
        try
        {
            LanguageWorkerCapabilityManifestSerializer.Validate(actual, identity);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            throw new LanguageWorkerConformanceException("capability-manifest", exception.Message, exception);
        }

        Require("capability-manifest", actual.SchemaVersion == expected.SchemaVersion, "The endpoint manifest schema version differs.");
        Require("capability-manifest", actual.WorkerId == expected.WorkerId, "The endpoint manifest worker ID differs.");
        Require("capability-manifest", actual.LanguageId == expected.LanguageId, "The endpoint manifest language ID differs.");
        Require("capability-manifest", actual.ToolchainIds.SequenceEqual(expected.ToolchainIds, StringComparer.Ordinal), "The endpoint manifest toolchain IDs differ.");
        Require("capability-manifest", actual.ProtocolVersion == expected.ProtocolVersion, "The endpoint manifest protocol differs.");
        Require("capability-manifest", actual.Capabilities.SequenceEqual(expected.Capabilities, StringComparer.Ordinal), "The endpoint manifest capabilities differ.");
        Require("capability-manifest", actual.ProducedArtifactFormats.SequenceEqual(expected.ProducedArtifactFormats, StringComparer.Ordinal), "The endpoint manifest artifact formats differ.");
        Require("capability-manifest", actual.SupportedReferenceSetIds.SequenceEqual(expected.SupportedReferenceSetIds, StringComparer.Ordinal), "The endpoint manifest reference sets differ.");
        Require("capability-manifest", actual.Limits == expected.Limits, "The endpoint manifest limits differ.");
        Require("capability-manifest", actual.Capabilities.Contains("compile-check", StringComparer.Ordinal), "Compile Check is not declared.");
        Require("capability-manifest", actual.Capabilities.Contains("artifact", StringComparer.Ordinal), "Artifact build is not declared.");
        Require("capability-manifest", actual.Capabilities.Contains("lsp", StringComparer.Ordinal), "LSP is not declared.");
    }

    private async Task VerifyCompileCheckAsync(LanguageWorkerConformanceScenario scenario, CancellationToken cancellationToken)
    {
        Require("compile-check", scenario.CompileCheckRequest.Target == BuildTarget.CompileCheck, "The scenario request must target Compile Check.");
        var response = await PostAsync<BuildRequest, LanguageWorkerBuildHttpResponse>("compile-check", "/api/v1/build", scenario.CompileCheckRequest, cancellationToken).ConfigureAwait(false);
        Require("compile-check", response.RequestId == scenario.CompileCheckRequest.RequestId, "The response request ID was not preserved.");
        var result = response.Result as CompilationCheckResult ?? throw new LanguageWorkerConformanceException("compile-check", "The endpoint did not return a CompilationCheckResult.");
        Require("compile-check", result.CompilationSucceeded, "The known-valid workspace did not compile.");
        Require("compile-check", result.Diagnostics.All(static diagnostic => diagnostic.Severity != DiagnosticSeverity.Error), "A successful Compile Check returned errors.");
        Require("compile-check", result.Identity.ToolchainId == scenario.CompileCheckRequest.ToolchainId, "The result used another toolchain identity.");
        Require("compile-check", result.Identity.ReferenceSetId == scenario.CompileCheckRequest.ReferenceSetId, "The result used another reference set.");
        Require("compile-check", response.DevelopmentArtifact is null, "Compile Check must not return an artifact envelope.");
    }

    private async Task VerifyArtifactAsync(LanguageWorkerConformanceScenario scenario, CancellationToken cancellationToken)
    {
        Require("artifact-envelope", scenario.ArtifactRequest.Target == BuildTarget.Artifact, "The scenario request must target Artifact.");
        var response = await PostAsync<BuildRequest, LanguageWorkerBuildHttpResponse>("artifact-envelope", "/api/v1/build", scenario.ArtifactRequest, cancellationToken).ConfigureAwait(false);
        var result = response.Result as BuildResult ?? throw new LanguageWorkerConformanceException("artifact-envelope", "The endpoint did not return a BuildResult.");
        Require("artifact-envelope", result.Outcome == BuildOutcome.Succeeded, "The known-valid workspace did not emit an artifact.");
        var envelope = response.DevelopmentArtifact ?? throw new LanguageWorkerConformanceException("artifact-envelope", "The worker omitted its conformance artifact envelope.");
        Require("artifact-envelope", result.ArtifactRef == envelope.ArtifactRef, "BuildResult and envelope artifact IDs differ.");
        Require("artifact-envelope", envelope.Manifest.ArtifactId == envelope.ArtifactRef, "The manifest and envelope artifact IDs differ.");
        Require("artifact-envelope", scenario.ExpectedManifest.ProducedArtifactFormats.Contains(envelope.ArtifactFormat, StringComparer.Ordinal), "The artifact format was not declared by the worker.");
        Require("artifact-envelope", envelope.FileContentsBase64 is not null, "A generic artifact envelope must carry file contents.");
        try
        {
            ArtifactIdentity.Validate(envelope.Manifest);
        }
        catch (ArgumentException exception)
        {
            throw new LanguageWorkerConformanceException("artifact-envelope", exception.Message, exception);
        }

        foreach (var file in envelope.Files)
        {
            Require("artifact-envelope", envelope.FileContentsBase64!.TryGetValue(file.Path, out var contentBase64), $"Artifact file '{file.Path}' has no content.");
            byte[] content;
            try
            {
                content = Convert.FromBase64String(contentBase64!);
            }
            catch (FormatException exception)
            {
                throw new LanguageWorkerConformanceException("artifact-envelope", $"Artifact file '{file.Path}' is not valid base64.", exception);
            }
            Require("artifact-envelope", content.LongLength == file.Size, $"Artifact file '{file.Path}' has the wrong size.");
            var digest = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(content))}";
            Require("artifact-envelope", digest == file.Digest, $"Artifact file '{file.Path}' has the wrong digest.");
        }
    }

    private async Task VerifyLspAsync(LanguageWorkerConformanceScenario scenario, CancellationToken cancellationToken)
    {
        var session = await PostAsync<OpenLanguageSessionRequest, LanguageSession>("lsp-lifecycle", "/api/v1/language-sessions", scenario.LanguageSessionRequest, cancellationToken).ConfigureAwait(false);
        Require("lsp-lifecycle", session.LanguageId == scenario.ExpectedManifest.LanguageId, "The session language differs.");
        Require("lsp-lifecycle", session.ToolchainId == scenario.LanguageSessionRequest.ToolchainId, "The session toolchain differs.");

        var lspPath = $"/api/v1/language-sessions/{Uri.EscapeDataString(session.SessionId)}/lsp";
        var webSocketUri = CreateWebSocketUri(lspPath);
        using var socket = await connectWebSocketAsync(webSocketUri, cancellationToken).ConfigureAwait(false);

        await SendAsync(socket, new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { capabilities = new { } } }, cancellationToken).ConfigureAwait(false);
        using (var initialized = await ReceiveUntilAsync(socket, static root => HasId(root, 1), cancellationToken).ConfigureAwait(false)) {
            var capabilities = initialized.RootElement.GetProperty("result").GetProperty("capabilities");
            Require("lsp-lifecycle", capabilities.TryGetProperty("completionProvider", out _), "The LSP server did not advertise completion.");
            Require("lsp-lifecycle", capabilities.TryGetProperty("textDocumentSync", out _), "The LSP server did not advertise document synchronization.");
        }

        await SendAsync(socket, new { jsonrpc = "2.0", method = "initialized", @params = new { } }, cancellationToken).ConfigureAwait(false);
        await SendAsync(socket, new { jsonrpc = "2.0", method = "textDocument/didOpen", @params = new { textDocument = new { uri = scenario.DocumentUri, languageId = scenario.ExpectedManifest.LanguageId, version = 1, text = scenario.OpenText } } }, cancellationToken).ConfigureAwait(false);
        using (var diagnostics = await ReceiveDiagnosticsAsync(socket, cancellationToken).ConfigureAwait(false)) {
            Require("lsp-lifecycle", diagnostics.RootElement.GetProperty("params").GetProperty("diagnostics").EnumerateArray().Any(item => item.GetProperty("code").GetString() == scenario.ExpectedOpenDiagnosticCode), "didOpen did not publish the expected diagnostic.");
        }

        await SendAsync(socket, new { jsonrpc = "2.0", method = "textDocument/didChange", @params = new { textDocument = new { uri = scenario.DocumentUri, version = 2 }, contentChanges = new[] { new { text = scenario.ChangedText } } } }, cancellationToken).ConfigureAwait(false);
        using (var diagnostics = await ReceiveDiagnosticsAsync(socket, cancellationToken).ConfigureAwait(false)) {
            Require("lsp-lifecycle", !diagnostics.RootElement.GetProperty("params").GetProperty("diagnostics").EnumerateArray().Any(), "didChange did not clear diagnostics for the known-valid document.");
        }

        await SendAsync(socket, new { jsonrpc = "2.0", id = 2, method = "textDocument/completion", @params = new { textDocument = new { uri = scenario.DocumentUri }, position = new { line = scenario.CompletionPosition.Line, character = scenario.CompletionPosition.Character }, context = new { triggerKind = 1 } } }, cancellationToken).ConfigureAwait(false);
        using (var completion = await ReceiveUntilAsync(socket, static root => HasId(root, 2), cancellationToken).ConfigureAwait(false)) {
            Require("lsp-lifecycle", completion.RootElement.GetProperty("result").GetProperty("items").EnumerateArray().Any(item => item.GetProperty("label").GetString() == scenario.ExpectedCompletionLabel), "Completion did not contain the expected item.");
        }

        await SendAsync(socket, new { jsonrpc = "2.0", id = 3, method = "shutdown", @params = new { } }, cancellationToken).ConfigureAwait(false);
        using (var shutdown = await ReceiveUntilAsync(socket, static root => HasId(root, 3), cancellationToken).ConfigureAwait(false)) {
            Require("lsp-lifecycle", shutdown.RootElement.GetProperty("result").ValueKind == JsonValueKind.Null, "Shutdown did not return null.");
        }
        await SendAsync(socket, new { jsonrpc = "2.0", method = "exit", @params = new { } }, cancellationToken).ConfigureAwait(false);

        using var closeResponse = await httpClient.DeleteAsync($"/api/v1/language-sessions/{Uri.EscapeDataString(session.SessionId)}", cancellationToken).ConfigureAwait(false);
        Require("lsp-lifecycle", closeResponse.IsSuccessStatusCode, "The language session could not be closed.");
    }

    private async Task VerifyRejectedToolchainAsync(LanguageWorkerConformanceScenario scenario, CancellationToken cancellationToken)
    {
        var wrongRequest = scenario.CompileCheckRequest with { ToolchainId = "another-toolchain" };
        using var response = await httpClient.PostAsJsonAsync("/api/v1/build", wrongRequest, JsonOptions, cancellationToken).ConfigureAwait(false);
        Require("request-isolation", (int)response.StatusCode == 400, "A request for another toolchain was not rejected.");
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        Require("request-isolation", body.Contains("wrong-toolchain", StringComparison.Ordinal), "The rejection did not identify the toolchain mismatch.");
    }

    private async Task RequireSuccessAsync(string check, string path, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(path, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new LanguageWorkerConformanceException(check, $"GET {path} returned {(int)response.StatusCode}: {body}");
        }
    }

    private async Task<T> GetAsync<T>(string check, string path, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(path, cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync<T>(check, path, response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(string check, string path, TRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(path, request, JsonOptions, cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync<TResponse>(check, path, response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ReadRequiredAsync<T>(string check, string path, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new LanguageWorkerConformanceException(check, $"{path} returned {(int)response.StatusCode}: {body}");
        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOptions) ?? throw new LanguageWorkerConformanceException(check, $"{path} returned an empty payload.");
        }
        catch (JsonException exception)
        {
            throw new LanguageWorkerConformanceException(check, $"{path} returned an invalid payload: {exception.Message}", exception);
        }
    }

    private Uri CreateWebSocketUri(string path)
    {
        var baseAddress = httpClient.BaseAddress ?? throw new LanguageWorkerConformanceException("lsp-lifecycle", "HttpClient.BaseAddress is required.");
        var builder = new UriBuilder(new Uri(baseAddress, path))
        {
            Scheme = baseAddress.Scheme == Uri.UriSchemeHttps ? "wss" : "ws"
        };
        return builder.Uri;
    }

    private static async Task SendAsync(WebSocket socket, object message, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, LspJsonOptions);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }

    private static Task<JsonDocument> ReceiveDiagnosticsAsync(WebSocket socket, CancellationToken cancellationToken) => ReceiveUntilAsync(socket, static root => root.TryGetProperty("method", out var method) && method.GetString() == "textDocument/publishDiagnostics", cancellationToken);

    private static async Task<JsonDocument> ReceiveUntilAsync(WebSocket socket, Func<JsonElement, bool> predicate, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var bytes = await ReceiveMessageAsync(socket, cancellationToken).ConfigureAwait(false);
            var document = JsonDocument.Parse(bytes);
            if (predicate(document.RootElement))
                return document;
            document.Dispose();
        }
        throw new LanguageWorkerConformanceException("lsp-lifecycle", "The expected LSP message was not received.");
    }

    private static async Task<byte[]> ReceiveMessageAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var content = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new LanguageWorkerConformanceException("lsp-lifecycle", "The LSP socket closed before the expected message.");
            if (result.MessageType != WebSocketMessageType.Text)
                throw new LanguageWorkerConformanceException("lsp-lifecycle", "The LSP server returned a non-text frame.");
            content.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        return content.ToArray();
    }

    private static bool HasId(JsonElement root, int expected) =>
        root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.GetInt32() == expected;

    private static void Require(string check, bool condition, string message)
    {
        if (!condition)
            throw new LanguageWorkerConformanceException(check, message);
    }
}
