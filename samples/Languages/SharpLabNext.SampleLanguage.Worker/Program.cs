using SharpLabNext.Contracts;
using SharpLabNext.LanguageWorker.Sdk;
using SharpLabNext.Observability;
using SharpLabNext.SampleLanguage.Worker;
using SharpLabNext.WorkerHost;

var builder = WebApplication.CreateBuilder(args);
var manifestPath = Path.Combine(AppContext.BaseDirectory, "language-worker.json");
var manifest = LanguageWorkerCapabilityManifestSerializer.Load(manifestPath);
var identity = new ServiceIdentity(manifest.WorkerId, ServiceKind.ToolchainWorker, builder.Configuration["MINILANG_RELEASE_ID"] ?? "content", ProtocolVersion.WorkerV1, manifest.Capabilities, "ready");
var workerIdentity = new MiniLanguageWorkerIdentity(identity.ReleaseId, manifest.WorkerId, builder.Configuration["MINILANG_COMPILER_VERSION"] ?? MiniLanguageCompiler.Version, builder.Configuration["MINILANG_COMPILER_COMMIT"], builder.Configuration["MINILANG_WORKER_IMAGE_ID"] ?? $"sha256:{new string('0', 64)}");
var referenceSets = MiniLanguageReferenceSetAttestations.Load(builder.Configuration, builder.Environment, manifest.SupportedReferenceSetIds);

builder.AddSharpLabNextObservability(identity.Id, identity.ReleaseId);
builder.Services.AddSingleton(workerIdentity);
builder.Services.AddSharpLabNextLanguageWorker<MiniLanguageBuildService, MiniLanguageSessionService>(identity, manifest, LanguageWorkerHostMetadata.Create(identity.Id, workerIdentity.WorkerImageId, referenceSets));

var app = builder.Build();
app.MapSharpLabNextLanguageWorker();
app.Run();

public partial class Program;
