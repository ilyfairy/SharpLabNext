using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace SharpLabNext.BundleBuilder;

public sealed record RuntimePromotionSourceChange(
    string Status,
    string Path,
    string? OriginalPath = null);

public interface IRuntimePromotionSourceInspector
{
    Task<bool> IsAncestorAsync(
        string repositoryRoot,
        string ancestorRevision,
        string descendantRevision,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RuntimePromotionSourceChange>> DiffAsync(
        string repositoryRoot,
        string ancestorRevision,
        string descendantRevision,
        CancellationToken cancellationToken = default);
}

public sealed class GitRuntimePromotionSourceInspector : IRuntimePromotionSourceInspector
{
    private const long MaximumGitOutputBytes = 8 * 1024 * 1024;

    public async Task<bool> IsAncestorAsync(
        string repositoryRoot,
        string ancestorRevision,
        string descendantRevision,
        CancellationToken cancellationToken = default)
    {
        ValidateRevision(ancestorRevision);
        ValidateRevision(descendantRevision);
        var result = await RunGitAsync(
            repositoryRoot,
            ["merge-base", "--is-ancestor", ancestorRevision, descendantRevision],
            cancellationToken);
        return result.ExitCode switch
        {
            0 => true,
            1 => false,
            _ => throw new BundleValidationException(
                $"Could not verify runtime promotion ancestry: {SingleLine(result.StandardError)}")
        };
    }

    public async Task<IReadOnlyList<RuntimePromotionSourceChange>> DiffAsync(
        string repositoryRoot,
        string ancestorRevision,
        string descendantRevision,
        CancellationToken cancellationToken = default)
    {
        ValidateRevision(ancestorRevision);
        ValidateRevision(descendantRevision);
        var result = await RunGitAsync(
            repositoryRoot,
            [
                "diff",
                "--name-status",
                "-z",
                "--find-renames",
                $"{ancestorRevision}..{descendantRevision}",
                "--"
            ],
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new BundleValidationException(
                $"Could not inspect runtime promotion source delta: {SingleLine(result.StandardError)}");
        }

        var fields = result.StandardOutput.Split('\0');
        var changes = new List<RuntimePromotionSourceChange>();
        for (var index = 0; index < fields.Length;)
        {
            var status = fields[index++];
            if (status.Length == 0)
                break;
            if (index >= fields.Length || fields[index].Length == 0)
                throw new BundleValidationException("Git returned a malformed runtime promotion source delta.");
            if (status[0] is 'R' or 'C')
            {
                var originalPath = fields[index++];
                if (index >= fields.Length || fields[index].Length == 0)
                    throw new BundleValidationException("Git returned a malformed renamed promotion path.");
                changes.Add(new RuntimePromotionSourceChange(status, fields[index++], originalPath));
            }
            else
            {
                changes.Add(new RuntimePromotionSourceChange(status, fields[index++]));
            }
        }
        return changes;
    }

    private static async Task<GitResult> RunGitAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.StandardOutputEncoding = Encoding.UTF8;
        startInfo.StandardErrorEncoding = Encoding.UTF8;
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(Path.GetFullPath(repositoryRoot));
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        Process? process = null;
        Task<string>? stdout = null;
        Task<string>? stderr = null;
        Task? exit = null;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new BundleValidationException("Could not start Git for runtime promotion proof.");
            stdout = ReadBoundedAsync(process.StandardOutput, "stdout", cancellationToken);
            stderr = ReadBoundedAsync(process.StandardError, "stderr", cancellationToken);
            exit = process.WaitForExitAsync(cancellationToken);
            var stdoutCompleted = false;
            var stderrCompleted = false;
            while (!exit.IsCompleted)
            {
                var pending = new List<Task>(3) { exit };
                if (!stdoutCompleted)
                    pending.Add(stdout);
                if (!stderrCompleted)
                    pending.Add(stderr);
                var completed = await Task.WhenAny(pending);
                if (completed == stdout)
                {
                    await stdout;
                    stdoutCompleted = true;
                }
                else if (completed == stderr)
                {
                    await stderr;
                    stderrCompleted = true;
                }
            }

