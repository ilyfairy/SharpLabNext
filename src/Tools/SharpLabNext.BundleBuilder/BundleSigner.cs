using System.Diagnostics;

namespace SharpLabNext.BundleBuilder;

public interface IBundleSigner
{
    Task SignAndVerifyAsync(string contentPath, string signaturePath, string privateKeyPath, string publicKeyPath, CancellationToken cancellationToken);
}

public sealed class OpenSslBundleSigner(string command) : IBundleSigner
{
    public async Task SignAndVerifyAsync(string contentPath, string signaturePath, string privateKeyPath, string publicKeyPath, CancellationToken cancellationToken)
    {
        EnsureFile(contentPath);
        EnsureFile(privateKeyPath);
        EnsureFile(publicKeyPath);
        var privateDescription = await RunAsync(["pkey", "-in", privateKeyPath, "-text", "-noout"], cancellationToken);
        var publicDescription = await RunAsync(["pkey", "-pubin", "-in", publicKeyPath, "-text", "-noout"], cancellationToken);
        if (!privateDescription.Contains("ED25519", StringComparison.OrdinalIgnoreCase) || !publicDescription.Contains("ED25519", StringComparison.OrdinalIgnoreCase))
        {
            throw new BundleValidationException("Bundle signing keys must use Ed25519.");
        }

        _ = await RunAsync(["pkeyutl", "-sign", "-rawin", "-inkey", privateKeyPath, "-in", contentPath, "-out", signaturePath], cancellationToken);
        _ = await RunAsync(["pkeyutl", "-verify", "-rawin", "-pubin", "-inkey", publicKeyPath, "-in", contentPath, "-sigfile", signaturePath], cancellationToken);
    }

    private async Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo { FileName = command, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ?? throw new BundleValidationException($"Could not start '{command}'.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new BundleValidationException($"OpenSSL failed with exit code {process.ExitCode}: {Limit(error.Trim())}");
        }
        return string.Concat(output, Environment.NewLine, error);
    }

    private static string Limit(string value) => value.Length <= 4096 ? value : value[..4096];

    private static void EnsureFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new BundleValidationException($"Signing input '{path}' does not exist.");
        }
    }
}
