namespace SharpLabNext.RuntimeProfile.Sdk;

public static class RuntimeProfileCommandBuilder
{
    private const int MaximumDynamicArgumentLength = 32 * 1024;

    public static IReadOnlyList<string> CreateRunCommand(RuntimeProfileDefinition profile, string normalizedEntryAssembly, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(arguments);
        if (profile.Operations?.Run is { } operation)
        {
            return CreateOperationCommand(operation, RuntimeProfileValidation.Validate(operation), normalizedEntryAssembly, arguments, methodFilter: null);
        }

        List<string> command = profile.Layout.RunnerKind switch
        {
            RuntimeRunnerKinds.DotNet =>
            [
                profile.Layout.DotNetHostPath,
                profile.Layout.RunnerAssemblyPath,
                WorkspaceFile(normalizedEntryAssembly),
                "--"
            ],
            RuntimeRunnerKinds.WineNetFx or RuntimeRunnerKinds.WineJSharp20 =>
            [
                profile.Layout.DotNetHostPath,
                profile.Layout.RunnerAssemblyPath,
                "bridge",
                profile.Layout.WineHostPath,
                WorkspaceFile(normalizedEntryAssembly),
                "--"
            ],
            RuntimeRunnerKinds.WineCoreClr =>
            [
                profile.Layout.WineHostPath,
                profile.Layout.DotNetHostPath,
                WorkspaceFileWine(normalizedEntryAssembly),
                "--"
            ],
            _ => throw new InvalidOperationException($"Runtime runner kind '{profile.Layout.RunnerKind}' is not supported.")
        };
        command.AddRange(arguments);
        return command;
    }

    public static IReadOnlyList<string> CreateJitCommand(RuntimeProfileDefinition profile, string normalizedEntryAssembly, string? methodFilter)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Operations?.Jit is { } operation)
        {
            return CreateOperationCommand(operation, RuntimeProfileValidation.Validate(operation), normalizedEntryAssembly, arguments: [], methodFilter);
        }

        if (StringComparer.Ordinal.Equals(profile.Layout.RunnerKind, RuntimeRunnerKinds.WineCoreClr))
        {
            throw new NotSupportedException("Legacy wine-coreclr layouts do not support JIT inspection; declare an operation-based JIT command.");
        }
        if (!StringComparer.Ordinal.Equals(profile.Layout.RunnerKind, RuntimeRunnerKinds.DotNet))
        {
            throw new NotSupportedException($"Runtime runner kind '{profile.Layout.RunnerKind}' does not support JIT inspection.");
        }
        var jitInspectorAssemblyPath = profile.Layout.JitInspectorAssemblyPath;
        if (string.IsNullOrWhiteSpace(jitInspectorAssemblyPath))
            throw new InvalidOperationException("The runtime profile does not declare a JIT inspector assembly.");
        var command = new List<string> { profile.Layout.DotNetHostPath, jitInspectorAssemblyPath, WorkspaceFile(normalizedEntryAssembly) };
        if (!string.IsNullOrWhiteSpace(methodFilter))
            command.Add(methodFilter);
        return command;
    }

    private static List<string> CreateOperationCommand(RuntimeOperationDefinition operation, IReadOnlyList<string> validationFailures, string normalizedEntryAssembly, IReadOnlyList<string> arguments, string? methodFilter)
    {
        if (validationFailures.Count > 0)
        {
            throw new InvalidOperationException($"The runtime operation command is invalid: {string.Join(" ", validationFailures)}");
        }

        var entryAssembly = WorkspaceFile(normalizedEntryAssembly, operation.PathStyle);
        ValidateDynamicValue(methodFilter, nameof(methodFilter));
        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index] is null)
                throw new ArgumentException("Runtime arguments cannot contain null values.", nameof(arguments));
            ValidateDynamicValue(arguments[index], $"arguments[{index}]");
        }

        var command = new List<string>(operation.Command.Argv.Count + arguments.Count + 1)
        {
            operation.Command.Executable
        };
        foreach (var token in operation.Command.Argv)
        {
            switch (token)
            {
                case RuntimeOperationPlaceholders.EntryAssembly:
                    command.Add(entryAssembly);
                    break;
                case RuntimeOperationPlaceholders.Arguments:
                    command.AddRange(arguments);
                    break;
                case RuntimeOperationPlaceholders.MethodFilter when !string.IsNullOrWhiteSpace(methodFilter):
                    command.Add(methodFilter);
                    break;
                case RuntimeOperationPlaceholders.MethodFilter:
                    break;
                default:
                    command.Add(token);
                    break;
            }
        }
        return command;
    }

    private static string WorkspaceFile(string normalizedArtifactPath, string pathStyle)
    {
        var unixPath = WorkspaceFile(normalizedArtifactPath);
        return pathStyle switch
        {
            RuntimeOperationPathStyles.Unix => unixPath,
            RuntimeOperationPathStyles.WineZ => $"Z:{unixPath.Replace('/', '\\')}",
            _ => throw new InvalidOperationException($"Runtime operation path style '{pathStyle}' is not supported.")
        };
    }

    private static string WorkspaceFileWine(string normalizedArtifactPath) =>
        $"Z:{WorkspaceFile(normalizedArtifactPath).Replace('/', '\\')}";

    private static void ValidateDynamicValue(string? value, string parameterName)
    {
        if (value is null)
            return;
        if (value.Length > MaximumDynamicArgumentLength || value.Contains('\0'))
        {
            throw new ArgumentException("Runtime arguments cannot exceed 32768 characters or contain NUL characters.", parameterName);
        }
    }

    public static string WorkspaceFile(string normalizedArtifactPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedArtifactPath);
        if (normalizedArtifactPath.Length > 4096 || normalizedArtifactPath.Contains('\0') || normalizedArtifactPath.StartsWith('/') || normalizedArtifactPath.Contains('\\') || normalizedArtifactPath.Split('/').Any(static segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException("The artifact path must be normalized and relative.", nameof(normalizedArtifactPath));
        }
        return $"{RuntimeImageLayout.WorkspacePath}/{normalizedArtifactPath}";
    }
}
