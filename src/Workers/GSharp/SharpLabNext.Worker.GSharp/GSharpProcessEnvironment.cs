using System.Diagnostics;

namespace SharpLabNext.Worker.GSharp;

internal static class GSharpProcessEnvironment
{
    public static ProcessStartInfo Create(GSharpWorkerSettings settings, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo { FileName = settings.DotNetHostPath, WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        var inheritedEnvironment = startInfo.Environment.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        startInfo.Environment.Clear();
        Copy("PATH");
        Copy("SystemRoot");
        Copy("WINDIR");
        Copy("DOTNET_ROOT");
        Copy("DOTNET_ROOT_X64");
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_EnableDiagnostics"] = "0";
        startInfo.Environment["COMPlus_EnableDiagnostics"] = "0";
        startInfo.Environment["LANG"] = "C.UTF-8";
        startInfo.Environment["LC_ALL"] = "C.UTF-8";
        startInfo.Environment["HOME"] = workingDirectory;
        startInfo.Environment["TMP"] = workingDirectory;
        startInfo.Environment["TEMP"] = workingDirectory;
        startInfo.Environment["TMPDIR"] = workingDirectory;
        return startInfo;

        void Copy(string name)
        {
            if (inheritedEnvironment.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value))
                startInfo.Environment[name] = value;
        }
    }

    public static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
    }

    public static string PublicText(string value, int maximumCharacters = 1024)
    {
        var compact = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return compact.Length <= maximumCharacters ? compact : compact[..maximumCharacters];
    }
}
