using SharpLabNext.Contracts;

namespace SharpLabNext.Contracts.Grpc;

public static class WorkerProtocol
{
    public static ProtocolVersion Current { get; } = ProtocolVersion.WorkerV1;

    public static bool IsCompatible(ProtocolVersion offered) => offered.Major == Current.Major;

    public static ProtocolVersion? Negotiate(IEnumerable<ProtocolVersion> callerVersions, IEnumerable<ProtocolVersion> workerVersions)
    {
        ArgumentNullException.ThrowIfNull(callerVersions);
        ArgumentNullException.ThrowIfNull(workerVersions);

        var callerByMajor = callerVersions.GroupBy(v => v.Major).ToDictionary(g => g.Key, g => g.Max(v => v.Minor));
        var workerByMajor = workerVersions.GroupBy(v => v.Major).ToDictionary(g => g.Key, g => g.Max(v => v.Minor));

        var commonMajor = callerByMajor.Keys.Intersect(workerByMajor.Keys).DefaultIfEmpty(-1).Max();

        return commonMajor < 0
            ? null : new ProtocolVersion(commonMajor, Math.Min(callerByMajor[commonMajor], workerByMajor[commonMajor]));
    }
}
