using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SharpLabNext.RuntimeSupervisor;

internal sealed record RuntimeSessionRequest(
    string SessionId,
    string ReleaseId,
    string Image,
    IReadOnlyList<string> Command,
    IReadOnlyDictionary<string, string> Environment,
    RuntimeSecurityPolicyOptions SecurityPolicy,
    RuntimeContainerIsolationKind IsolationKind,
    string? WinePrefixPath,
    string ManagementLabel,
    string ResourceScope);

internal sealed class RuntimeSessionLease : IAsyncDisposable
{
    private readonly RuntimeSessionRegistry _owner;
    private int _completed;

    internal RuntimeSessionLease(
        RuntimeSessionRegistry owner,
        RuntimeSessionSlot slot,
        RuntimeSessionResource resource,
        bool reused)
    {
        _owner = owner;
        Slot = slot;
        Resource = resource;
        Reused = reused;
    }

    internal RuntimeSessionSlot Slot { get; }

    internal RuntimeSessionResource Resource { get; }

    public string ContainerId => Resource.ContainerId;

    public bool Reused { get; }

    public ValueTask CompleteAsync(bool reusable) =>
        Interlocked.Exchange(ref _completed, 1) == 0
            ? _owner.CompleteAsync(this, reusable)
            : ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => CompleteAsync(reusable: false);
}

internal sealed class RuntimeSessionAdmissionLease(RuntimeSessionSlot slot) : IAsyncDisposable
{
    private RuntimeSessionSlot? _slot = slot;

    public ValueTask DisposeAsync()
    {
        var acquiredSlot = Interlocked.Exchange(ref _slot, null);
        acquiredSlot?.Gate.Release();
        return ValueTask.CompletedTask;
    }
}

internal sealed class RuntimeSessionSlot
{
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public RuntimeSessionResource? Resource { get; set; }

    public int Closing;
}

internal sealed record RuntimeSessionResource(
    string Fingerprint,
    string ContainerId,
    string MaterializerContainerId,
    string WorkspaceVolumeName,
    DateTimeOffset CreatedAtUtc);

