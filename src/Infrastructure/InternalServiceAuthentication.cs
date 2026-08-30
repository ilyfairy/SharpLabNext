using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Net.Http.Headers;
using SharpLabNext.Contracts;

namespace SharpLabNext.InternalServices;

public sealed class InternalServiceAuthenticationOptions
{
    public const string SectionName = "InternalServiceAuth";
    public const string AuthenticationScheme = "Bearer";
    public const int MaximumTokenLength = 8192;
    private const int MinimumTokenLength = 32;

    private readonly byte[]? _tokenHash;

    private InternalServiceAuthenticationOptions(bool required, string? token)
    {
        Required = required;
        Token = token;
        _tokenHash = token is null ? null : SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }

    public bool Required { get; }

    public bool Enabled => Token is not null;

    public string? Token { get; }

    public static InternalServiceAuthenticationOptions FromConfiguration(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var section = configuration.GetSection(SectionName);
        var required = ParseRequired(section["Required"], environment.IsProduction());
        var tokenFile = NullIfWhiteSpace(section["TokenFile"]);
        var inlineToken = NullIfWhiteSpace(section["Token"]);
        if (tokenFile is not null && inlineToken is not null)
        {
            throw new InvalidOperationException($"{SectionName}:TokenFile and {SectionName}:Token cannot both be configured.");
        }

        if (environment.IsProduction() && inlineToken is not null)
        {
            throw new InvalidOperationException($"{SectionName}:Token is not allowed in Production; use a read-only secret file.");
        }

        var token = tokenFile is null
            ? inlineToken : ReadTokenFile(tokenFile, environment.ContentRootPath);
        if (token is not null)
            ValidateToken(token);
        if (required && token is null)
        {
            throw new InvalidOperationException($"{SectionName}:TokenFile is required when internal service authentication is enabled.");
        }

        return new InternalServiceAuthenticationOptions(required, token);
    }

    public void ConfigureClient(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (Token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(AuthenticationScheme, Token);
    }

    internal bool IsAuthorized(string? authorization)
    {
        if (!Enabled)
            return !Required;
        if (!AuthenticationHeaderValue.TryParse(authorization, out var header) || !string.Equals(header.Scheme, AuthenticationScheme, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(header.Parameter))
        {
            return false;
        }

        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(header.Parameter));
        return CryptographicOperations.FixedTimeEquals(_tokenHash!, presentedHash);
    }

    private static bool ParseRequired(string? value, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;
        if (!bool.TryParse(value, out var required))
            throw new InvalidOperationException($"{SectionName}:Required must be true or false.");
        return required;
    }

    private static string ReadTokenFile(string configuredPath, string contentRootPath)
    {
        var path = Path.IsPathFullyQualified(configuredPath)
            ? configuredPath : Path.Combine(contentRootPath, configuredPath);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new InvalidOperationException($"The configured internal service token file '{fullPath}' does not exist.");

        return File.ReadAllText(fullPath).TrimEnd('\r', '\n');
    }

    private static void ValidateToken(string token)
    {
        if (token.Length is < MinimumTokenLength or > MaximumTokenLength)
        {
            throw new InvalidOperationException($"The internal service token must be between {MinimumTokenLength} and {MaximumTokenLength} characters.");
        }
        if (token.Any(static character => character is <= ' ' or >= '\u007f'))
            throw new InvalidOperationException("The internal service token must contain visible ASCII characters only.");
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public static class InternalServiceAuthenticationExtensions
{
    public static IApplicationBuilder UseSharpLabNextInternalServiceAuthentication(this IApplicationBuilder app, InternalServiceAuthenticationOptions options) =>
        app.UseMiddleware<InternalServiceAuthenticationMiddleware>(options);
}

public sealed class InternalServiceAuthenticationMiddleware(RequestDelegate next, InternalServiceAuthenticationOptions options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (IsAnonymousHealthEndpoint(context.Request.Path) || IsAuthorized(context.Request.Headers.Authorization))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/problem+json";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers[HeaderNames.WWWAuthenticate] = InternalServiceAuthenticationOptions.AuthenticationScheme;
        await context.Response.WriteAsJsonAsync(new { Type = "https://sharplabnext.dev/problems/internal-service-authentication-required", Title = "Internal service authentication is required", Status = StatusCodes.Status401Unauthorized, TraceId = context.TraceIdentifier }, ContractJson.CreateSerializerOptions(), context.RequestAborted).ConfigureAwait(false);
    }

    private bool IsAuthorized(Microsoft.Extensions.Primitives.StringValues values) =>
        values.Count switch
        {
            0 => options.IsAuthorized(null),
            1 => options.IsAuthorized(values[0]),
            _ => false
        };

    private static bool IsAnonymousHealthEndpoint(PathString path) =>
        path == "/health/live" || path == "/health/ready";
}
