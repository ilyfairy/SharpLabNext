using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.ArtifactWorker.Sdk;
using SharpLabNext.Contracts;
using SharpLabNext.InternalServices;
using SharpLabNext.Observability;
using SharpLabNext.Worker.Artifacts.JSIL;

var builder = WebApplication.CreateBuilder(args);
var internalServiceAuthentication = InternalServiceAuthenticationOptions.FromConfiguration(
    builder.Configuration,
    builder.Environment);
var manifest = ArtifactWorkerCapabilityManifestSerializer.Load(
    Path.Combine(AppContext.BaseDirectory, "artifact-worker.json"));
var settings = JsilWorkerSettings.FromConfiguration(builder.Configuration, manifest);
var identity = new ServiceIdentity(
    manifest.WorkerId,
    ServiceKind.ArtifactWorker,
    settings.ReleaseId,
    ProtocolVersion.WorkerV1,
    manifest.Capabilities,
    "starting");
builder.AddSharpLabNextObservability(identity.Id, identity.ReleaseId);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    ContractJson.ApplySerializerOptions(options.SerializerOptions);
});
builder.Services.AddSingleton(settings);
builder.Services.AddHttpClient<IArtifactStoreClient, ArtifactStoreClient>(client =>
{
    client.BaseAddress = new Uri(settings.ArtifactStoreBaseUrl, UriKind.Absolute);
    client.Timeout = Timeout.InfiniteTimeSpan;
    internalServiceAuthentication.ConfigureClient(client);
});
builder.Services.AddSingleton<IJsilArtifactMaterializer, JsilArtifactMaterializer>();
builder.Services.AddSingleton<IJsilProcessRunner, JsilProcessRunner>();
builder.Services.AddSingleton<JsilArtifactHandler>();
builder.Services.AddSingleton<IArtifactRenderHandler>(services =>
    services.GetRequiredService<JsilArtifactHandler>());
builder.Services.AddArtifactWorkerReadinessCheck<JsilReadinessCheck>();
builder.Services.AddSharpLabNextArtifactWorker(identity, settings.WorkerImageId, manifest);

var app = builder.Build();
app.MapSharpLabNextArtifactWorker();
app.Run();

public partial class Program;
