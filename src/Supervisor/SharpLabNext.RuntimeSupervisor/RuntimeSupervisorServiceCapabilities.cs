namespace SharpLabNext.RuntimeSupervisor;

internal static class RuntimeSupervisorServiceCapabilities
{
    public static readonly IReadOnlyList<string> All =
    [
        "health",
        "run",
        "jit",
        "operation-stream",
        "docker-one-shot",
        "docker-session-reuse",
        "runtime-capability-preflight",
        "runtime-performance-preflight"
    ];
}
