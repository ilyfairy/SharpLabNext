#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0

using System.Diagnostics;
using System.Text.RegularExpressions;

var startInfo = new ProcessStartInfo("docker")
{
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false
};
startInfo.ArgumentList.Add("buildx");
startInfo.ArgumentList.Add("inspect");
startInfo.ArgumentList.Add("--bootstrap");

Process? process;
try
{
    process = Process.Start(startInfo);
}
catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
{
    Console.Error.WriteLine($"Could not start Docker Buildx: {exception.Message}");
    return 1;
}

if (process is null)
{
    Console.Error.WriteLine("Could not start Docker Buildx.");
    return 1;
}

using (process)
{
    var standardOutput = process.StandardOutput.ReadToEndAsync();
    var standardError = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var output = $"{await standardOutput}\n{await standardError}";

    if (process.ExitCode != 0)
    {
        Console.Error.WriteLine(output.Trim());
        Console.Error.WriteLine("Docker Buildx is unavailable or its active builder could not be bootstrapped.");
        return process.ExitCode;
    }

    var matches = Regex.Matches(
        output,
        @"BuildKit version:\s*v?(?<major>\d+)\.(?<minor>\d+)(?:\.(?<patch>\d+))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    if (matches.Count == 0)
    {
        Console.Error.WriteLine("Could not determine the active builder's BuildKit version from 'docker buildx inspect --bootstrap'.");
        return 1;
    }

    var minimum = new Version(0, 13, 0);
    var detected = matches
        .Select(static match => new Version(
            int.Parse(match.Groups["major"].Value, System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(match.Groups["minor"].Value, System.Globalization.CultureInfo.InvariantCulture),
            match.Groups["patch"].Success
                ? int.Parse(match.Groups["patch"].Value, System.Globalization.CultureInfo.InvariantCulture)
                : 0))
        .ToArray();
    var unsupported = detected.Where(version => version < minimum).ToArray();
    if (unsupported.Length > 0)
    {
        Console.Error.WriteLine(
            $"BuildKit {minimum} or newer is required for deterministic timestamp rewriting. " +
            $"Detected: {string.Join(", ", detected.Select(static version => version.ToString()))}.");
        return 1;
    }

    Console.WriteLine(
        $"BuildKit capability check passed (minimum {minimum}; detected {string.Join(", ", detected.Select(static version => version.ToString()))}).");
}

return 0;
