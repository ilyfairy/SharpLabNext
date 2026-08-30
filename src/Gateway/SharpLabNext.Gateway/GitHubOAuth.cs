using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using SharpLabNext.Contracts;

namespace SharpLabNext.Gateway;

public sealed record GitHubOAuthOptions(string? ClientId, string? ClientSecret, Uri AuthorizationEndpoint, Uri TokenEndpoint, Uri? CallbackUri, TimeSpan PendingStateLifetime, TimeSpan SessionLifetime)
{
    public bool Available =>
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret) &&
        CallbackUri is not null;

    public static GitHubOAuthOptions FromConfiguration(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        var section = configuration.GetSection("GitHub:OAuth");
        var explicitlyEnabled = OptionalBoolean(section["Enabled"]);
        var clientId = NullIfWhiteSpace(section["ClientId"]);
        if (clientId is { Length: > 512 } || clientId?.Any(char.IsControl) == true)
            throw new InvalidOperationException("GitHub:OAuth:ClientId is invalid.");
        var callbackUri = OptionalAbsoluteUri(section["CallbackUri"]);
        var secret = NullIfWhiteSpace(section["ClientSecret"]);
        var secretFile = NullIfWhiteSpace(section["ClientSecretFile"]);
        if (secret is not null && secretFile is not null)
        {
            throw new InvalidOperationException("GitHub:OAuth:ClientSecret and GitHub:OAuth:ClientSecretFile cannot both be configured.");
        }
        if (environment.IsProduction() && secret is not null)
        {
            throw new InvalidOperationException("GitHub:OAuth:ClientSecret is not allowed in Production; use ClientSecretFile.");
        }
        if (secretFile is not null)
        {
            var path = Path.GetFullPath(secretFile, environment.ContentRootPath);
            if (!File.Exists(path))
                throw new InvalidOperationException("GitHub:OAuth:ClientSecretFile does not exist.");
            secret = NullIfWhiteSpace(File.ReadAllText(path));
        }
        if (secret is { Length: > 4096 })
            throw new InvalidOperationException("The GitHub OAuth client secret is too large.");

        var anyCredential = clientId is not null || callbackUri is not null || secret is not null;
        var complete = clientId is not null && callbackUri is not null && secret is not null;
        if ((anyCredential || explicitlyEnabled == true) && !complete)
        {
            throw new InvalidOperationException("GitHub OAuth requires ClientId, CallbackUri, and ClientSecretFile/ClientSecret together.");
        }
        if (explicitlyEnabled == false && anyCredential)
        {
            throw new InvalidOperationException("GitHub OAuth credentials cannot be configured while GitHub:OAuth:Enabled is false.");
        }
        if (complete)
            ValidateCallbackUri(callbackUri!, environment);

        return new GitHubOAuthOptions(
            explicitlyEnabled == false ? null : clientId,
            explicitlyEnabled == false ? null : secret,
            GitHubExternalEndpoint.Parse(
                NullIfWhiteSpace(section["AuthorizationEndpoint"]) ?? "https://github.com/login/oauth/authorize",
                "GitHub:OAuth:AuthorizationEndpoint",
                environment),
            GitHubExternalEndpoint.Parse(
                NullIfWhiteSpace(section["TokenEndpoint"]) ?? "https://github.com/login/oauth/access_token",
                "GitHub:OAuth:TokenEndpoint",
                environment),
            explicitlyEnabled == false ? null : callbackUri,
            PositiveDuration(section["PendingStateLifetime"], TimeSpan.FromMinutes(10), "PendingStateLifetime"),
            PositiveDuration(section["SessionLifetime"], TimeSpan.FromHours(8), "SessionLifetime"));
    }

    private static bool? OptionalBoolean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (!bool.TryParse(value, out var parsed))
            throw new InvalidOperationException("GitHub:OAuth:Enabled must be true or false.");
        return parsed;
    }

    private static void ValidateCallbackUri(Uri callbackUri, IHostEnvironment environment)
    {
        if (environment.IsProduction() && callbackUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("GitHub:OAuth:CallbackUri must use HTTPS in Production.");
        if (callbackUri.Scheme == Uri.UriSchemeHttp && !callbackUri.IsLoopback)
        {
            throw new InvalidOperationException("GitHub:OAuth:CallbackUri may use HTTP only for localhost development or tests.");
        }
    }

    private static Uri AbsoluteUri(string? value, string fallback)
    {
        if (!Uri.TryCreate(NullIfWhiteSpace(value) ?? fallback, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("GitHub OAuth endpoints must be absolute HTTP(S) URIs.");
        }
        return uri;
    }

    private static Uri? OptionalAbsoluteUri(string? value)
    {
        var normalized = NullIfWhiteSpace(value);
        return normalized is null ? null : AbsoluteUri(normalized, normalized);
    }

    private static TimeSpan PositiveDuration(string? value, TimeSpan fallback, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        if (!TimeSpan.TryParse(value, out var parsed) || parsed <= TimeSpan.Zero)
            throw new InvalidOperationException($"GitHub:OAuth:{name} must be a positive duration.");
        return parsed;
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal static class GitHubExternalEndpoint
{
    public static Uri Parse(string? value, string configurationKey, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException($"{configurationKey} must be an absolute HTTP(S) URI.");
        }
        if (environment.IsProduction() && uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"{configurationKey} must use HTTPS in Production.");
        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
        {
            throw new InvalidOperationException($"{configurationKey} may use HTTP only for loopback development or tests.");
        }
        return uri;
    }
}

public sealed record GitHubOAuthPendingState(string State, string ReturnPath, DateTimeOffset ExpiresAtUtc);

public sealed record GitHubOAuthSession(string SessionId, string AccessToken, string Login, string CsrfToken, DateTimeOffset ExpiresAtUtc);

public sealed class GitHubOAuthSessionStore(GitHubOAuthOptions options)
{
    private const int MaximumPendingStates = 1024;
    private const int MaximumSessions = 1024;
    private readonly ConcurrentDictionary<string, GitHubOAuthPendingState> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, GitHubOAuthSession> _sessions = new(StringComparer.Ordinal);

    public GitHubOAuthPendingState CreatePending(string? returnPath, DateTimeOffset now)
    {
        Cleanup(now);
        if (_pending.Count >= MaximumPendingStates)
            throw new GitHubOAuthException("Too many GitHub OAuth requests are pending.");
        var state = RandomToken();
        var pending = new GitHubOAuthPendingState(state, NormalizeReturnPath(returnPath), now + options.PendingStateLifetime);
        _pending[state] = pending;
        return pending;
    }

    public bool TryTakePending(string state, string stateCookie, DateTimeOffset now, out GitHubOAuthPendingState? pending)
    {
        pending = null;
        if (!FixedTimeEquals(state, stateCookie) || !_pending.TryRemove(state, out var candidate))
            return false;
        if (candidate.ExpiresAtUtc <= now)
            return false;
        pending = candidate;
        return true;
    }

    public GitHubOAuthSession CreateSession(string accessToken, string login, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(login);
        Cleanup(now);
        if (_sessions.Count >= MaximumSessions)
            throw new GitHubOAuthException("Too many GitHub OAuth sessions are active.");
        var session = new GitHubOAuthSession(RandomToken(), accessToken, login, RandomToken(), now + options.SessionLifetime);
        _sessions[session.SessionId] = session;
        return session;
    }

    public bool TryGetSession(string? sessionId, DateTimeOffset now, out GitHubOAuthSession? session)
    {
        session = null;
        if (string.IsNullOrWhiteSpace(sessionId) || !_sessions.TryGetValue(sessionId, out var candidate))
            return false;
        if (candidate.ExpiresAtUtc <= now)
        {
            _sessions.TryRemove(sessionId, out _);
            return false;
        }
        session = candidate;
        return true;
    }

    public bool ValidateCsrf(GitHubOAuthSession session, string? csrfToken) =>
        csrfToken is not null && FixedTimeEquals(session.CsrfToken, csrfToken);

    public void RemoveSession(string? sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
            _sessions.TryRemove(sessionId, out _);
    }

    private void Cleanup(DateTimeOffset now)
    {
        foreach (var item in _pending)
        {
            if (item.Value.ExpiresAtUtc <= now)
                _pending.TryRemove(item.Key, out _);
        }
        foreach (var item in _sessions)
        {
            if (item.Value.ExpiresAtUtc <= now)
                _sessions.TryRemove(item.Key, out _);
        }
    }

    private static string NormalizeReturnPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "/";
        if (value.Length > 2048 ||
            !value.StartsWith('/') ||
            value.StartsWith("//", StringComparison.Ordinal) ||
            value.Contains('\\') ||
            value.Any(static character => char.IsControl(character)))
            throw new GitHubOAuthException("The OAuth return path is invalid.");
        return value;
    }

    private static string RandomToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}

public sealed class GitHubOAuthClient(HttpClient httpClient, GitHubOAuthOptions options)
{
    public Uri CreateAuthorizationUri(string state, Uri redirectUri)
    {
        if (!options.Available)
            throw new GitHubOAuthException("GitHub OAuth is not configured.");
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = options.ClientId,
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["scope"] = "gist read:user",
            ["state"] = state,
            ["allow_signup"] = "true"
        };
        return new UriBuilder(options.AuthorizationEndpoint)
        {
            Query = string.Join('&', query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value ?? string.Empty)}"))
        }.Uri;
    }

    public async Task<string> ExchangeCodeAsync(string code, Uri redirectUri, CancellationToken cancellationToken)
    {
        if (!options.Available)
            throw new GitHubOAuthException("GitHub OAuth is not configured.");
        using var request = new HttpRequestMessage(HttpMethod.Post, options.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = options.ClientId!,
                ["client_secret"] = options.ClientSecret!,
                ["code"] = code,
                ["redirect_uri"] = redirectUri.AbsoluteUri
            })
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadFromJsonAsync<OAuthTokenResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || body is null || string.IsNullOrWhiteSpace(body.AccessToken))
            throw new GitHubOAuthException("GitHub rejected the OAuth authorization code.");
        return body.AccessToken;
    }

    private sealed record OAuthTokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("token_type")] string? TokenType,
        [property: JsonPropertyName("scope")] string? Scope,
        [property: JsonPropertyName("error")] string? Error);
}

public sealed class GitHubOAuthException(string message) : Exception(message);
