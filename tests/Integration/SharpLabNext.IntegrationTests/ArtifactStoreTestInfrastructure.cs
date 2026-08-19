using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Contracts;

namespace SharpLabNext.IntegrationTests;

internal sealed class ArtifactStoreProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _standardOutput;
    private readonly Task<string> _standardError;
    private readonly bool _deleteRootOnDispose;

    private ArtifactStoreProcess(
        Process process,
        Task<string> standardOutput,
        Task<string> standardError,
        string rootPath,
        bool deleteRootOnDispose,
        HttpClient httpClient)
    {
        _process = process;
        _standardOutput = standardOutput;
        _standardError = standardError;
        _deleteRootOnDispose = deleteRootOnDispose;
        RootPath = rootPath;
        HttpClient = httpClient;
        Client = new ArtifactStoreClient(httpClient);
    }

    public string RootPath { get; }

    public HttpClient HttpClient { get; }

    public ArtifactStoreClient Client { get; }

    public static async Task<ArtifactStoreProcess> StartAsync(
        CancellationToken cancellationToken,
        string? rootPath = null,
        bool deleteRootOnDispose = true,
        string? internalServiceToken = null)
    {
        var repositoryRoot = FindRepositoryRoot();
        var storeRoot = rootPath ?? Path.Combine(
            Path.GetTempPath(),
            "SharpLabNext-ArtifactStoreTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storeRoot);
        var port = ReserveTcpPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}", UriKind.Absolute);
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add("src/ArtifactStore/SharpLabNext.ArtifactStore/SharpLabNext.ArtifactStore.csproj");
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add("Release");
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--no-launch-profile");
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add(baseAddress.AbsoluteUri);
        startInfo.Environment["ArtifactStore__RootPath"] = storeRoot;
        startInfo.Environment["ArtifactStore__DefaultTimeToLive"] = "00:05:00";
        startInfo.Environment["ArtifactStore__MaximumTimeToLive"] = "01:00:00";
        startInfo.Environment["ArtifactStore__MaximumLeaseDuration"] = "00:05:00";
        startInfo.Environment["ArtifactStore__CleanupInterval"] = "01:00:00";
        if (internalServiceToken is not null)
        {
            var tokenPath = Path.Combine(storeRoot, "internal-service-token");
            await File.WriteAllTextAsync(
                tokenPath,
                internalServiceToken + Environment.NewLine,
                cancellationToken);
            startInfo.Environment["InternalServiceAuth__Required"] = "true";
            startInfo.Environment["InternalServiceAuth__TokenFile"] = tokenPath;
        }
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the Artifact Store test process.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        var httpClient = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(10) };
        if (internalServiceToken is not null)
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", internalServiceToken);
        var result = new ArtifactStoreProcess(process, output, error, storeRoot, deleteRootOnDispose, httpClient);

        try
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"Artifact Store exited during startup.{Environment.NewLine}stdout:{Environment.NewLine}{await output}{Environment.NewLine}stderr:{Environment.NewLine}{await error}");
                }

                try
                {
                    using var response = await httpClient.GetAsync("/health/ready", cancellationToken);
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        return result;
                    }
                }
                catch (HttpRequestException)
                {
                }

                await Task.Delay(50, cancellationToken);
            }

            throw new TimeoutException("Artifact Store did not become ready in time.");
        }
        catch
        {
            await result.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        HttpClient.Dispose();
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }

        try
        {
            await _process.WaitForExitAsync();
            _ = await _standardOutput;
            _ = await _standardError;
        }
        finally
        {
            _process.Dispose();
            if (_deleteRootOnDispose)
            {
                DeleteTestRoot();
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SharpLabNext.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the SharpLabNext repository root.");
    }

    private static int ReserveTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private void DeleteTestRoot()
    {
        for (var attempt = 0; attempt < 5 && Directory.Exists(RootPath); attempt++)
        {
            try
            {
                Directory.Delete(RootPath, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(25);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                Thread.Sleep(25);
            }
        }
    }
}

internal static class ArtifactStoreTestData
{
    public static ArtifactManifest CreateManifest(params (string Path, byte[] Content, string Role)[] files)
    {
        if (files.Length == 0)
        {
            files = [("app.dll", Encoding.UTF8.GetBytes("test assembly"), "primary-assembly")];
        }

        var placeholder = new ArtifactRef($"sha256:{new string('0', ArtifactStoreProtocol.Sha256HexLength)}");
        var manifest = new ArtifactManifest(
            1,
            placeholder,
            new ArtifactProducer(
                "test-release",
                "csharp",
                "roslyn-stable",
                "5.6.0",
                null,
                $"sha256:{new string('1', ArtifactStoreProtocol.Sha256HexLength)}"),
            "net10-ref",
            "net10.0",
            "dotnet-managed-pe-v1",
            new ArtifactRuntimeRequirement(
                "coreclr",
                [new FrameworkRequirement("Microsoft.NETCore.App", "10.0.9")],
                "anycpu",
                []),
            [],
            BuildOutputKind.Console,
            files[0].Path,
            "Program.Main",
            files.Select(file => new ArtifactFileDescriptor(
                file.Role,
                file.Path,
                file.Content.LongLength,
                ContentIdentity.Compute(file.Content).Value)).ToArray());
        return ArtifactIdentity.WithComputedId(manifest);
    }
}
