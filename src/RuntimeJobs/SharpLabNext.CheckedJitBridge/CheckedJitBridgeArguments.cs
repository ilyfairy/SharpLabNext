using System;
using System.IO;
using System.Linq;

namespace SharpLabNext.CheckedJitBridge;

internal sealed record CheckedJitBridgeArguments(string AssemblyPath, string? MethodFilter)
{
    private const string Usage = "Usage: SharpLabNext.CheckedJitBridge jit <absolute-entry-assembly> [<method-filter>]";

    public static CheckedJitBridgeArguments Parse(string[] args)
    {
        if (args is null)
            throw new ArgumentNullException(nameof(args));
        if (args.Length is < 2 or > 3 || !string.Equals(args[0], "jit", StringComparison.Ordinal))
            throw new ArgumentException(Usage, nameof(args));

        var assemblyPath = BridgePathValidation.ValidateAssemblyPath(args[1]);
        var filter = args.Length == 3 ? ValidateMethodFilter(args[2]) : null;
        return new CheckedJitBridgeArguments(assemblyPath, filter);
    }

    internal static string? ValidateMethodFilter(string value)
    {
        if (value is null)
            throw new ArgumentNullException(nameof(value));
        if (value.Length > 256)
            throw new ArgumentException("Method filter exceeds 256 characters.", nameof(value));
        if (value.Any(char.IsControl))
            throw new ArgumentException("Method filter contains a control character.", nameof(value));
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

internal sealed record CheckedJitChildArguments(string PipeHandle, string AssemblyPath, string Nonce, string? MethodFilter)
{
    public static CheckedJitChildArguments Parse(string[] args)
    {
        if (args is null)
            throw new ArgumentNullException(nameof(args));
        if (args.Length != 5 || !string.Equals(args[0], "--child", StringComparison.Ordinal))
            throw new ArgumentException("The Checked JIT child arguments are invalid.", nameof(args));
        if (string.IsNullOrWhiteSpace(args[1]) || args[1].Length > 128 || args[1].Any(char.IsControl))
            throw new ArgumentException("The Checked JIT child pipe handle is invalid.", nameof(args));
        if (!BridgePathValidation.IsLowerHexNonce(args[3]))
            throw new ArgumentException("The Checked JIT child nonce is invalid.", nameof(args));

        return new CheckedJitChildArguments(args[1], BridgePathValidation.ValidateAssemblyPath(args[2]), args[3], CheckedJitBridgeArguments.ValidateMethodFilter(args[4]));
    }
}

internal static class BridgePathValidation
{
    private const int MaximumPathLength = 4_096;
    private static readonly char[] PathSeparators = { '/', '\\' };

    public static string ValidateAssemblyPath(string value)
    {
        var path = ValidateCanonicalFilePath(value, "User entry assembly");
        if (!string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("User entry assembly must be a managed .dll file.", nameof(value));
        return path;
    }

    public static string ValidateRuntimeHostPath(string value)
    {
        var path = ValidateCanonicalFilePath(value, "Target runtime host");
        var hostName = Path.GetFileNameWithoutExtension(path);
        if (!string.Equals(hostName, "dotnet", StringComparison.OrdinalIgnoreCase) && !string.Equals(hostName, "corerun", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Target runtime host must be dotnet or corerun.", nameof(value));
        }
        return path;
    }

    public static string ValidateBridgeAssemblyPath(string value)
    {
        var path = ValidateCanonicalFilePath(value, "Checked JIT bridge");
        if (!string.Equals(Path.GetFileName(path), "SharpLabNext.CheckedJitBridge.dll", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The Checked JIT bridge assembly name is invalid.", nameof(value));
        }
        return path;
    }

    public static bool IsLowerHexNonce(string value)
    {
        if (value is null || value.Length != 32)
            return false;
        foreach (var character in value)
        {
            if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')))
                return false;
        }
        return true;
    }

    private static string ValidateCanonicalFilePath(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumPathLength || value.Any(char.IsControl) || !Path.IsPathFullyQualified(value) || ContainsTraversalSegment(value))
        {
            throw new ArgumentException($"{description} path must be absolute and canonical.", nameof(value));
        }

        var fullPath = Path.GetFullPath(value);
        var comparison = Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(fullPath, value, comparison))
            throw new ArgumentException($"{description} path must be absolute and canonical.", nameof(value));
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"{description} was not found.", fullPath);
        return fullPath;
    }

    private static bool ContainsTraversalSegment(string path) =>
        path.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries).Any(segment => segment == "." || segment == "..");
}
