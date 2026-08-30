using System.Diagnostics;

namespace SharpLabNext.Worker.CppCli;

internal static class CppCliCompilerCommand
{
    public static ProcessStartInfo Create(CppCliWorkerSettings settings, string workingDirectory, string sourcePath, string objectPath, string outputPath, bool optimize)
    {
        RequireRelative(sourcePath, nameof(sourcePath));
        RequireRelative(objectPath, nameof(objectPath));
        RequireRelative(outputPath, nameof(outputPath));
        var startInfo = new ProcessStartInfo { FileName = settings.CompilerPath, WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        var inherited = startInfo.Environment.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        startInfo.Environment.Clear();
        Copy("PATH");
        Copy("HOME");
        Copy("WINEPREFIX");
        Copy("DISPLAY");
        Copy("XAUTHORITY");
        Copy("XDG_RUNTIME_DIR");
        Copy("WINEDLLOVERRIDES");
        startInfo.Environment["WINEDEBUG"] = "-all";
        startInfo.Environment["LANG"] = "C.UTF-8";
        startInfo.Environment["LC_ALL"] = "C.UTF-8";

        startInfo.ArgumentList.Add("/nologo");
        startInfo.ArgumentList.Add("/EHa");
        startInfo.ArgumentList.Add("/clr");
        startInfo.ArgumentList.Add("/MD");
        startInfo.ArgumentList.Add(optimize ? "/O2" : "/Od");
        startInfo.ArgumentList.Add("/utf-8");
        startInfo.ArgumentList.Add("/diagnostics:column");
        startInfo.ArgumentList.Add("/experimental:deterministic");
        startInfo.ArgumentList.Add(sourcePath);
        startInfo.ArgumentList.Add($"/Fo{objectPath}");
        startInfo.ArgumentList.Add($"/Fe{outputPath}");
        startInfo.ArgumentList.Add("/link");
        startInfo.ArgumentList.Add("/Brepro");
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
            throw new ArgumentException("MSVC source and output paths must be safe relative paths.", parameterName);
        }
    }
}
