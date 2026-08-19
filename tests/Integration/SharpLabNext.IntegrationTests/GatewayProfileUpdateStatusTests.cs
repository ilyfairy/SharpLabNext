using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using SharpLabNext.Contracts;

namespace SharpLabNext.IntegrationTests;

public sealed class GatewayProfileUpdateStatusTests
{
    [Fact]
    public async Task MissingStatusFileReturnsExplicitUnknownForCurrentRelease()
    {
        var missingPath = Path.Combine(
            Path.GetTempPath(),
            $"SharpLabNext.ProfileStatus.{Guid.NewGuid():N}",
            "missing.json");
        await using var factory = new ProfileUpdateStatusGatewayFactory(missingPath);
        using var client = factory.CreateClient();
        var catalog = await GatewayTestCatalog.GetAsync(client);

        using var response = await client.GetAsync(
            "/api/v1/profile-updates",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await response.Content.ReadFromJsonAsync<ProfileUpdateStatusDocument>(
            ContractJson.CreateSerializerOptions(),
            TestContext.Current.CancellationToken);
        Assert.NotNull(status);
        Assert.Equal(ProfileUpdateStatusKind.Unknown, status.Status);
        Assert.False(status.Checked);
        Assert.Equal(catalog.ReleaseId, status.Active.ReleaseId);
        Assert.Null(status.Active.LockDigest);
        Assert.Null(status.UpdateAvailable);
        Assert.Equal(ProfileUpdatePublicStage.None, status.LastStage.Stage);
        Assert.Equal(ProfileUpdatePublicStageOutcome.NotChecked, status.LastStage.Outcome);
    }

    [Fact]
    public async Task PublishedStatusIsReturnedWithoutInternalDetails()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"SharpLabNext.ProfileStatus.{Guid.NewGuid():N}");
        var statusPath = Path.Combine(root, "status.public.json");
        Directory.CreateDirectory(root);
        try
        {
            await using var factory = new ProfileUpdateStatusGatewayFactory(statusPath);
            using var client = factory.CreateClient();
            var catalog = await GatewayTestCatalog.GetAsync(client);
            await File.WriteAllTextAsync(
                statusPath,
                $$"""
                {
                  "schemaVersion": 1,
                  "status": "candidate-failed",
                  "checked": true,
                  "active": {
                    "releaseId": "{{catalog.ReleaseId}}",
                    "lockDigest": "sha256:{{new string('a', 64)}}"
                  },
                  "candidate": {
                    "releaseId": "2026.07.11.2",
                    "lockDigest": "sha256:{{new string('b', 64)}}"
                  },
                  "updateAvailable": true,
                  "checkedAt": "2026-07-11T01:00:00Z",
                  "updatedAt": "2026-07-11T01:05:00Z",
                  "candidatePath": "C:\\private\\candidate",
                  "source": "class SecretSource {}",
                  "lastStage": {
                    "stage": "build",
                    "outcome": "failed",
                    "startedAt": "2026-07-11T01:01:00Z",
                    "completedAt": "2026-07-11T01:02:00Z",
                    "commands": ["dotnet build C:\\private\\source"],
                    "error": {
                      "code": "profile-update.build-failed",
                      "message": "dotnet build failed at C:\\private\\source\\Program.cs"
                    }
                  }
                }
                """,
                TestContext.Current.CancellationToken);

            using var response = await client.GetAsync(
                "/api/v1/profile-updates",
                TestContext.Current.CancellationToken);
            var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain("private", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("dotnet build", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SecretSource", json, StringComparison.Ordinal);
            var status = await response.Content.ReadFromJsonAsync<ProfileUpdateStatusDocument>(
                ContractJson.CreateSerializerOptions(),
                TestContext.Current.CancellationToken);
            Assert.NotNull(status);
            Assert.Equal(ProfileUpdateStatusKind.CandidateFailed, status.Status);
            Assert.True(status.Checked);
            Assert.Equal(catalog.ReleaseId, status.Active.ReleaseId);
            Assert.Equal("2026.07.11.2", status.Candidate?.ReleaseId);
            Assert.True(status.UpdateAvailable);
            Assert.Equal("profile-update.build-failed", status.LastStage.Error?.Code);
            Assert.Equal(
                "Profile candidate build failed; the approved release remains active.",
                status.LastStage.Error?.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

internal sealed class ProfileUpdateStatusGatewayFactory(string statusPath)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ProfileUpdates:StatusPath", statusPath);
    }
}