            await exit;
            if (!stdoutCompleted)
                await stdout;
            if (!stderrCompleted)
                await stderr;
            return new GitResult(process.ExitCode, stdout.Result, stderr.Result);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new BundleValidationException(
                $"Git is required to verify runtime promotion source closure: {exception.Message}");
        }
        catch (GitOutputLimitException exception)
        {
            // A producer that ignores the output limit can otherwise keep Git blocked on a full pipe.
            if (process is not null)
                await AbortProcessAsync(process, stdout, stderr, exit);
            throw new BundleValidationException(exception.Message);
        }
        catch
        {
            if (process is not null)
                await AbortProcessAsync(process, stdout, stderr, exit);
            throw;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static async Task AbortProcessAsync(
        Process process,
        params Task?[] tasks)
    {
        TryKill(process);
        foreach (var task in tasks)
        {
            if (task is null)
                continue;
            try
            {
                await task;
            }
            catch
            {
            }
        }
        try
        {
            await process.WaitForExitAsync();
        }
        catch
        {
        }
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        string streamName,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var buffer = new char[8192];
        long bytes = 0;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                return output.ToString();
            bytes = checked(bytes + Encoding.UTF8.GetByteCount(buffer, 0, read));
            if (bytes > MaximumGitOutputBytes)
            {
                throw new GitOutputLimitException(
                    $"Git {streamName} exceeded the {MaximumGitOutputBytes} byte output limit while verifying runtime promotion source closure.");
            }
            output.Append(buffer, 0, read);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static void ValidateRevision(string revision)
    {
        if (revision.Length is not (40 or 64) ||
            revision.Any(static character =>
                !char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f')))
        {
            throw new BundleValidationException(
                "Runtime promotion source revisions must be full lowercase Git commits.");
        }
    }

    private static string SingleLine(string value) =>
        string.Join(' ', value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();

    private sealed record GitResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class GitOutputLimitException(string message) : Exception(message);
}

internal static class RuntimePromotionSourceClosure
{
    private const long MaximumTransactionFileBytes = 16 * 1024 * 1024;
    internal const int MaximumTransactionFileCount = 1024;
    internal const long MaximumTransactionBytes = 256 * 1024 * 1024;
    private static readonly string[] SharedMaterialPaths =
    [
        "deploy/images.json",
        "profiles/catalog/catalog.json",
        "profiles/lock.json",
        "profiles/runtime-matrix.json"
    ];

    public static async Task<RuntimePromotionSourceClosureSnapshot?> CaptureAsync(
        string repositoryRoot,
        RepositorySourceProvenance releaseSource,
        IReadOnlyList<RuntimePromotionTrustSnapshot> promotionTrust,
        IRuntimePromotionSourceInspector? inspector,
        CancellationToken cancellationToken)
    {
        if (promotionTrust.Count == 0)
            return null;
        if (!releaseSource.IsVerified || releaseSource.HeadRevision is null)
        {
            throw new BundleValidationException(
                "Promotion-bound release material requires a clean verified release revision.");
        }
        var buildRevisions = promotionTrust
            .Select(static item => item.BuildSourceRevision)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (buildRevisions.Length != 1)
        {
            throw new BundleValidationException(
                "One release cannot combine runtime promotions built from different source revisions.");
        }
        var buildRevision = buildRevisions[0];
        var releaseRevision = releaseSource.Revision;
        if (StringComparer.Ordinal.Equals(buildRevision, releaseRevision))
        {
            throw new BundleValidationException(
                "Runtime promotion build and release revisions must be distinct commits.");
        }

        inspector ??= new GitRuntimePromotionSourceInspector();
        if (!await inspector.IsAncestorAsync(
                repositoryRoot,
                buildRevision,
                releaseRevision,
                cancellationToken))
        {
            throw new BundleValidationException(
                $"Runtime promotion build revision '{buildRevision}' is not an ancestor of release revision '{releaseRevision}'.");
        }

        var transactionFiles = new Dictionary<string, TransactionFile>(StringComparer.Ordinal);
        var capturedInputs = new Dictionary<string, TransactionFile>(StringComparer.Ordinal);
        foreach (var trust in promotionTrust.OrderBy(static item => item.RuntimeId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await AddVerifiedAsync(transactionFiles, repositoryRoot, trust.Receipt, cancellationToken);
            if (trust.WineOperatorReceipt is { } wineOperatorReceipt)
            {
                await AddVerifiedAsync(transactionFiles, repositoryRoot, wineOperatorReceipt.Receipt, cancellationToken);
                await AddVerifiedAsync(transactionFiles, repositoryRoot, wineOperatorReceipt.Signature, cancellationToken);
                await AddVerifiedAsync(
                    capturedInputs,
                    repositoryRoot,
                    wineOperatorReceipt.PublicKey,
                    cancellationToken);
            }
            foreach (var evidence in trust.Evidence)
                await AddVerifiedAsync(transactionFiles, repositoryRoot, evidence, cancellationToken);
            await AddVerifiedAsync(capturedInputs, repositoryRoot, trust.PerformancePolicy, cancellationToken);
            var signedPlan = trust.SignedPlan
                ?? throw new BundleValidationException(
                    $"Runtime '{trust.RuntimeId}' has no captured signed promotion plan.");
            Add(capturedInputs, new TransactionFile(signedPlan.PublicKey, signedPlan.PublicKeyBytes));
            var planBinding = await ValidatePlanBindingAsync(
                repositoryRoot,
                trust,
                capturedInputs[trust.PerformancePolicy.RelativePath],
                cancellationToken);
            Add(capturedInputs, planBinding.Candidate);
            foreach (var planFile in planBinding.TransactionFiles)
            {
                Add(transactionFiles, planFile);
            }
            Add(transactionFiles, await ReadFileAsync(
                repositoryRoot,
                $"profiles/runtimes/{trust.RuntimeId}.json",
                cancellationToken));
        }
        foreach (var relativePath in SharedMaterialPaths)
            Add(transactionFiles, await ReadFileAsync(repositoryRoot, relativePath, cancellationToken));

        var changes = await inspector.DiffAsync(
            repositoryRoot,
            buildRevision,
            releaseRevision,
            cancellationToken);
        ValidateExactDiff(transactionFiles.Keys, changes);
        var overlap = transactionFiles.Keys.Intersect(capturedInputs.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (overlap.Length > 0)
        {
            throw new BundleValidationException(
                "Runtime promotion baseline inputs overlap transaction outputs: " +
                string.Join(", ", overlap));
        }
        var capturedFiles = new Dictionary<string, TransactionFile>(StringComparer.Ordinal);
        foreach (var file in transactionFiles.Values)
            Add(capturedFiles, file);
        foreach (var file in capturedInputs.Values)
            Add(capturedFiles, file);
        return new RuntimePromotionSourceClosureSnapshot(
            buildRevision,
            releaseRevision,
            transactionFiles.Values
                .Select(static item => item.Snapshot)
                .OrderBy(static item => item.RelativePath, StringComparer.Ordinal)
                .ToArray(),
            capturedFiles.Values
                .Select(static item => new RuntimePromotionCapturedFile(
                    item.RelativePath,
                    item.Sha256,
                    item.Bytes))
                .OrderBy(static item => item.RelativePath, StringComparer.Ordinal)
                .ToArray());
    }

    public static async Task RevalidateAsync(
        string repositoryRoot,
        RuntimePromotionSourceClosureSnapshot snapshot,
        IRuntimePromotionSourceInspector? inspector,
        CancellationToken cancellationToken)
    {
        inspector ??= new GitRuntimePromotionSourceInspector();
        if (!await inspector.IsAncestorAsync(
                repositoryRoot,
                snapshot.BuildSourceRevision,
                snapshot.ReleaseSourceRevision,
                cancellationToken))
        {
            throw new BundleValidationException(
                "Runtime promotion source ancestry changed before release finalization.");
        }
        ValidateExactDiff(
            snapshot.Files.Select(static item => item.RelativePath),
            await inspector.DiffAsync(
                repositoryRoot,
                snapshot.BuildSourceRevision,
                snapshot.ReleaseSourceRevision,
                cancellationToken));
        foreach (var expected in snapshot.CapturedFiles)
        {
            var observed = await ReadFileAsync(
                repositoryRoot,
                expected.RelativePath,
                cancellationToken);
            var capturedDigest =
                $"sha256:{Convert.ToHexStringLower(SHA256.HashData(expected.Bytes))}";
            if (!StringComparer.Ordinal.Equals(expected.Sha256, capturedDigest) ||
                !StringComparer.Ordinal.Equals(expected.Sha256, observed.Sha256) ||
                !expected.Bytes.AsSpan().SequenceEqual(observed.Bytes))
            {
                throw new BundleValidationException(
                    $"Runtime promotion captured file '{expected.RelativePath}' changed before release finalization.");
            }
        }
    }

    private static async Task<PlanBindingFiles> ValidatePlanBindingAsync(
        string repositoryRoot,
        RuntimePromotionTrustSnapshot trust,
        TransactionFile performancePolicy,
        CancellationToken cancellationToken)
    {
        var signedPlan = trust.SignedPlan
            ?? throw new BundleValidationException(
                $"Runtime '{trust.RuntimeId}' has no captured signed promotion plan.");
        var plan = new TransactionFile(signedPlan.Plan, signedPlan.PlanBytes);
        var signature = new TransactionFile(signedPlan.Signature, signedPlan.SignatureBytes);
        var preflight = await ReadFileAsync(
            repositoryRoot,
            $"profiles/runtime-promotion-plans/{trust.RuntimeId}.profile.json",
            cancellationToken);
        var candidate = await ReadFileAsync(
            repositoryRoot,
            $"profiles/runtimes/candidates/{trust.RuntimeId}.json",
            cancellationToken);
        var context = RuntimePromotionPlanWorkflow.CreateContext(
            candidate.Bytes,
            preflight.Bytes,
            signedPlan.PlanBytes,
            performancePolicy.Bytes);
        if (!StringComparer.Ordinal.Equals(context.ProfileId, trust.RuntimeId) ||
            !StringComparer.Ordinal.Equals(context.PlanSha256, trust.PlanSha256) ||
            !StringComparer.Ordinal.Equals(context.SourceRevision, trust.BuildSourceRevision) ||
            !StringComparer.Ordinal.Equals(context.ImageReference, trust.ImmutableReference) ||
            !StringComparer.Ordinal.Equals(context.ImageId, trust.ImageId) ||
            context.ImageSizeBytes != trust.ImageSizeBytes)
        {
            throw new BundleValidationException(
                $"Runtime '{trust.RuntimeId}' promotion plan does not bind its receipt and immutable image.");
        }
        if (trust.WineOperatorReceipt is { } wineOperatorReceipt)
        {
            if (context.WineOperator != wineOperatorReceipt.Binding)
            {
                throw new BundleValidationException(
                    $"Runtime '{trust.RuntimeId}' promotion plan does not bind its signed Wine operator receipt.");
            }
        }
        else if (context.WineOperator is not null)
        {
            throw new BundleValidationException(
                $"Runtime '{trust.RuntimeId}' promotion plan unexpectedly binds a Wine operator receipt.");
        }
        return new PlanBindingFiles([plan, signature, preflight], candidate);
    }

    private static void ValidateExactDiff(
        IEnumerable<string> expectedPaths,
        IReadOnlyList<RuntimePromotionSourceChange> changes)
    {
        var expected = expectedPaths.ToHashSet(StringComparer.Ordinal);
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in changes)
        {
            if (change.Status is not ("A" or "M") || change.OriginalPath is not null)
            {
                throw new BundleValidationException(
                    $"Runtime promotion delta contains forbidden Git status '{change.Status}' for '{change.Path}'.");
            }
            ValidateCanonicalPath(change.Path);
            if (!observed.Add(change.Path))
            {
                throw new BundleValidationException(
                    $"Runtime promotion delta contains duplicate path '{change.Path}'.");
            }
        }
        var missing = expected.Except(observed, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var extra = observed.Except(expected, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length > 0 || extra.Length > 0)
        {
            throw new BundleValidationException(
                "Runtime promotion source delta is not the exact verified transaction union. " +
                $"Missing [{string.Join(", ", missing)}]; extra [{string.Join(", ", extra)}].");
        }
    }

    private static async Task<TransactionFile> ReadFileAsync(
        string repositoryRoot,
        string relativePath,
        CancellationToken cancellationToken)
    {
        ValidateCanonicalPath(relativePath);
        var root = Path.GetFullPath(repositoryRoot);
        var absolutePath = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(root, absolutePath);
        if (relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative == ".." || Path.IsPathRooted(relative))
        {
            throw new BundleValidationException(
                $"Runtime promotion transaction file '{relativePath}' escapes the repository.");
        }
        var directory = new DirectoryInfo(Path.GetDirectoryName(absolutePath)!);
        for (var current = directory; current is not null &&
             !StringComparer.OrdinalIgnoreCase.Equals(current.FullName, root); current = current.Parent)
        {
            current.Refresh();
            if (!current.Exists || current.LinkTarget is not null ||
                current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new BundleValidationException(
                    $"Runtime promotion transaction directory '{current.FullName}' is not a regular directory.");
            }
        }
        var info = new FileInfo(absolutePath);
        info.Refresh();
        if (!info.Exists || info.LinkTarget is not null ||
            info.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            info.Length is < 1 or > MaximumTransactionFileBytes)
        {
            throw new BundleValidationException(
                $"Runtime promotion transaction file '{relativePath}' must be a bounded regular non-link file.");
        }
        byte[] bytes;
        await using (var stream = new FileStream(
                         absolutePath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         16 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken);
            if (stream.ReadByte() != -1 || stream.Length != info.Length)
            {
                throw new BundleValidationException(
                    $"Runtime promotion transaction file '{relativePath}' changed while reading.");
            }
        }
        var digest = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
        return new TransactionFile(
            new RuntimePromotionFileSnapshot(relativePath, digest),
            bytes);
    }

    private static async Task AddVerifiedAsync(
        Dictionary<string, TransactionFile> files,
        string repositoryRoot,
        RuntimePromotionFileSnapshot expected,
        CancellationToken cancellationToken)
    {
        var observed = await ReadFileAsync(repositoryRoot, expected.RelativePath, cancellationToken);
        if (!StringComparer.Ordinal.Equals(expected.Sha256, observed.Sha256))
        {
            throw new BundleValidationException(
                $"Runtime promotion transaction file '{expected.RelativePath}' changed after online validation.");
        }
        Add(files, observed);
    }

    private static void Add(
        Dictionary<string, TransactionFile> files,
        TransactionFile file)
    {
        if (files.TryGetValue(file.RelativePath, out var existing) &&
            !StringComparer.Ordinal.Equals(existing.Sha256, file.Sha256))
        {
            throw new BundleValidationException(
                $"Runtime promotions disagree about transaction file '{file.RelativePath}'.");
        }
        files[file.RelativePath] = file;
    }

    private static void ValidateCanonicalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || Path.IsPathRooted(path) ||
            path.Split('/').Any(static segment => segment is "" or "." or ".."))
        {
            throw new BundleValidationException(
                $"Runtime promotion transaction path '{path}' is not canonical.");
        }
    }

    private sealed record TransactionFile(RuntimePromotionFileSnapshot Snapshot, byte[] Bytes)
    {
        public string RelativePath => Snapshot.RelativePath;
        public string Sha256 => Snapshot.Sha256;
    }

    private sealed record PlanBindingFiles(
        IReadOnlyList<TransactionFile> TransactionFiles,
        TransactionFile Candidate);
}

internal sealed record RuntimePromotionSourceClosureSnapshot(
    string BuildSourceRevision,
    string ReleaseSourceRevision,
    IReadOnlyList<RuntimePromotionFileSnapshot> Files,
    IReadOnlyList<RuntimePromotionCapturedFile> CapturedFiles);

internal sealed record RuntimePromotionCapturedFile(
    string RelativePath,
    string Sha256,
    byte[] Bytes);
