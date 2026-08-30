using System.Net.Mime;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using SharpLabNext.ArtifactStore;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.Contracts;
using SharpLabNext.Http;
using SharpLabNext.InternalServices;
using SharpLabNext.Observability;

var builder = WebApplication.CreateBuilder(args);
var internalServiceAuthentication = InternalServiceAuthenticationOptions.FromConfiguration(builder.Configuration, builder.Environment);
var descriptor = new ServiceIdentity("artifact-store", ServiceKind.ArtifactStore, builder.Configuration["ReleaseId"] ?? "development", ProtocolVersion.WorkerV1, ["health", "content-cas-v1", "artifact-cas-v1", "leases-v1"], "local-sqlite-v1");
builder.AddSharpLabNextObservability(descriptor.Id, descriptor.ReleaseId);
builder.Services.AddSingleton(descriptor);
builder.Services.AddOptions<ArtifactStoreOptions>().Bind(builder.Configuration.GetSection(ArtifactStoreOptions.SectionName));
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 320L * 1024 * 1024;
    options.ValueLengthLimit = 4 * 1024 * 1024;
    options.MultipartHeadersLengthLimit = 32 * 1024;
});
builder.Services.ConfigureHttpJsonOptions(options =>
{
    ContractJson.ApplySerializerOptions(options.SerializerOptions);
});
builder.Services.AddSharpLabNextProblemDetails();
builder.Services.AddSingleton<LocalArtifactStore>();
builder.Services.AddHostedService<ArtifactStoreMaintenanceService>();

var app = builder.Build();
app.UseExceptionHandler(errorApp => errorApp.Run(WriteProblemAsync));
app.UseSharpLabNextInternalServiceAuthentication(internalServiceAuthentication);

var store = app.Services.GetRequiredService<LocalArtifactStore>();
await store.InitializeAsync(app.Lifetime.ApplicationStopping);

app.MapGet("/health/live", () => Results.Ok(new { Status = "live" }));
app.MapGet("/health/ready", (ServiceIdentity service) => Results.Ok(new { Status = "ready", service.Id, service.ReleaseId, Storage = "local-cas-sqlite" }));
app.MapGet("/api/v1/artifacts/status", (ServiceIdentity service) => service);

app.MapPut(
    "/internal/v1/contents/sha256/{digest}",
    async (string digest, HttpRequest request, LocalArtifactStore artifactStore, CancellationToken cancellationToken) =>
    {
        var contentRef = ParseContentRef(digest);
        var result = await artifactStore.PutContentAsync(contentRef, request.Body, request.ContentLength, ParseTimeToLive(request), cancellationToken);
        return Results.Ok(result);
    });

app.MapGet(
    "/internal/v1/contents/sha256/{digest}",
    async (string digest, HttpResponse response, LocalArtifactStore artifactStore, CancellationToken cancellationToken) =>
    {
        var contentRef = ParseContentRef(digest);
        var content = await artifactStore.OpenContentReadAsync(contentRef, cancellationToken);
        response.ContentLength = content.Size;
        response.Headers.ETag = new EntityTagHeaderValue($"\"{content.ContentRef.Value}\"").ToString();
        return Results.Stream(content.Stream, MediaTypeNames.Application.Octet);
    });

