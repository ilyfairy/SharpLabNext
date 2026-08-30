using SharpLabNext.Contracts;
using SharpLabNext.LanguageWorker.Sdk;
using SharpLabNext.Observability;
using SharpLabNext.Worker.GSharp;

var builder = WebApplication.CreateBuilder(args);
var manifest = LanguageWorkerCapabilityManifestSerializer.Load(Path.Combine(AppContext.BaseDirectory, "language-worker.json"));
var settings = GSharpWorkerSettings.FromConfiguration(builder.Configuration);
if (!manifest.ToolchainIds.Order(StringComparer.Ordinal).SequenceEqual(settings.Toolchains.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal))
{
    throw new InvalidOperationException("The configured G# toolchain profiles do not match the capability manifest.");
}
foreach (var toolchain in settings.Toolchains.Values)
{
    if (!File.Exists(toolchain.CompilerAssemblyPath))
        throw new InvalidOperationException($"The fixed G# compiler assembly for '{toolchain.ToolchainId}' is missing.");
    if (!File.Exists(toolchain.LanguageServerAssemblyPath))
        throw new InvalidOperationException($"The fixed G# language server assembly for '{toolchain.ToolchainId}' is missing.");
}
var referenceSets = new GSharpReferenceSetProvider(settings.ReferenceSets, builder.Environment.IsProduction() || builder.Configuration.GetValue("ReferenceSetAttestation:Required", false));
var identity = new ServiceIdentity(manifest.WorkerId, ServiceKind.ToolchainWorker, settings.Identity.ReleaseId, ProtocolVersion.WorkerV1, manifest.Capabilities, "ready");

builder.AddSharpLabNextObservability(identity.Id, identity.ReleaseId);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(referenceSets);
builder.Services.AddSingleton<GSharpCompilerProcess>();
builder.Services.AddSharpLabNextLanguageWorker<GSharpBuildService, GSharpLanguageSessionService>(identity, manifest, LanguageWorkerHostMetadata.Create(identity.Id, settings.Identity.WorkerImageId, referenceSets.Attestations));

var app = builder.Build();
app.MapSharpLabNextLanguageWorker();
app.Run();

public partial class Program;
