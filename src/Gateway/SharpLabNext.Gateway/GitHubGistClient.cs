using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpLabNext.Gateway;

public interface IGitHubGistClient
{
    Task<string> GetLoginAsync(string accessToken, CancellationToken cancellationToken);
    Task<GitHubGist> GetAsync(string id, string? accessToken, CancellationToken cancellationToken);
    Task<GitHubGist> CreateAsync(GitHubGistWriteRequest request, string accessToken, CancellationToken cancellationToken);
    Task<GitHubGist> UpdateAsync(string id, GitHubGistWriteRequest request, string accessToken, CancellationToken cancellationToken);
}

public sealed record GitHubGist(string Id, string HtmlUrl, string? OwnerLogin, bool IsPublic, string Description, DateTimeOffset? UpdatedAtUtc, IReadOnlyDictionary<string, GitHubGistFile> Files);

public sealed record GitHubGistFile(string FileName, string? Content, bool Truncated, string? RawUrl, long? Size);

public sealed record GitHubGistWriteRequest(string Description, bool? IsPublic, IReadOnlyDictionary<string, string?> Files);

public sealed class GitHubGistClient(HttpClient httpClient) : IGitHubGistClient
{
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<string> GetLoginAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "user", accessToken);
        var response = await SendJsonAsync<GitHubUserResponse>(request, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(response.Login))
            throw new GitHubApiException(HttpStatusCode.BadGateway, "GitHub returned an invalid user profile.");
        return response.Login;
    }

    public async Task<GitHubGist> GetAsync(string id, string? accessToken, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, $"gists/{Uri.EscapeDataString(id)}", accessToken);
        var response = await SendJsonAsync<GitHubGistResponse>(request, cancellationToken).ConfigureAwait(false);
        var gist = await ConvertAsync(response, accessToken, cancellationToken).ConfigureAwait(false);
        if (!StringComparer.OrdinalIgnoreCase.Equals(id, gist.Id))
            throw new GitHubApiException(HttpStatusCode.BadGateway, "GitHub returned a different Gist ID.");
        return gist;
    }

    public async Task<GitHubGist> CreateAsync(GitHubGistWriteRequest request, string accessToken, CancellationToken cancellationToken)
    {
        using var message = CreateRequest(HttpMethod.Post, "gists", accessToken);
        message.Content = JsonContent.Create(ToWriteBody(request, includeVisibility: true), options: JsonOptions);
        var response = await SendJsonAsync<GitHubGistResponse>(message, cancellationToken).ConfigureAwait(false);
        return await ConvertAsync(response, accessToken, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitHubGist> UpdateAsync(string id, GitHubGistWriteRequest request, string accessToken, CancellationToken cancellationToken)
    {
        using var message = CreateRequest(HttpMethod.Patch, $"gists/{Uri.EscapeDataString(id)}", accessToken);
        message.Content = JsonContent.Create(ToWriteBody(request, includeVisibility: false), options: JsonOptions);
        var response = await SendJsonAsync<GitHubGistResponse>(message, cancellationToken).ConfigureAwait(false);
        var gist = await ConvertAsync(response, accessToken, cancellationToken).ConfigureAwait(false);
        if (!StringComparer.OrdinalIgnoreCase.Equals(id, gist.Id))
            throw new GitHubApiException(HttpStatusCode.BadGateway, "GitHub returned a different Gist ID.");
        return gist;
    }

    private async Task<T> SendJsonAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new GitHubApiException(HttpStatusCode.ServiceUnavailable, "GitHub is unavailable.", exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw ErrorFor(response.StatusCode);
            var bytes = await ReadLimitedAsync(response.Content, MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
            try
            {
                return JsonSerializer.Deserialize<T>(bytes, JsonOptions) ?? throw new GitHubApiException(HttpStatusCode.BadGateway, "GitHub returned an empty response.");
            }
            catch (JsonException exception)
            {
                throw new GitHubApiException(HttpStatusCode.BadGateway, "GitHub returned an invalid response.", exception);
            }
        }
    }

    private async Task<GitHubGist> ConvertAsync(GitHubGistResponse response, string? accessToken, CancellationToken cancellationToken)
    {
        if (response.Files is { Count: > 256 })
            throw new GitHubApiException(HttpStatusCode.RequestEntityTooLarge, "The Gist contains too many files.");
        var files = new Dictionary<string, GitHubGistFile>(StringComparer.Ordinal);
        foreach (var (name, source) in response.Files ?? [])
        {
            var content = source.Content;
            if (source.Truncated && NeedsRawWorkspaceContent(name))
            {
                if (!Uri.TryCreate(source.RawUrl, UriKind.Absolute, out var rawUri) || rawUri.Scheme != Uri.UriSchemeHttps || !StringComparer.OrdinalIgnoreCase.Equals(rawUri.Host, "gist.githubusercontent.com"))
                {
                    throw new GitHubApiException(HttpStatusCode.BadGateway, "GitHub returned an unsafe raw Gist URL.");
                }
                using var rawRequest = CreateRequest(HttpMethod.Get, rawUri.AbsoluteUri, accessToken);
                using var rawResponse = await httpClient.SendAsync(rawRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!rawResponse.IsSuccessStatusCode)
                    throw ErrorFor(rawResponse.StatusCode);
                var rawBytes = await ReadLimitedAsync(rawResponse.Content, 512 * 1024, cancellationToken).ConfigureAwait(false);
                content = System.Text.Encoding.UTF8.GetString(rawBytes);
            }
            files[name] = new GitHubGistFile(source.FileName ?? name, content, source.Truncated, source.RawUrl, source.Size);
        }
        if (!Uri.TryCreate(response.HtmlUrl, UriKind.Absolute, out var htmlUri) || htmlUri.Scheme != Uri.UriSchemeHttps || !StringComparer.OrdinalIgnoreCase.Equals(htmlUri.Host, "gist.github.com") || !string.IsNullOrEmpty(htmlUri.UserInfo))
        {
            throw new GitHubApiException(HttpStatusCode.BadGateway, "GitHub returned an invalid Gist URL.");
        }
        return new GitHubGist(response.Id ?? throw new GitHubApiException(HttpStatusCode.BadGateway, "GitHub omitted the Gist ID."), htmlUri.AbsoluteUri, response.Owner?.Login, response.Public, response.Description ?? string.Empty, response.UpdatedAt, files);
    }

    private static bool NeedsRawWorkspaceContent(string name) =>
        name.EndsWith(".sharplab.json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".vb", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".fs", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".fsx", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".il", StringComparison.OrdinalIgnoreCase);

    private static HttpRequestMessage CreateRequest(HttpMethod method, string uri, string? accessToken)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.ParseAdd("SharpLabNext/1.0");
        if (!string.IsNullOrWhiteSpace(accessToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static GitHubGistWriteBody ToWriteBody(GitHubGistWriteRequest request, bool includeVisibility) =>
        new(request.Description, includeVisibility ? request.IsPublic : null, request.Files.ToDictionary(static item => item.Key, static item => item.Value is null ? null : new GitHubGistFileWrite(item.Value), StringComparer.Ordinal));

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int maximumBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximumBytes)
            throw new GitHubApiException(HttpStatusCode.RequestEntityTooLarge, "The GitHub response exceeds the configured limit.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (buffer.Length > maximumBytes - read)
                throw new GitHubApiException(HttpStatusCode.RequestEntityTooLarge, "The GitHub response exceeds the configured limit.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static GitHubApiException ErrorFor(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.NotFound => new(statusCode, "The Gist was not found or is private."),
        HttpStatusCode.Unauthorized => new(statusCode, "The GitHub authorization is no longer valid."),
        HttpStatusCode.Forbidden => new(statusCode, "GitHub denied access to the Gist."),
        HttpStatusCode.UnprocessableEntity => new(statusCode, "GitHub rejected the Gist contents."),
        HttpStatusCode.TooManyRequests => new(statusCode, "GitHub rate limited the request."),
        _ => new(HttpStatusCode.BadGateway, "GitHub could not process the Gist request.")
    };

    private sealed record GitHubUserResponse([property: JsonPropertyName("login")] string? Login);

    private sealed record GitHubGistResponse(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("owner")] GitHubOwnerResponse? Owner,
        [property: JsonPropertyName("public")] bool Public,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("updated_at")] DateTimeOffset? UpdatedAt,
        [property: JsonPropertyName("files")] Dictionary<string, GitHubGistFileResponse>? Files);

    private sealed record GitHubOwnerResponse([property: JsonPropertyName("login")] string? Login);

    private sealed record GitHubGistFileResponse(
        [property: JsonPropertyName("filename")] string? FileName,
        [property: JsonPropertyName("content")] string? Content,
        [property: JsonPropertyName("truncated")] bool Truncated,
        [property: JsonPropertyName("raw_url")] string? RawUrl,
        [property: JsonPropertyName("size")] long? Size);

    private sealed record GitHubGistWriteBody(
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("public"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Public,
        [property: JsonPropertyName("files")] Dictionary<string, GitHubGistFileWrite?> Files);

    private sealed record GitHubGistFileWrite([property: JsonPropertyName("content")] string Content);
}

public sealed class GitHubApiException(HttpStatusCode statusCode, string publicMessage, Exception? innerException = null) : Exception(publicMessage, innerException)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string PublicMessage { get; } = publicMessage;
}
