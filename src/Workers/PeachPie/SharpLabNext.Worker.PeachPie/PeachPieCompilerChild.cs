using SharpLabNext.Contracts;
using SharpLabNext.LanguageWorker.Sdk;
using SharpLabNext.WorkerHost;

namespace SharpLabNext.Worker.PeachPie;

public static class PeachPieCompilerChild
{
    public const string ChildArgument = "--sharplabnext-peachpie-compiler-child";

    public static bool IsInvocation(string[] args) =>
        args.Length == 1 && StringComparer.Ordinal.Equals(args[0], ChildArgument);

    public static async Task RunAsync(WebApplicationBuilder builder)
    {
        var manifest = LanguageWorkerCapabilityManifestSerializer.Load(
            Path.Combine(AppContext.BaseDirectory, "language-worker.json"));
        var settings = PeachPieWorkerSettings.FromConfiguration(builder.Configuration);
        var referenceSets = new PeachPieReferenceSetProvider(
            settings.ReferenceSets,
            builder.Environment.IsProduction() ||
            builder.Configuration.GetValue("ReferenceSetAttestation:Required", false));
        var compiler = new PeachPieCompiler(referenceSets, settings, manifest);
        var output = Console.OpenStandardOutput();
        try
        {
            var request = await CompilerChildProtocol.ReadRequestAsync<BuildRequest>(
                Console.OpenStandardInput(),
                settings.BuildProcess.MaximumRequestBytes,
                CancellationToken.None).ConfigureAwait(false);
            var response = await compiler.CompileAsync(request, CancellationToken.None).ConfigureAwait(false);
            await CompilerChildProtocol.WriteSuccessAsync(
                output,
                response,
                settings.BuildProcess.MaximumResponseBytes,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (PeachPieBuildRequestValidationException exception)
        {
            await WriteFailureAsync(CompilerChildFailureKind.InvalidRequest, exception.Message);
        }
        catch (PeachPieReferenceSetUnavailableException exception)
        {
            await WriteFailureAsync(CompilerChildFailureKind.ReferenceSetUnavailable, exception.Message);
        }
        catch (PeachPieBuildOutputLimitExceededException exception)
        {
            await WriteFailureAsync(CompilerChildFailureKind.OutputLimitExceeded, exception.Message);
        }
        catch (PeachPieCompilerFailureException exception)
        {
            await WriteFailureAsync(CompilerChildFailureKind.CompilerFailure, exception.Message);
        }
        catch (OperationCanceledException)
        {
            await WriteFailureAsync(
                CompilerChildFailureKind.DeadlineExceeded,
                "The PeachPie compiler process deadline elapsed.");
        }
        catch (Exception)
        {
            await WriteFailureAsync(
                CompilerChildFailureKind.Internal,
                "The PeachPie compiler process failed.");
        }

        async Task WriteFailureAsync(CompilerChildFailureKind kind, string message) =>
            await CompilerChildProtocol.WriteFailureAsync<PeachPieCompilerResponse>(
                output,
                kind,
                message,
                settings.BuildProcess.MaximumResponseBytes,
                CancellationToken.None).ConfigureAwait(false);
    }
}
