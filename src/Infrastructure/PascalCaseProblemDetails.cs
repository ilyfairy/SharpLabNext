using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SharpLabNext.Contracts;

namespace SharpLabNext.Http;

/// <summary>
/// Registers the SharpLabNext problem-details writer. ASP.NET's built-in
/// ProblemDetails metadata intentionally uses RFC 9110 lower-case names,
/// which are not the PascalCase names used by our business wire contract.
/// </summary>
internal static class SharpLabNextProblemDetailsExtensions
{
    public static IServiceCollection AddSharpLabNextProblemDetails(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions();
        // Register our writer before AddProblemDetails adds the framework
        // writer. ProblemDetailsService uses the first writer that can write.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IProblemDetailsWriter, PascalCaseProblemDetailsWriter>());
        services.AddProblemDetails();
        return services;
    }

    public static ValueTask WriteProblemDetailsAsync(this HttpResponse response, ProblemDetails problem, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(problem);
        return PascalCaseProblemDetailsWriter.WriteAsync(response, problem, cancellationToken);
    }
}

internal sealed class PascalCaseProblemDetailsWriter : IProblemDetailsWriter
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public bool CanWrite(ProblemDetailsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var contentType = context.HttpContext.Response.ContentType;
        return contentType is null ||
            contentType.StartsWith("application/problem+json", StringComparison.OrdinalIgnoreCase) ||
            contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase);
    }

    public ValueTask WriteAsync(ProblemDetailsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return WriteAsync(context.HttpContext.Response, context.ProblemDetails, context.HttpContext.RequestAborted);
    }

    internal static async ValueTask WriteAsync(HttpResponse response, ProblemDetails problem, CancellationToken cancellationToken)
    {
        response.StatusCode = problem.Status ?? response.StatusCode;
        response.ContentType = "application/problem+json; charset=utf-8";

        // ProblemDetails properties carry JsonPropertyName attributes from
        // ASP.NET, so naming policies cannot rename them. Flatten the object
        // into an explicit business-wire dictionary instead.
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Type"] = problem.Type,
            ["Title"] = problem.Title,
            ["Status"] = problem.Status,
            ["Detail"] = problem.Detail,
            ["Instance"] = problem.Instance
        };

        foreach (var extension in problem.Extensions)
        {
            var name = ToPascalCase(extension.Key);
            if (name.Length == 0 || payload.ContainsKey(name))
                continue;
            payload[name] = extension.Value;
        }

        await JsonSerializer.SerializeAsync(response.Body, payload, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static string ToPascalCase(string name)
    {
        if (string.IsNullOrEmpty(name) || char.IsUpper(name[0]))
            return name;
        return char.ToUpperInvariant(name[0]) + name[1..];
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = ContractJson.CreateSerializerOptions();
        // Problem details are assembled as a dictionary so the standard
        // ASP.NET property attributes cannot force lower-camel names.
        options.DictionaryKeyPolicy = null;
        return options;
    }
}
