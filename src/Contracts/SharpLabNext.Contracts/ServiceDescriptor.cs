namespace SharpLabNext.Contracts;

public enum ServiceKind
{
    Gateway,
    ArtifactStore,
    RuntimeSupervisor,
    ToolchainWorker,
    ArtifactWorker
}

public sealed record ProtocolVersion : IComparable<ProtocolVersion>
{
    public ProtocolVersion(int major, int minor)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        Major = major;
        Minor = minor;
    }

    public int Major { get; }

    public int Minor { get; }

    public static ProtocolVersion WorkerV1 { get; } = new(1, 0);

    public static ProtocolVersion RuntimeChildV1 { get; } = new(1, 0);

    public int CompareTo(ProtocolVersion? other)
    {
        if (other is null)
            return 1;

        var majorComparison = Major.CompareTo(other.Major);
        return majorComparison != 0 ? majorComparison : Minor.CompareTo(other.Minor);
    }

    public static bool operator <(ProtocolVersion left, ProtocolVersion right) => left.CompareTo(right) < 0;

    public static bool operator <=(ProtocolVersion left, ProtocolVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >(ProtocolVersion left, ProtocolVersion right) => left.CompareTo(right) > 0;

    public static bool operator >=(ProtocolVersion left, ProtocolVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() => $"{Major}.{Minor}";
}

public sealed record ServiceIdentity(
    string Id,
    ServiceKind Kind,
    string ReleaseId,
    ProtocolVersion Protocol,
    IReadOnlyList<string> Capabilities,
    string Status);

public static class ContractSchemaVersions
{
    public const int ArtifactManifest = 1;
    public const int Catalog = 1;
    public const int WorkspaceSnapshot = 1;
    public const int Url = 3;
    public const string Lsp = "3.17";
}

public static class ContractConventions
{
    public const string TextCoordinateEncoding = "utf-16";
}
