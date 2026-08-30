using Microsoft.Extensions.Logging.Abstractions;
using SharpLabNext.Contracts;

namespace SharpLabNext.Worker.GSharp.Tests;

public sealed class GSharpLanguageSessionServiceTests
{
    [Theory]
    [InlineData(GSharpToolchain.ToolchainId)]
    [InlineData(GSharpToolchain.LegacyToolchainId)]
    public async Task SessionBindsSelectedToolchainProfile(string toolchainId)
    {
        var root = GSharpTestSettings.CreateRoot();
        try
        {
            var settings = GSharpTestSettings.CreateSettings(root);
            var service = new GSharpLanguageSessionService(settings, GSharpTestSettings.LoadManifest(), NullLoggerFactory.Instance);
            var build = GSharpTestSettings.CreateRequest(BuildTarget.CompileCheck, "package Session\n\nlet answer = 42\n", BuildOutputKind.Auto, toolchainId);
            var request = new OpenLanguageSessionRequest("request-session", "pipeline-session", GSharpToolchain.LanguageId, toolchainId, build.ReferenceSetId, build.Workspace);

            var session = await service.OpenAsync(request, TestContext.Current.CancellationToken);

            var expected = settings.GetToolchain(toolchainId);
            Assert.Equal(toolchainId, session.ToolchainId);
            Assert.Equal($"{expected.CompilerVersion}@{expected.CompilerCommit}", session.CompilerBuildIdentity);
            Assert.True(await service.CloseAsync(session.SessionId, TestContext.Current.CancellationToken));
        }
        finally
        {
            GSharpTestSettings.DeleteRoot(root);
        }
    }
}
