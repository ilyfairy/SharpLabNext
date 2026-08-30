using System.Diagnostics;

namespace SharpLabNext.Worker.JSharp;

internal static class JSharpCompilerCommand
{
    public static ProcessStartInfo Create(JSharpWorkerSettings settings, string workingDirectory, string sourcePath, string outputPath, bool optimize)
    {
        RequireRelative(sourcePath, nameof(sourcePath));
        RequireRelative(outputPath, nameof(outputPath));
        var startInfo = new ProcessStartInfo { FileName = settings.CompilerHostPath, WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        var inherited = startInfo.Environment.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        startInfo.Environment.Clear();
        Copy("PATH");
        Copy("HOME");
        Copy("DISPLAY");
        Copy("XAUTHORITY");
        Copy("XDG_RUNTIME_DIR");
        Copy("WINEDLLOVERRIDES");
        startInfo.Environment["WINEPREFIX"] = JSharpToolchain.WinePrefixPath;
        startInfo.Environment["WINEARCH"] = JSharpToolchain.WineArchitecture;
        startInfo.Environment["WINEDEBUG"] = "-all";
        startInfo.Environment["LANG"] = "C.UTF-8";
        startInfo.Environment["LC_ALL"] = "C.UTF-8";

        startInfo.ArgumentList.Add(settings.CompilerPath);
        startInfo.ArgumentList.Add("/nologo");
        startInfo.ArgumentList.Add("/target:exe");
        startInfo.ArgumentList.Add("/platform:x64");
        startInfo.ArgumentList.Add("/utf8output");
        startInfo.ArgumentList.Add("/warn:4");
        startInfo.ArgumentList.Add(optimize ? "/optimize+" : "/optimize-");
        startInfo.ArgumentList.Add($"/out:{outputPath}");
        startInfo.ArgumentList.Add(sourcePath);
        return startInfo;

        void Copy(string name)
        {
            if (inherited.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
                startInfo.Environment[name] = value;
        }
    }

    private static void RequireRelative(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':') || path.Replace('\\', '/').Split('/').Any(static segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException("J# source and output paths must be safe relative paths.", parameterName);
        }
    }
}