app.MapPut(
    "/internal/v1/artifacts/sha256/{digest}",
    async (string digest, HttpRequest request, LocalArtifactStore artifactStore, CancellationToken cancellationToken) =>
    {
        if (!request.HasFormContentType)
        {
            throw new ArtifactValidationException("Artifact PUT requires multipart/form-data.");
        }

        var form = await request.ReadFormAsync(cancellationToken);
        var manifestFieldNames = form.Keys.Where(name => string.Equals(name, ArtifactStoreProtocol.ManifestPartName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (manifestFieldNames.Any(name => !string.Equals(name, ArtifactStoreProtocol.ManifestPartName, StringComparison.Ordinal)) || manifestFieldNames.Length != 1 || form[manifestFieldNames[0]].Count != 1)
        {
            throw new ArtifactValidationException("Artifact PUT requires exactly one Manifest field.");
        }

        ArtifactManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ArtifactManifest>(form[manifestFieldNames[0]][0]!, ContractJson.CreateSerializerOptions()) ?? throw new ArtifactValidationException("Artifact manifest was empty.");
        }
        catch (JsonException exception)
        {
            throw new ArtifactValidationException("Artifact manifest JSON is invalid.", exception);
        }

        if (form.Files.Any(file => !string.Equals(file.Name, ArtifactStoreProtocol.FilesPartName, StringComparison.Ordinal)))
        {
            throw new ArtifactValidationException("Unknown multipart file field.");
        }

        var uploads = form.Files.Select(file => new ArtifactUploadSource(file.FileName, file.OpenReadStream)).ToArray();
        var result = await artifactStore.PutArtifactAsync(ParseArtifactRef(digest), manifest, uploads, ParseTimeToLive(request), cancellationToken);
        return Results.Ok(result);
    });

app.MapGet(
    "/internal/v1/artifacts/sha256/{digest}",
    async (string digest, LocalArtifactStore artifactStore, CancellationToken cancellationToken) =>
    {
        var descriptor = await artifactStore.GetArtifactAsync(ParseArtifactRef(digest), cancellationToken);
        return descriptor is null ? Results.NotFound() : Results.Ok(descriptor);
    });

app.MapGet(
    "/internal/v1/artifacts/sha256/{digest}/files/{**path}",
    async (string digest, string path, HttpResponse response, LocalArtifactStore artifactStore, CancellationToken cancellationToken) =>
    {
        var content = await artifactStore.OpenArtifactFileReadAsync(ParseArtifactRef(digest), path, cancellationToken);
        response.ContentLength = content.Size;
        response.Headers.ETag = new EntityTagHeaderValue($"\"{content.ContentRef.Value}\"").ToString();
        return Results.Stream(content.Stream, MediaTypeNames.Application.Octet);
    });

app.MapPost(
    "/internal/v1/artifacts/sha256/{digest}/leases",
    async (string digest, ArtifactLeaseRequest request, LocalArtifactStore artifactStore, CancellationToken cancellationToken) =>
    {
        var lease = await artifactStore.AcquireLeaseAsync(ParseArtifactRef(digest), request.Owner, TimeSpan.FromSeconds(request.DurationSeconds), cancellationToken);
        return Results.Ok(ToResponse(lease));
    });

app.MapPut(
    "/internal/v1/leases/{leaseToken}",
    async (string leaseToken, ArtifactLeaseRenewalRequest request, LocalArtifactStore artifactStore, CancellationToken cancellationToken) =>
    {
        var lease = await artifactStore.RenewLeaseAsync(leaseToken, TimeSpan.FromSeconds(request.DurationSeconds), cancellationToken);
        return Results.Ok(ToResponse(lease));
    });

app.MapDelete(
    "/internal/v1/leases/{leaseToken}",
    async (string leaseToken, LocalArtifactStore artifactStore, CancellationToken cancellationToken) =>
    {
        await artifactStore.ReleaseLeaseAsync(leaseToken, cancellationToken);
        return Results.NoContent();
    });

app.MapPost("/internal/v1/maintenance/collect", async (GarbageCollectionRequest request, LocalArtifactStore artifactStore, CancellationToken cancellationToken) => Results.Ok(await artifactStore.CollectGarbageAsync(request.MaxArtifacts, request.MaxContents, cancellationToken)));

app.Run();

static ContentRef ParseContentRef(string digest)
{
    try
    {
        return ArtifactStoreProtocol.ContentRefFromDigest(digest);
    }
    catch (ArgumentException exception)
    {
        throw new ArtifactValidationException("Content reference is invalid.", exception);
    }
}

static ArtifactRef ParseArtifactRef(string digest)
{
    try
    {
        return ArtifactStoreProtocol.ArtifactRefFromDigest(digest);
    }
    catch (ArgumentException exception)
    {
        throw new ArtifactValidationException("Artifact reference is invalid.", exception);
    }
}

static TimeSpan? ParseTimeToLive(HttpRequest request)
{
    if (!PascalCaseQuery.TryGetOptionalInt32(request, "TtlSeconds", out var seconds))
    {
        throw new ArtifactValidationException("TtlSeconds must use its exact PascalCase spelling and be a positive integer.");
    }

    if (seconds is null)
    {
        return null;
    }

    if (seconds <= 0)
    {
        throw new ArtifactValidationException("TtlSeconds must be a positive integer.");
    }

    return TimeSpan.FromSeconds(seconds.Value);
}

static ArtifactLeaseResponse ToResponse(LeaseMetadata lease) =>
    new(lease.LeaseToken, lease.ArtifactRef, lease.Owner, lease.ExpiresAt);

static async Task WriteProblemAsync(HttpContext context)
{
    var exception = context.Features.Get<IExceptionHandlerPathFeature>()?.Error;
    var (status, title, detail) = exception switch
    {
        ArtifactValidationException validation =>
            (StatusCodes.Status400BadRequest, "Artifact request is invalid", validation.Message),
        ArtifactNotFoundException notFound =>
            (StatusCodes.Status404NotFound, "Artifact was not found", notFound.Message),
        ArtifactConflictException conflict =>
            (StatusCodes.Status409Conflict, "Artifact conflict", conflict.Message),
        ArtifactLimitExceededException limit =>
            (StatusCodes.Status413PayloadTooLarge, "Artifact limit exceeded", limit.Message),
        BadHttpRequestException badRequest =>
            (badRequest.StatusCode, "Artifact request is invalid", "The HTTP request could not be processed."),
        _ =>
            (StatusCodes.Status500InternalServerError, "Artifact Store failure", "The Artifact Store could not complete the request.")
    };

    context.Response.StatusCode = status;
    await context.Response.WriteProblemDetailsAsync(new ProblemDetails { Status = status, Title = title, Detail = detail, Instance = context.Request.Path, Extensions = { ["TraceId"] = context.TraceIdentifier } }, context.RequestAborted);
}

public partial class Program;
