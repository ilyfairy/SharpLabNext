using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace SharpLabNext.CheckedJitBridge;

internal static class CheckedJitChildProcess
{
    public static ProcessStartInfo CreateStartInfo(
        CheckedJitBridgeArguments options,
        string pipeHandle,
        string nonce,
        string userAssemblyName,
        string? runtimeHostPath = null,
        string? bridgeAssemblyPath = null)
    {
        if (options is null)
            throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(pipeHandle) ||
            pipeHandle.Length > 128 ||
            pipeHandle.Any(char.IsControl))
        {
            throw new ArgumentException("The Checked JIT child pipe handle is invalid.", nameof(pipeHandle));
        }
        if (!BridgePathValidation.IsLowerHexNonce(nonce))
            throw new ArgumentException("The Checked JIT child nonce is invalid.", nameof(nonce));
        ValidateAssemblyName(userAssemblyName);

        runtimeHostPath = BridgePathValidation.ValidateRuntimeHostPath(
            runtimeHostPath ?? GetCurrentRuntimeHostPath());
        bridgeAssemblyPath = BridgePathValidation.ValidateBridgeAssemblyPath(
            bridgeAssemblyPath ?? typeof(CheckedJitChildProcess).Assembly.Location);

        var startInfo = new ProcessStartInfo(runtimeHostPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(bridgeAssemblyPath);
        startInfo.ArgumentList.Add("--child");
        startInfo.ArgumentList.Add(pipeHandle);
        startInfo.ArgumentList.Add(options.AssemblyPath);
        startInfo.ArgumentList.Add(nonce);
        startInfo.ArgumentList.Add(options.MethodFilter ?? string.Empty);

        RemoveJitOutputFileEnvironment(startInfo);
        SetJitEnvironment(startInfo, userAssemblyName);
        startInfo.Environment["DOTNET_EnableDiagnostics"] = "0";
        startInfo.Environment["COMPlus_EnableDiagnostics"] = "0";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        return startInfo;
    }

    private static void SetJitEnvironment(ProcessStartInfo startInfo, string userAssemblyName)
    {
        startInfo.Environment["DOTNET_JitDisasm"] = "*";
        startInfo.Environment["COMPlus_JitDisasm"] = "*";
        startInfo.Environment["DOTNET_JitDisasmAssemblies"] = userAssemblyName;
        startInfo.Environment["COMPlus_JitDisasmAssemblies"] = userAssemblyName;
        startInfo.Environment["DOTNET_JitDisasmWithDebugInfo"] = "1";
        startInfo.Environment["COMPlus_JitDisasmWithDebugInfo"] = "1";
        startInfo.Environment["DOTNET_JitDisasmWithCodeBytes"] = "1";
        startInfo.Environment["COMPlus_JitDisasmWithCodeBytes"] = "1";
    }

    private static void RemoveJitOutputFileEnvironment(ProcessStartInfo startInfo)
    {
        startInfo.Environment.Remove("DOTNET_JitStdOutFile");
        startInfo.Environment.Remove("COMPlus_JitStdOutFile");
        startInfo.Environment.Remove("SHARPLABNEXT_JIT_OUTPUT_PATH");
    }

    private static void ValidateAssemblyName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
            throw new InvalidDataException("The user assembly name is invalid for Checked JIT filtering.");
        foreach (var character in value)
        {
            if (!(char.IsLetterOrDigit(character) || character is '.' or '_' or '-' or '+'))
            {
                throw new InvalidDataException(
                    "The user assembly name contains a character unsupported by Checked JIT filtering.");
            }
        }
    }

    private static string GetCurrentRuntimeHostPath()
    {
        using var current = Process.GetCurrentProcess();
        return current.MainModule?.FileName
            ?? throw new InvalidOperationException("The current target runtime host path is unavailable.");
    }
}
