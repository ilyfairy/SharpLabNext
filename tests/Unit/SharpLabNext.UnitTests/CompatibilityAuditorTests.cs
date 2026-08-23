using SharpLabNext.Catalog;
using SharpLabNext.CompatibilityCli;

namespace SharpLabNext.UnitTests;

public sealed class CompatibilityAuditorTests
{
    [Fact]
    public async Task DevelopmentCatalogPassesFullCompatibilityAudit()
    {
        var catalogTask = CatalogLoader.LoadCatalogAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "catalog.json"),
            TestContext.Current.CancellationToken);
        var lockTask = CatalogLoader.LoadReleaseLockAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "lock.json"),
            TestContext.Current.CancellationToken);
        await Task.WhenAll(catalogTask, lockTask);

        var report = CompatibilityAuditor.Audit(
            await catalogTask,
            await lockTask,
            DateTimeOffset.UnixEpoch);

        Assert.True(report.IsValid, string.Join(Environment.NewLine, report.Issues));
        Assert.NotEmpty(report.Matrix);
        Assert.Contains(report.Matrix, static entry =>
            entry.ToolchainId == "roslyn-stable" &&
            entry.ReferenceSetId == "net10-ref" &&
            entry.RuntimeId == "dotnet-11-preview-linux-x64" &&
            entry.OutputId == "run" &&
            entry.Disposition == CompatibilityDisposition.Supported);
        Assert.Contains(report.Matrix, static entry =>
            entry.ToolchainId == "roslyn-const-generics" &&
            entry.ReferenceSetId == "const-generics-ref" &&
            entry.RuntimeId == "const-generics-linux-x64" &&
            entry.OutputId == "run" &&
            entry.Disposition == CompatibilityDisposition.Supported);
        Assert.Contains(report.Matrix, static entry =>
            entry.ToolchainId == "roslyn-const-generics" &&
            entry.ReferenceSetId == "const-generics-ref" &&
            entry.RuntimeId == "dotnet-10-linux-x64" &&
            entry.OutputId == "run" &&
            entry.Disposition == CompatibilityDisposition.Rejected);
        Assert.Contains(report.Matrix, static entry =>
            entry.LanguageId == "gsharp" &&
            entry.ToolchainId == "gsharp-stable" &&
            entry.ReferenceSetId == "net10-ref" &&
            entry.RuntimeId == "dotnet-11-preview-linux-x64" &&
            entry.OutputId == "run" &&
            entry.Disposition == CompatibilityDisposition.Supported);
        Assert.DoesNotContain(report.Matrix, static entry =>
            entry.ToolchainId == "gsharp-stable" &&
            entry.ReferenceSetId == "net11-preview-ref");
        Assert.Contains(report.Matrix, static entry =>
            entry.LanguageId == "cppcli" &&
            entry.ToolchainId == "msvc-cppcli-netfx48" &&
            entry.ReferenceSetId == "netfx48-ref" &&
            entry.RuntimeId == "wine-netfx48-linux-x64" &&
            entry.OutputId == "run" &&
            entry.Disposition == CompatibilityDisposition.Supported);
        Assert.Contains(report.Matrix, static entry =>
            entry.LanguageId == "csharp" &&
            entry.ToolchainId == "roslyn-stable-netfx48" &&
            entry.ReferenceSetId == "netfx48-managed-ref" &&
            entry.RuntimeId == "wine-netfx48-linux-x64" &&
            entry.OutputId == "run" &&
            entry.Disposition == CompatibilityDisposition.Supported);
        Assert.Contains(report.Matrix, static entry =>
            entry.LanguageId == "visual-basic" &&
            entry.ToolchainId == "roslyn-stable-netfx48" &&
            entry.ReferenceSetId == "netfx48-managed-ref" &&
            entry.RuntimeId == "wine-netfx48-linux-x64" &&
            entry.OutputId == "run" &&
            entry.Disposition == CompatibilityDisposition.Supported);
        Assert.Contains(report.Matrix, static entry =>
            entry.ToolchainId == "roslyn-stable-netfx48" &&
            entry.ReferenceSetId == "netfx48-managed-ref" &&
            entry.RuntimeId == "mono-6.12-linux-x64" &&
            entry.OutputId == "jit-asm" &&
            entry.Disposition == CompatibilityDisposition.Supported);
        Assert.Contains(report.Matrix, static entry =>
            entry.ToolchainId == "roslyn-stable-netfx48" &&
            entry.ReferenceSetId == "netfx48-managed-ref" &&
            entry.OutputId == "jit-asm" &&
            entry.RuntimeId == "wine-netfx48-linux-x64" &&
            entry.Disposition == CompatibilityDisposition.Supported);
        Assert.Contains(report.Matrix, static entry =>
            entry.ToolchainId == "roslyn-stable-netfx48" &&
            entry.ReferenceSetId == "netfx20-managed-ref" &&
            entry.OutputId == "jit-asm" &&
            entry.RuntimeId == "wine-netfx48-linux-x64" &&
            entry.Disposition == CompatibilityDisposition.Rejected);
        Assert.DoesNotContain(report.Matrix, static entry =>
            entry.ToolchainId == "roslyn-stable-netfx48" &&
            entry.OutputId is "execution-flow" or "run-il" or "il-verify" &&
            entry.Disposition == CompatibilityDisposition.Supported);
        Assert.DoesNotContain(report.Matrix, static entry =>
            entry.ToolchainId == "msvc-cppcli-netfx48" &&
            entry.OutputId is "jit-asm" or "execution-flow" &&
            entry.Disposition == CompatibilityDisposition.Supported);
        Assert.Contains(report.Matrix, static entry =>
            entry.LanguageId == "jsharp" &&
            entry.ToolchainId == "vjc-jsharp20" &&
            entry.ReferenceSetId == "jsharp20-ref" &&
            entry.RuntimeId == "wine-jsharp20-linux-x64" &&
            entry.OutputId == "run" &&
            entry.Disposition == CompatibilityDisposition.Supported);
        Assert.Contains(report.Matrix, static entry =>
            entry.LanguageId == "jsharp" &&
            entry.ToolchainId == "vjc-jsharp20" &&
            entry.ReferenceSetId == "jsharp20-ref" &&
            entry.RuntimeId == "wine-netfx48-linux-x64" &&
            entry.OutputId == "run" &&
            entry.Disposition == CompatibilityDisposition.Rejected);
        Assert.DoesNotContain(report.Matrix, static entry =>
            entry.ToolchainId == "vjc-jsharp20" &&
            entry.OutputId is "ast" or "il-verify" or "jit-asm" or "execution-flow" or "run-il" &&
            entry.Disposition == CompatibilityDisposition.Supported);
    }

    [Fact]
    public async Task AuditRejectsCatalogAndLockFromDifferentReleases()
    {
        var catalog = await CatalogLoader.LoadCatalogAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "catalog.json"),
            TestContext.Current.CancellationToken);
        var releaseLock = await CatalogLoader.LoadReleaseLockAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "lock.json"),
            TestContext.Current.CancellationToken);

        var report = CompatibilityAuditor.Audit(
            catalog,
            releaseLock with { ReleaseId = "different-release" },
            DateTimeOffset.UnixEpoch);

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, static issue => issue.Contains("does not match", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MarkdownReportContainsAuditStatusAndMatrix()
    {
        var catalog = await CatalogLoader.LoadCatalogAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "catalog.json"),
            TestContext.Current.CancellationToken);
        var releaseLock = await CatalogLoader.LoadReleaseLockAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "lock.json"),
            TestContext.Current.CancellationToken);
        var report = CompatibilityAuditor.Audit(catalog, releaseLock, DateTimeOffset.UnixEpoch);

        var markdown = CompatibilityCliProgram.ToMarkdown(report);

        Assert.Contains("Status: **valid**", markdown, StringComparison.Ordinal);
        Assert.Contains("| Language | Toolchain | API | Output | Runtime | Result |", markdown, StringComparison.Ordinal);
    }
}
