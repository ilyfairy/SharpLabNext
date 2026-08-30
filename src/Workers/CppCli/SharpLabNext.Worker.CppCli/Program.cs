using SharpLabNext.Contracts;
using SharpLabNext.LanguageWorker.Sdk;
using SharpLabNext.Observability;
using SharpLabNext.Worker.CppCli;

var builder = WebApplication.CreateBuilder(args);
var manifest = LanguageWorkerCapabilityManifestSerializer.Load(Path.Combine(AppContext.BaseDirectory, "language-worker.json"));
var settings = CppCliWorkerSettings.FromConfiguration(builder.Configuration);
if (!File.Exists(settings.CompilerPath))
    throw new InvalidOperationException("The fixed MSVC C++/CLI compiler is missing.");
var identity = new ServiceIdentity(manifest.WorkerId, ServiceKind.ToolchainWorker, settings.Identity.ReleaseId, ProtocolVersion.WorkerV1, manifest.Capabilities, "ready");

builder.AddSharpLabNextObservability(identity.Id, identity.ReleaseId);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<CppCliCompilerProcess>();
builder.Services.AddSharpLabNextLanguageWorker<CppCliBuildService>(identity, manifest, LanguageWorkerHostMetadata.Create(identity.Id, settings.Identity.WorkerImageId, [settings.ReferenceSet.CreateAttestation()]));

var app = builder.Build();
app.MapSharpLabNextLanguageWorker(mapLanguageSessions: false);
app.Run();

public partial class Program;
