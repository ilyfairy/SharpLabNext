namespace SharpLabNext.RuntimeProfile.Sdk;

/// <summary>
/// Immutable contract for the trusted performance-measurement sidecar script.
/// The content digest is deliberately pinned separately from the image digest so
/// a correctly labelled image cannot substitute a different helper executable.
/// </summary>
public static class RuntimeMeasurementHelperContract
{
    public const string Implementation = "sharplabnext-runtime-cgroup-sidecar-v1";

    public const string Entrypoint = "/usr/local/bin/sharplabnext-runtime-measurement";

    public const string ContentSha256 =
        "sha256:f7645af4191d024c86769f3e39fd76ad237f537572c752fdfec3ff529aea9e4c";
}