public sealed partial class RuntimeSessionRegistry(
    IDockerEngineClient docker,
    IOptions<RuntimeSupervisorOptions> configuredOptions,
    RuntimeSandboxPolicy sandbox,
    ILogger<RuntimeSessionRegistry> logger)
{
    private readonly ConcurrentDictionary<string, RuntimeSessionSlot> _slots =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _releasedSessions =
        new(StringComparer.Ordinal);
    private readonly RuntimeSupervisorOptions _options = configuredOptions.Value;

    public bool Enabled => _options.SessionReuseEnabled;

    internal async Task<RuntimeSessionLease> AcquireAsync(
        RuntimeSessionRequest request,
        Stream archive,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(archive);
        if (!Enabled)
            throw new InvalidOperationException("Runtime session reuse is disabled.");

        var slot = await AcquireOpenSlotAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
        try
        {
            var fingerprint = CreateFingerprint(request);
            var maximumAge = TimeSpan.FromSeconds(_options.SessionMaximumAgeSeconds);
            var resource = slot.Resource;
            var reused = resource is not null &&
                string.Equals(resource.Fingerprint, fingerprint, StringComparison.Ordinal) &&
                DateTimeOffset.UtcNow - resource.CreatedAtUtc < maximumAge;
            if (!reused)
            {
                slot.Resource = null;
                await RemoveResourceQuietlyAsync(resource).ConfigureAwait(false);
                resource = await CreateResourceAsync(request, fingerprint, archive, cancellationToken)
                    .ConfigureAwait(false);
                slot.Resource = resource;
            }
            else
            {
                try
                {
                    ResetArchive(archive);
                    await docker.StartContainerAsync(
                        resource!.MaterializerContainerId,
                        cancellationToken).ConfigureAwait(false);
                    await docker.UploadArchiveAsync(
                        resource.MaterializerContainerId,
                        archive,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    LogStaleSessionResource(logger, request.SessionId, exception);
                    slot.Resource = null;
                    await RemoveResourceQuietlyAsync(resource).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    resource = await CreateResourceAsync(request, fingerprint, archive, cancellationToken)
                        .ConfigureAwait(false);
                    slot.Resource = resource;
                    reused = false;
                }
            }

            if (Volatile.Read(ref slot.Closing) != 0 || IsReleased(request.SessionId))
            {
                slot.Resource = null;
                await RemoveResourceQuietlyAsync(resource).ConfigureAwait(false);
                throw new RuntimeSessionClosingException();
            }

            return new RuntimeSessionLease(this, slot, resource!, reused);
        }
        catch
        {
            slot.Gate.Release();
            throw;
        }
    }

    internal async Task<RuntimeSessionAdmissionLease> AcquireOneShotAdmissionAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var slot = await AcquireOpenSlotAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return new RuntimeSessionAdmissionLease(slot);
    }

    public async Task ReleaseAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId);
        _ = cancellationToken;
        MarkReleased(sessionId);
        var slot = _slots.GetOrAdd(sessionId, static _ => new RuntimeSessionSlot());

        if (Interlocked.Exchange(ref slot.Closing, 1) == 0 && slot.Resource is { } active)
        {
            try
            {
                using var killTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await docker.RemoveContainerAsync(active.ContainerId, killTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                LogSessionKillFailed(logger, sessionId, exception);
            }
        }

        using var gateTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await slot.Gate.WaitAsync(gateTimeout.Token).ConfigureAwait(false);
        try
        {
            var resource = slot.Resource;
            slot.Resource = null;
            await RemoveResourceQuietlyAsync(resource).ConfigureAwait(false);
            _slots.TryRemove(new KeyValuePair<string, RuntimeSessionSlot>(sessionId, slot));
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    internal async ValueTask CompleteAsync(RuntimeSessionLease lease, bool reusable)
    {
        var slot = lease.Slot;
        try
        {
            var isCurrent = ReferenceEquals(slot.Resource, lease.Resource);
            var mayReuse = reusable &&
                isCurrent &&
                Volatile.Read(ref slot.Closing) == 0;
            if (mayReuse)
            {
                mayReuse = await CleanWorkspaceAsync(lease.Resource).ConfigureAwait(false);
            }

            if (!mayReuse && isCurrent)
            {
                slot.Resource = null;
                await RemoveResourceQuietlyAsync(lease.Resource).ConfigureAwait(false);
            }
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    private async Task<RuntimeSessionResource> CreateResourceAsync(
        RuntimeSessionRequest request,
        string fingerprint,
        Stream archive,
        CancellationToken cancellationToken)
    {
        ResetArchive(archive);
        var materialization = await docker.MaterializeWorkspaceAsync(
            request.SessionId,
            request.ReleaseId,
            request.Image,
            archive,
            request.SecurityPolicy,
            request.IsolationKind,
            request.ManagementLabel,
            request.ResourceScope,
            createMeasurementControl: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        string? containerId = null;
        try
        {
            var spec = new RuntimeContainerSpec(
                $"sln-session-{Guid.NewGuid():N}",
                request.SessionId,
                request.ReleaseId,
                request.Image,
                request.Command,
                request.Environment,
                request.SecurityPolicy,
                request.ManagementLabel,
                request.ResourceScope,
                materialization.VolumeName,
                TraceParent: null,
                IsolationKind: request.IsolationKind,
                WinePrefixPath: request.WinePrefixPath);
            containerId = await docker.CreateContainerAsync(spec, cancellationToken).ConfigureAwait(false);
            return new RuntimeSessionResource(
                fingerprint,
                containerId,
                materialization.MaterializerContainerId,
                materialization.VolumeName,
                DateTimeOffset.UtcNow);
        }
        catch
        {
            await RemoveResourceQuietlyAsync(new RuntimeSessionResource(
                fingerprint,
                containerId ?? string.Empty,
                materialization.MaterializerContainerId,
                materialization.VolumeName,
                DateTimeOffset.UtcNow)).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<bool> CleanWorkspaceAsync(RuntimeSessionResource resource)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await docker.StopContainerAsync(
                resource.MaterializerContainerId,
                TimeSpan.FromSeconds(2),
                timeout.Token).ConfigureAwait(false);
            var exit = await docker.WaitContainerAsync(resource.MaterializerContainerId, timeout.Token)
                .ConfigureAwait(false);
            if (exit.StatusCode == 0 && !exit.OomKilled && string.IsNullOrWhiteSpace(exit.Error))
                return true;

            LogWorkspaceCleanupFailed(
                logger,
                resource.MaterializerContainerId,
                exit.StatusCode,
                exit.Error ?? string.Empty);
        }
        catch (Exception exception)
        {
            LogWorkspaceCleanupException(logger, resource.MaterializerContainerId, exception);
        }

        return false;
    }

    private async Task RemoveResourceQuietlyAsync(RuntimeSessionResource? resource)
    {
        if (resource is null)
            return;

        if (!string.IsNullOrWhiteSpace(resource.ContainerId))
            await RemoveContainerQuietlyAsync(resource.ContainerId).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(resource.MaterializerContainerId))
            await RemoveContainerQuietlyAsync(resource.MaterializerContainerId).ConfigureAwait(false);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await docker.RemoveWorkspaceVolumeAsync(resource.WorkspaceVolumeName, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogSessionVolumeRemovalFailed(logger, resource.WorkspaceVolumeName, exception);
        }
    }

    private async Task RemoveContainerQuietlyAsync(string containerId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await docker.RemoveContainerAsync(containerId, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogSessionContainerRemovalFailed(logger, containerId, exception);
        }
    }

    private string CreateFingerprint(RuntimeSessionRequest request)
    {
        var payload = new
        {
            request.ReleaseId,
            request.Image,
            Command = request.Command,
            Environment = request.Environment.OrderBy(static pair => pair.Key, StringComparer.Ordinal),
            Policy = new
            {
                request.SecurityPolicy.Id,
                request.SecurityPolicy.MemoryBytes,
                request.SecurityPolicy.NanoCpus,
                request.SecurityPolicy.PidsLimit,
                request.SecurityPolicy.MaximumDurationSeconds,
                request.SecurityPolicy.MaximumArtifactBytes,
                request.SecurityPolicy.MaximumOutputBytes,
                request.SecurityPolicy.TmpfsBytes
            },
            request.IsolationKind,
            Sandbox = new
            {
                sandbox.PolicyId,
                sandbox.SeccompProfileSha256,
                sandbox.SecurityOptions,
                sandbox.OpenFilesSoftLimit,
                sandbox.OpenFilesHardLimit
            },
            request.ManagementLabel,
            request.ResourceScope
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload)));
    }

    private async Task<RuntimeSessionSlot> AcquireOpenSlotAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        ValidateSessionId(sessionId);
        if (IsReleased(sessionId))
            throw new RuntimeSessionClosingException();

        var slot = _slots.GetOrAdd(sessionId, static _ => new RuntimeSessionSlot());
        await slot.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (Volatile.Read(ref slot.Closing) == 0 && !IsReleased(sessionId))
            return slot;

        slot.Gate.Release();
        throw new RuntimeSessionClosingException();
    }

    private void MarkReleased(string sessionId)
    {
        var now = DateTimeOffset.UtcNow;
        _releasedSessions[sessionId] = now.AddSeconds(_options.StaleContainerSeconds);
        foreach (var released in _releasedSessions)
        {
            if (released.Value <= now)
                _releasedSessions.TryRemove(released);
        }
    }

    private bool IsReleased(string sessionId)
    {
        if (!_releasedSessions.TryGetValue(sessionId, out var expiresAt))
            return false;
        if (expiresAt > DateTimeOffset.UtcNow)
            return true;
        _releasedSessions.TryRemove(sessionId, out _);
        return false;
    }

    private static void ResetArchive(Stream archive)
    {
        if (!archive.CanSeek)
            throw new InvalidOperationException("Runtime session archives must be seekable.");
        archive.Position = 0;
    }

    internal static void ValidateSessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) ||
            sessionId.Length > 128 ||
            sessionId.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')))
        {
            throw new ArgumentException("The runtime session ID is malformed.", nameof(sessionId));
        }
    }

    [LoggerMessage(EventId = 4020, Level = LogLevel.Warning, Message = "Runtime session {SessionId} referenced a stale Docker resource; recreating it.")]
    private static partial void LogStaleSessionResource(ILogger logger, string sessionId, Exception exception);

    [LoggerMessage(EventId = 4021, Level = LogLevel.Warning, Message = "Could not kill the active container for runtime session {SessionId}.")]
    private static partial void LogSessionKillFailed(ILogger logger, string sessionId, Exception exception);

    [LoggerMessage(EventId = 4022, Level = LogLevel.Warning, Message = "Runtime workspace cleanup container {ContainerId} exited with status {StatusCode}: {Error}")]
    private static partial void LogWorkspaceCleanupFailed(ILogger logger, string containerId, long statusCode, string error);

    [LoggerMessage(EventId = 4023, Level = LogLevel.Warning, Message = "Runtime workspace cleanup container {ContainerId} failed.")]
    private static partial void LogWorkspaceCleanupException(ILogger logger, string containerId, Exception exception);

    [LoggerMessage(EventId = 4024, Level = LogLevel.Warning, Message = "Could not remove runtime session container {ContainerId}.")]
    private static partial void LogSessionContainerRemovalFailed(ILogger logger, string containerId, Exception exception);

    [LoggerMessage(EventId = 4025, Level = LogLevel.Warning, Message = "Could not remove runtime session workspace volume {VolumeName}.")]
    private static partial void LogSessionVolumeRemovalFailed(ILogger logger, string volumeName, Exception exception);
}

internal sealed class RuntimeSessionClosingException : Exception
{
    public RuntimeSessionClosingException() : base("The runtime session is closing.")
    {
    }
}
