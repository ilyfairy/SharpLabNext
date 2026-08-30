using SharpLabNext.Contracts;
using SharpLabNext.LanguageWorker.Sdk;
using SharpLabNext.Observability;
using SharpLabNext.Worker.PeachPie;
using SharpLabNext.WorkerHost;

if (PeachPieCompilerChild.IsInvocation(args))
{
    await PeachPieCompilerChild.RunAsync(WebApplication.CreateBuilder([]));
    return;
}

var builder = WebApplication.CreateBuilder(args);
var manifest = LanguageWorkerCapabilityManifestSerializer.Load(Path.Combine(AppContext.BaseDirectory, "language-worker.json"));
var settings = PeachPieWorkerSettings.FromConfiguration(builder.Configuration);
if (!settings.BuildProcess.Enabled && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("PeachPie:BuildProcess:Enabled can only be false in Development.");
if (!File.Exists(settings.RuntimeAssemblyPath) || !File.Exists(settings.LibraryAssemblyPath) || !File.Exists(settings.MonoUnixNativeLibraryPath))
    throw new InvalidOperationException("The pinned PeachPie runtime support files are unavailable.");
var referenceSets = new PeachPieReferenceSetProvider(settings.ReferenceSets, builder.Environment.IsProduction() || builder.Configuration.GetValue("ReferenceSetAttestation:Required", false));
var identity = new ServiceIdentity(PeachPieToolchain.ToolchainId, ServiceKind.ToolchainWorker, settings.Identity.ReleaseId, ProtocolVersion.WorkerV1, manifest.Capabilities, "ready");
var hostMetadata = LanguageWorkerHostMetadata.Create(identity.Id, settings.Identity.WorkerImageId, referenceSets.Attestations);

builder.AddSharpLabNextObservability(identity.Id, identity.ReleaseId);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(referenceSets);
builder.Services.AddSingleton<PeachPieCompiler>();
builder.Services.AddSingleton<ICompilerProcessRunner>(new CompilerProcessRunner(settings.BuildProcess));
builder.Services.AddSharpLabNextLanguageWorker<PeachPieBuildService>(identity, manifest, hostMetadata);

var app = builder.Build();
app.MapSharpLabNextLanguageWorker(mapLanguageSessions: false);
app.Use(async (context, next) =>
{
    if (HttpMethods.IsGet(context.Request.Method) && context.Request.Path.Equals("/api/v1/worker/describe", StringComparison.Ordinal))
    {
        var descriptor = new WorkerDescriptor(
            identity,
            hostMetadata.InstanceId,
            WorkerKind.Toolchain,
            hostMetadata.WorkerImageId,
            identity.Protocol,
            [identity.Protocol],
            manifest.Capabilities.Select(capability => new WorkerCapabilityDescriptor(capability, 1, Available: true, manifest.ToolchainIds)).ToArray(),
            manifest.ToolchainIds,
            hostMetadata.StartedAtUtc,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["compilerVersion"] = settings.Identity.CompilerVersion,
                ["compilerCommit"] = settings.Identity.CompilerCommit
            },
            hostMetadata.ReferenceSets);
        await Results.Ok(descriptor).ExecuteAsync(context);
        return;
    }
    await next(context);
});
app.Run();

public partial class Program;
