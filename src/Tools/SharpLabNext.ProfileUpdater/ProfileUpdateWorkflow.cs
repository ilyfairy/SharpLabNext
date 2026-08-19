using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpLabNext.Catalog;
using SharpLabNext.Contracts;

namespace SharpLabNext.ProfileUpdater;

public sealed class ProfileUpdateWorkflow
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
    private readonly string repositoryRoot;
    private readonly string lockPath;
    private readonly string catalogPath;
    private readonly string stateRoot;
    private readonly ReleaseLockUpdater lockUpdater;
    private readonly IProfileUpdateCommandRunner commandRunner;
    private readonly IProfileCandidateWorkspaceManager workspaceManager;
    private readonly TimeProvider timeProvider;

    public ProfileUpdateWorkflow(
        string repositoryRoot,
        string lockPath,
        string stateRoot,
        IProfileSourceClient sourceClient,
        IProfileUpdateCommandRunner commandRunner,
        TimeProvider? timeProvider = null,
        IProfileCandidateWorkspaceManager? workspaceManager = null)
    {
        this.repositoryRoot = Path.GetFullPath(repositoryRoot);
        this.lockPath = Path.GetFullPath(lockPath);
        catalogPath = Path.Combine(this.repositoryRoot, "profiles", "catalog", "catalog.json");
        this.stateRoot = Path.GetFullPath(stateRoot);
        lockUpdater = new ReleaseLockUpdater(
            sourceClient ?? throw new ArgumentNullException(nameof(sourceClient)),
            Path.Combine(this.repositoryRoot, "profiles", "channels"));
        this.commandRunner = commandRunner ?? throw new ArgumentNullException(nameof(commandRunner));
        this.workspaceManager = workspaceManager ?? new GitProfileCandidateWorkspaceManager(commandRunner);
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ProfileUpdateCheckResult> CheckAsync(
        string? releaseId = null,
        CancellationToken cancellationToken = default)
    {
        var startedAt = UtcNow();
        var current = await ReadLockAsync(lockPath, cancellationToken);
        try
        {
            var result = await lockUpdater.ResolveAsync(current.Document, releaseId, cancellationToken);
            var completedAt = UtcNow();
            var stage = SucceededStage(ProfileUpdateStage.Check, startedAt, completedAt);
            await SaveStateAsync(
                current.Document.ReleaseId,
                current.Digest,
                result.Candidate.ReleaseId,
                null,
                result.Changes.Count > 0,
                null,
                stage,
                cancellationToken);
            return new ProfileUpdateCheckResult(
                current.Digest,
                result.Candidate.ReleaseId,
                result.Changes.Count > 0,
                result.Changes,
                stage);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var stage = FailedStage(ProfileUpdateStage.Check, startedAt, UtcNow(), exception.Message);
            await SaveStateAsync(
                current.Document.ReleaseId,
                current.Digest,
                null,
                null,
                updateAvailable: false,
                lastKnownGoodDigest: null,
                stage,
                cancellationToken);
            throw;
        }
    }

    public async Task<ProfileUpdateCandidateResult> ResolveAsync(
        string? releaseId = null,
        string? outputPath = null,
        CancellationToken cancellationToken = default)
    {
        var startedAt = UtcNow();
        var current = await ReadLockAsync(lockPath, cancellationToken);
        try
        {
            var result = await lockUpdater.ResolveAsync(current.Document, releaseId, cancellationToken);
            var candidateBytes = SerializeLock(result.Candidate);
            var candidateDigest = ComputeDigest(candidateBytes);
            var candidatePath = GetCandidateLockPath(candidateDigest);
            await AtomicFile.WriteAllBytesAsync(candidatePath, candidateBytes, cancellationToken);
            var workspaceRoot = GetCandidateWorkspacePath(candidateDigest);
            await workspaceManager.PrepareAsync(repositoryRoot, workspaceRoot, cancellationToken);
            var catalogTemplate = await CatalogLoader.LoadCatalogAsync(catalogPath, cancellationToken);
            var material = await CandidateReleaseMaterializer.WriteAsync(
                workspaceRoot,
                catalogTemplate,
                result.Candidate,
                candidateDigest,
                cancellationToken);
            await AtomicFile.WriteAllBytesAsync(material.LockPath, candidateBytes, cancellationToken);
            var materialDigest = await ComputeMaterialDigestAsync(material, includePackageLocks: false, cancellationToken);
            if (outputPath is not null)
            {
                await AtomicFile.WriteAllBytesAsync(outputPath, candidateBytes, cancellationToken);
            }

            var completedAt = UtcNow();
            var stage = SucceededStage(ProfileUpdateStage.Resolve, startedAt, completedAt);
            var receipt = new ProfileUpdateReceipt
            {
                SchemaVersion = 1,
                ReleaseId = result.Candidate.ReleaseId,
                SourceDigest = current.Digest,
                CandidateDigest = candidateDigest,
                CandidatePath = Path.GetRelativePath(repositoryRoot, candidatePath).Replace('\\', '/'),
                WorkspacePath = Path.GetRelativePath(repositoryRoot, workspaceRoot).Replace('\\', '/'),
                MaterialDigest = materialDigest,
                CreatedAt = completedAt,
                Changes = result.Changes,
                Stages = [stage]
            };
            await SaveReceiptAsync(receipt, cancellationToken);
            await SaveStateAsync(
                current.Document.ReleaseId,
                current.Digest,
                receipt.ReleaseId,
                receipt.CandidateDigest,
                receipt.Changes.Count > 0,
                null,
                stage,
                cancellationToken);
            return new ProfileUpdateCandidateResult(candidateDigest, candidatePath, receipt);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var stage = FailedStage(ProfileUpdateStage.Resolve, startedAt, UtcNow(), exception.Message);
            await SaveStateAsync(
                current.Document.ReleaseId,
                current.Digest,
                null,
                null,
                updateAvailable: false,
                lastKnownGoodDigest: null,
                stage,
                cancellationToken);
            throw;
        }
    }

    public async Task<ProfileUpdateStageResult> BuildAsync(
        string? candidatePath,
        string? candidateDigest,
        string configuration,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration(configuration);
        var candidate = await LoadCandidateAsync(candidatePath, candidateDigest, cancellationToken);
        RequireLatestStageSucceeded(candidate.Receipt, ProfileUpdateStage.Resolve);
        var commands = await CreateBuildCommandsAsync(candidate, configuration, cancellationToken);
        var result = await RunCommandsAsync(
            candidate,
            ProfileUpdateStage.Build,
            configuration,
            testScope: null,
            commands,
            cancellationToken);
        var materialDigest = await ComputeMaterialDigestAsync(
            candidate.Material,
            includePackageLocks: true,
            cancellationToken);
        var receipt = result.Receipt with { MaterialDigest = materialDigest };
        await SaveReceiptAsync(receipt, cancellationToken);
        return result with { Receipt = receipt };
    }

    public async Task<ProfileUpdateStageResult> TestAsync(
        string? candidatePath,
        string? candidateDigest,
        string configuration,
        ProfileUpdateTestScope testScope,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration(configuration);
        var candidate = await LoadCandidateAsync(candidatePath, candidateDigest, cancellationToken);
        RequireLatestStageSucceeded(candidate.Receipt, ProfileUpdateStage.Build);
        await RequireMaterialDigestAsync(candidate, cancellationToken);
        var commands = CreateTestCommands(candidate, configuration, testScope);
        var result = await RunCommandsAsync(
            candidate,
            ProfileUpdateStage.Test,
            configuration,
            testScope,
            commands,
            cancellationToken);
        await RequireMaterialDigestAsync(candidate with { Receipt = result.Receipt }, cancellationToken);
        return result;
    }

    public async Task<ProfileUpdateStageResult> PromoteAsync(
        string? candidatePath,
        string? candidateDigest,
        CancellationToken cancellationToken = default)
    {
        var candidate = await LoadCandidateAsync(candidatePath, candidateDigest, cancellationToken);
        var latestTest = candidate.Receipt.Stages.LastOrDefault(static stage => stage.Stage == ProfileUpdateStage.Test);
        if (latestTest is null || latestTest.Status != ProfileUpdateStageStatus.Succeeded)
        {
            throw new ProfileUpdateValidationException("Promotion requires a successful test receipt for the candidate lock.");
        }

        if (latestTest.TestScope != ProfileUpdateTestScope.Full)
        {
            throw new ProfileUpdateValidationException("Promotion requires the full test scope to succeed.");
        }
        await RequireMaterialDigestAsync(candidate, cancellationToken);

        var startedAt = UtcNow();
        var activeLock = await ReadLockAsync(lockPath, cancellationToken);
        var successfulStage = SucceededStage(ProfileUpdateStage.Promote, startedAt, UtcNow());
        var successfulReceipt = candidate.Receipt with
        {
            Stages = [.. candidate.Receipt.Stages, successfulStage]
        };

        try
        {
            var historyRoot = Path.Combine(stateRoot, "history");
            var lastKnownGoodRoot = Path.Combine(stateRoot, "last-known-good");
            var previousHistoryRoot = Path.Combine(historyRoot, DigestHex(activeLock.Digest));
            var candidateHistoryRoot = Path.Combine(historyRoot, DigestHex(candidate.Digest));
            var candidateBytes = await File.ReadAllBytesAsync(candidate.Path, cancellationToken);
            var receiptBytes = SerializeJson(successfulReceipt);

            await WriteActiveMaterialSnapshotAsync(previousHistoryRoot, cancellationToken);
            await WriteCandidateMaterialSnapshotAsync(candidate, candidateHistoryRoot, cancellationToken);
            await AtomicFile.WriteAllBytesAsync(
                Path.Combine(candidateHistoryRoot, "receipt.json"),
                receiptBytes,
                cancellationToken);
            await AtomicFile.WriteAllBytesAsync(
                Path.Combine(lastKnownGoodRoot, "previous.lock.json"),
                activeLock.Bytes,
                cancellationToken);
            await AtomicFile.WriteAllBytesAsync(
                Path.Combine(lastKnownGoodRoot, "lock.json"),
                candidateBytes,
                cancellationToken);
            await WriteCandidateMaterialSnapshotAsync(
                candidate,
                Path.Combine(lastKnownGoodRoot, "material"),
                cancellationToken);
            await AtomicFile.WriteAllBytesAsync(
                Path.Combine(lastKnownGoodRoot, "receipt.json"),
                receiptBytes,
                cancellationToken);
            await SaveReceiptAsync(successfulReceipt, cancellationToken);

            var commitSourceDigest = ComputeDigest(await File.ReadAllBytesAsync(lockPath, cancellationToken));
            if (!string.Equals(commitSourceDigest, candidate.Receipt.SourceDigest, StringComparison.Ordinal))
            {
                throw new ProfileUpdateValidationException(
                    "The approved lock changed while promotion metadata was being prepared; resolve again.");
            }

            var replacements = await CreatePromotionReplacementsAsync(candidate, candidateBytes, cancellationToken);
            await AtomicFile.ReplaceSetAsync(replacements, cancellationToken);
            await SaveStateAsync(
                candidate.Document.ReleaseId,
                candidate.Digest,
                candidate.Document.ReleaseId,
                candidate.Digest,
                updateAvailable: false,
                lastKnownGoodDigest: candidate.Digest,
                successfulStage,
                cancellationToken);
            return new ProfileUpdateStageResult(
                candidate.Digest,
                candidate.Path,
                successfulReceipt,
                successfulStage);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var currentDigest = ComputeDigest(await File.ReadAllBytesAsync(lockPath, cancellationToken));
            if (!string.Equals(currentDigest, candidate.Digest, StringComparison.Ordinal))
            {
                var failedStage = FailedStage(ProfileUpdateStage.Promote, startedAt, UtcNow(), exception.Message);
                var failedReceipt = candidate.Receipt with
                {
                    Stages = [.. candidate.Receipt.Stages, failedStage]
                };
                await SaveReceiptAsync(failedReceipt, cancellationToken);
                await SaveStateAsync(
                    activeLock.Document.ReleaseId,
                    activeLock.Digest,
                    candidate.Document.ReleaseId,
                    candidate.Digest,
                    updateAvailable: true,
                    lastKnownGoodDigest: null,
                    failedStage,
                    cancellationToken);
            }

            throw;
        }
    }

    private async Task<ProfileUpdateStageResult> RunCommandsAsync(
        CandidateContext candidate,
        ProfileUpdateStage stageKind,
        string configuration,
        ProfileUpdateTestScope? testScope,
        IReadOnlyList<ProfileUpdateExternalCommand> commands,
        CancellationToken cancellationToken)
    {
        var stageStartedAt = UtcNow();
        var executed = new List<ProfileUpdateExecutedCommand>();
        Exception? failure = null;
        try
        {
            foreach (var command in commands.Where(static command => !command.AlwaysRun))
            {
                await RunCommandAsync(command, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failure = exception;
        }
        finally
        {
            foreach (var command in commands.Where(static command => command.AlwaysRun))
            {
                try
                {
                    await RunCommandAsync(command, CancellationToken.None);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failure = failure is null
                        ? exception
                        : new InvalidOperationException(
                            $"{failure.Message} Cleanup also failed: {exception.Message}",
                            new AggregateException(failure, exception));
                }
            }
        }

        if (failure is not null)
        {
            var failed = new ProfileUpdateStageReceipt
            {
                Stage = stageKind,
                Status = ProfileUpdateStageStatus.Failed,
                StartedAt = stageStartedAt,
                CompletedAt = UtcNow(),
                Configuration = configuration,
                TestScope = testScope,
                Commands = executed,
                Error = Limit(failure.Message)
            };
            var receipt = candidate.Receipt with
            {
                Stages = [.. candidate.Receipt.Stages, failed]
            };
            await SaveReceiptAndStateAsync(candidate, receipt, failed, cancellationToken);
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        var succeeded = new ProfileUpdateStageReceipt
        {
            Stage = stageKind,
            Status = ProfileUpdateStageStatus.Succeeded,
            StartedAt = stageStartedAt,
            CompletedAt = UtcNow(),
            Configuration = configuration,
            TestScope = testScope,
            Commands = executed
        };
        var successfulReceipt = candidate.Receipt with
        {
            Stages = [.. candidate.Receipt.Stages, succeeded]
        };
        await SaveReceiptAndStateAsync(candidate, successfulReceipt, succeeded, cancellationToken);
        return new ProfileUpdateStageResult(candidate.Digest, candidate.Path, successfulReceipt, succeeded);

        async Task RunCommandAsync(ProfileUpdateExternalCommand command, CancellationToken commandCancellationToken)
        {
            var commandStartedAt = UtcNow();
            ProfileUpdateCommandResult result;
            try
            {
                result = await commandRunner.RunAsync(command, commandCancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                executed.Add(CommandReceipt(command, commandStartedAt, UtcNow(), -1));
                throw new InvalidOperationException(
                    $"External command '{command.FileName}' could not be executed: {exception.Message}",
                    exception);
            }

            executed.Add(CommandReceipt(command, commandStartedAt, UtcNow(), result.ExitCode));
            if (result.ExitCode != 0)
                throw new ProfileUpdateCommandFailedException(command, result.ExitCode);
        }
    }

    private async Task<IReadOnlyList<ProfileUpdateExternalCommand>> CreateBuildCommandsAsync(
        CandidateContext candidate,
        string configuration,
        CancellationToken cancellationToken)
    {
        var sourceRevision = $"candidate-{DigestHex(candidate.Digest)[..12]}";
        var sourceDateEpoch = await SourceDateEpochResolver.ResolveAsync(
            candidate.WorkspaceRoot,
            sourceRevision,
            allowUncommittedSourceForDevelopment: true,
            cancellationToken: cancellationToken);
        var bakeEnvironment = BakeEnvironmentResolver.Create(
            candidate.Document,
            Path.Combine(candidate.WorkspaceRoot, "profiles", "base-images.json"),
            sourceRevision,
            sourceDateEpoch,
            "sharplabnext");
        return
        [
            Command(
                "dotnet",
                [
                    "run", "eng/verify-ilsense-inputs.cs", "--",
                    "--repository-root", candidate.WorkspaceRoot,
                    "--lock", candidate.Material.LockPath,
                    "--verify-restore"
                ],
                workingDirectory: candidate.WorkspaceRoot),
            Command("dotnet", ["run", "eng/verify-buildkit.cs"], workingDirectory: candidate.WorkspaceRoot),
            Command("dotnet", ["restore", "SharpLabNext.slnx", "--force-evaluate", "/p:RestoreLockedMode=false"], workingDirectory: candidate.WorkspaceRoot),
            Command("dotnet", ["restore", "SharpLabNext.slnx", "--locked-mode"], workingDirectory: candidate.WorkspaceRoot),
            Command("npm", ["--prefix", "frontend", "ci", "--no-audit", "--no-fund"], workingDirectory: candidate.WorkspaceRoot),
            Command("npm", ["--prefix", "frontend", "run", "lint"], workingDirectory: candidate.WorkspaceRoot),
            Command("npm", ["--prefix", "frontend", "run", "build"], workingDirectory: candidate.WorkspaceRoot),
            Command("dotnet", ["build", "SharpLabNext.slnx", "--configuration", configuration, "--no-restore"], workingDirectory: candidate.WorkspaceRoot),
            Command("docker", ["buildx", "bake", "--file", "eng/bake.hcl"], bakeEnvironment, candidate.WorkspaceRoot)
        ];
    }

    private List<ProfileUpdateExternalCommand> CreateTestCommands(
        CandidateContext candidate,
        string configuration,
        ProfileUpdateTestScope testScope)
    {
        var commands = new List<ProfileUpdateExternalCommand>();
        var workingDirectory = candidate.WorkspaceRoot;
        if (testScope == ProfileUpdateTestScope.Full)
        {
            commands.Add(Command(
                "dotnet",
                ["test", "SharpLabNext.slnx", "--configuration", configuration, "--no-build", "--no-restore"],
                workingDirectory: workingDirectory));
            commands.Add(Command(
                "npm",
                ["--prefix", "frontend", "run", "test", "--if-present"],
                workingDirectory: workingDirectory));
        }
        else
        {
            commands.Add(Command(
                "dotnet",
                [
                    "test",
                    "tests/Unit/SharpLabNext.UnitTests/SharpLabNext.UnitTests.csproj",
                    "--configuration",
                    configuration,
                    "--no-build",
                    "--no-restore"
                ],
                workingDirectory: workingDirectory));
            if (candidate.Receipt.Changes.Any(static change =>
                    change.ComponentId.StartsWith("frontend-", StringComparison.Ordinal)))
            {
                commands.Add(Command(
                    "npm",
                    ["--prefix", "frontend", "run", "test", "--if-present"],
                    workingDirectory: workingDirectory));
            }
        }

        commands.Add(Command(
            "node",
            ["eng/validate-schemas.mjs", "--release-lock", candidate.Material.LockPath],
            workingDirectory: workingDirectory));
        commands.Add(Command(
            "node",
            ["eng/validate-compose.mjs"],
            workingDirectory: workingDirectory));
        commands.Add(Command(
            "dotnet",
            [
                "run",
                "--project",
                "src/Tools/SharpLabNext.CompatibilityCli",
                "--configuration",
                configuration,
                "--no-build",
                "--",
                "validate",
                "--lock",
                candidate.Material.LockPath,
                "--output",
                Path.Combine(Path.GetDirectoryName(candidate.Path)!, "compatibility-report.json")
            ],
            workingDirectory: workingDirectory));

        if (testScope == ProfileUpdateTestScope.Full)
        {
            AddCandidateDeploymentCommands(commands, candidate, configuration);
        }
        return commands;
    }

    private void AddCandidateDeploymentCommands(
        List<ProfileUpdateExternalCommand> commands,
        CandidateContext candidate,
        string configuration)
    {
        var validationNumber = candidate.Receipt.Stages.Count(static stage => stage.Stage == ProfileUpdateStage.Test) + 1;
        var candidateArtifacts = Path.Combine(candidate.WorkspaceRoot, "artifacts", "profile-candidate");
        var bundleRoot = Path.Combine(candidateArtifacts, $"bundle-{validationNumber}");
        var generatedComposePath = Path.Combine(bundleRoot, "compose.generated.yaml");
        var bundlePath = Path.Combine(bundleRoot, "bundle.json");
        var performanceReportPath = Path.Combine(
            candidateArtifacts,
            $"performance-report-{validationNumber}.json");
        var projectName = $"sln-profile-{DigestHex(candidate.Digest)[..12]}-{validationNumber}";
        var endpoints = CandidateReleaseMaterializer.CreateValidationEndpoints(candidate.Digest);
        var gatewayPort = new Uri(endpoints.Gateway).Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var composeEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SHARPLABNEXT_BIND_ADDRESS"] = "127.0.0.1",
            ["SHARPLABNEXT_HTTP_PORT"] = gatewayPort,
            ["SHARPLABNEXT_RELEASE_ID"] = candidate.Document.ReleaseId
        };
        var e2eEnvironment = new Dictionary<string, string>(composeEnvironment, StringComparer.Ordinal)
        {
            ["SHARPLABNEXT_E2E_BASE_URL"] = endpoints.Gateway,
            ["SHARPLABNEXT_E2E_RUNTIME_IMAGE"] = $"sharplabnext/runtime-dotnet10:{candidate.Document.ReleaseId}"
        };
        var composeArguments = new[]
        {
            "compose",
            "--project-name", projectName,
            "--file", "deploy/compose.prod.yaml",
            "--file", generatedComposePath,
            "--file", candidate.Material.ValidationComposePath
        };
        var cleanupArguments = new[]
        {
            "compose",
            "--project-name", projectName,
            "--file", "deploy/compose.prod.yaml",
            "--file", candidate.Material.ValidationComposePath,
            "down",
            "--volumes",
            "--remove-orphans",
            "--timeout", "30"
        };

        commands.Add(Command(
            "dotnet",
            [
                "run",
                "--project", "src/Tools/SharpLabNext.BundleBuilder",
                "--configuration", configuration,
                "--no-build",
                "--no-restore",
                "--",
                "--repository-root", candidate.WorkspaceRoot,
                "--catalog", candidate.Material.CatalogPath,
                "--lock", candidate.Material.LockPath,
                "--output", bundleRoot,
                "--metadata-only",
                "--source-revision", $"candidate-{DigestHex(candidate.Digest)[..12]}",
                "--allow-uncommitted-source-for-development"
            ],
            workingDirectory: candidate.WorkspaceRoot));
        commands.Add(Command(
            "docker",
            [.. composeArguments, "config", "--quiet"],
            composeEnvironment,
            candidate.WorkspaceRoot));
        commands.Add(Command(
            "docker",
            [.. composeArguments, "up", "--detach", "--pull", "never", "--no-build", "--wait", "--wait-timeout", "300"],
            composeEnvironment,
            candidate.WorkspaceRoot));
        commands.Add(Command(
            "dotnet",
            [
                "run", "eng/verify-profile-candidate.cs", "--",
                "--lock", candidate.Material.LockPath,
                "--catalog", candidate.Material.CatalogPath,
                "--endpoints", candidate.Material.ValidationEndpointsPath,
                "--bundle", bundlePath
            ],
            workingDirectory: candidate.WorkspaceRoot));
        commands.Add(Command(
            "dotnet",
            ["run", "eng/smoke/gateway-compose.cs", "--", endpoints.Gateway, "--full"],
            e2eEnvironment,
            candidate.WorkspaceRoot));
        commands.Add(Command(
            "dotnet",
            [
                "run", "eng/performance/gateway-performance.cs", "--",
                "--base-address", endpoints.Gateway,
                "--thresholds", "eng/performance/thresholds.v1.json",
                "--output", performanceReportPath
            ],
            e2eEnvironment,
            candidate.WorkspaceRoot));
        commands.Add(Command(
            "dotnet",
            ["run", "eng/smoke/gateway-compose.cs", "--", endpoints.Gateway, "--security"],
            e2eEnvironment,
            candidate.WorkspaceRoot));
        commands.Add(Command(
            "dotnet",
            ["run", "eng/smoke/runtime-failures.cs", "--", endpoints.Gateway],
            e2eEnvironment,
            candidate.WorkspaceRoot));
        commands.Add(Command(
            "npm",
            ["--prefix", "frontend", "run", "test:e2e"],
            e2eEnvironment,
            candidate.WorkspaceRoot));
        commands.Add(Command(
            "docker",
            cleanupArguments,
            composeEnvironment,
            candidate.WorkspaceRoot,
            alwaysRun: true));
    }

    private async Task<CandidateContext> LoadCandidateAsync(
        string? candidatePath,
        string? candidateDigest,
        CancellationToken cancellationToken)
    {
        if (candidatePath is not null && candidateDigest is not null)
        {
            throw new ProfileUpdateValidationException("Specify either a candidate path or a candidate digest, not both.");
        }

        var requestedDigest = candidateDigest is null ? null : NormalizeDigest(candidateDigest);
        if (candidatePath is null && requestedDigest is null)
        {
            var state = await LoadStateAsync(cancellationToken)
                ?? throw new ProfileUpdateValidationException("No latest candidate is recorded; run resolve first.");
            requestedDigest = state.LatestCandidateDigest is null
                ? throw new ProfileUpdateValidationException("No latest candidate is recorded; run resolve first.")
                : NormalizeDigest(state.LatestCandidateDigest);
        }

        var path = candidatePath is null
            ? GetCandidateLockPath(requestedDigest!)
            : Path.GetFullPath(candidatePath);
        var candidateBytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var actualDigest = ComputeDigest(candidateBytes);
        if (requestedDigest is not null && !string.Equals(requestedDigest, actualDigest, StringComparison.Ordinal))
        {
            throw new ProfileUpdateValidationException(
                $"Candidate lock digest mismatch: expected '{requestedDigest}', actual '{actualDigest}'.");
        }

        var canonicalPath = GetCandidateLockPath(actualDigest);
        var canonicalBytes = await File.ReadAllBytesAsync(canonicalPath, cancellationToken);
        var canonicalDigest = ComputeDigest(canonicalBytes);
        if (!string.Equals(canonicalDigest, actualDigest, StringComparison.Ordinal))
        {
            throw new ProfileUpdateValidationException("The content-addressed candidate copy does not match the requested lock.");
        }

        var document = await CatalogLoader.LoadReleaseLockAsync(path, cancellationToken);
        var receipt = await LoadReceiptAsync(actualDigest, cancellationToken);
        if (!string.Equals(receipt.CandidateDigest, actualDigest, StringComparison.Ordinal))
        {
            throw new ProfileUpdateValidationException("Candidate receipt digest does not match the candidate lock.");
        }

        if (!string.Equals(receipt.ReleaseId, document.ReleaseId, StringComparison.Ordinal))
        {
            throw new ProfileUpdateValidationException("Candidate receipt release ID does not match the candidate lock.");
        }

        var workspaceRoot = Path.GetFullPath(Path.Combine(repositoryRoot, receipt.WorkspacePath));
        var expectedWorkspaceRoot = GetCandidateWorkspacePath(actualDigest);
        if (!string.Equals(workspaceRoot, expectedWorkspaceRoot, PathComparison))
        {
            throw new ProfileUpdateValidationException(
                "Candidate receipt workspace does not match the content-addressed candidate directory.");
        }
        var material = CandidateReleaseMaterializer.Locate(workspaceRoot);
        RequireMatchingRuntimeProfileSet(
            CandidateReleaseMaterializer.Locate(repositoryRoot).RuntimeProfiles,
            material.RuntimeProfiles);
        var workspaceLockBytes = await File.ReadAllBytesAsync(material.LockPath, cancellationToken);
        if (!string.Equals(ComputeDigest(workspaceLockBytes), actualDigest, StringComparison.Ordinal))
        {
            throw new ProfileUpdateValidationException(
                "Candidate workspace lock does not match the content-addressed candidate lock.");
        }
        var candidateCatalog = await CatalogLoader.LoadCatalogAsync(material.CatalogPath, cancellationToken);
        CandidateReleaseMaterializer.ValidateIdentityClosure(document, candidateCatalog);

        var activeBytes = await File.ReadAllBytesAsync(lockPath, cancellationToken);
        var activeDigest = ComputeDigest(activeBytes);
        if (!string.Equals(receipt.SourceDigest, activeDigest, StringComparison.Ordinal))
        {
            throw new ProfileUpdateValidationException(
                $"Candidate source digest '{receipt.SourceDigest}' does not match active lock digest '{activeDigest}'.");
        }

        return new CandidateContext(document, actualDigest, canonicalPath, receipt, workspaceRoot, material);
    }

    private static void RequireMatchingRuntimeProfileSet(
        IReadOnlyList<CandidateRuntimeProfileMaterial> active,
        IReadOnlyList<CandidateRuntimeProfileMaterial> candidate)
    {
        var activePaths = active.Select(static profile => profile.RelativePath).Order(StringComparer.Ordinal).ToArray();
        var candidatePaths = candidate.Select(static profile => profile.RelativePath).Order(StringComparer.Ordinal).ToArray();
        if (!activePaths.SequenceEqual(candidatePaths, StringComparer.Ordinal))
        {
            throw new ProfileUpdateValidationException(
                "Candidate active runtime profile set does not match the approved repository profile set.");
        }
    }

    private async Task SaveReceiptAndStateAsync(
        CandidateContext candidate,
        ProfileUpdateReceipt receipt,
        ProfileUpdateStageReceipt stage,
        CancellationToken cancellationToken)
    {
        await SaveReceiptAsync(receipt, cancellationToken);
        var active = await ReadLockAsync(lockPath, cancellationToken);
        await SaveStateAsync(
            active.Document.ReleaseId,
            active.Digest,
            candidate.Document.ReleaseId,
            candidate.Digest,
            candidate.Receipt.Changes.Count > 0,
            null,
            stage,
            cancellationToken);
    }

    private async Task SaveStateAsync(
        string activeReleaseId,
        string activeDigest,
        string? candidateReleaseId,
        string? candidateDigest,
        bool updateAvailable,
        string? lastKnownGoodDigest,
        ProfileUpdateStageReceipt stage,
        CancellationToken cancellationToken)
    {
        var previous = await LoadStateAsync(cancellationToken);
        var clearCandidate = stage.Stage == ProfileUpdateStage.Check ||
            stage is { Stage: ProfileUpdateStage.Resolve, Status: ProfileUpdateStageStatus.Failed };
        var effectiveLastKnownGoodDigest = lastKnownGoodDigest ?? previous?.LastKnownGoodDigest ?? activeDigest;
        var effectiveLastKnownGoodReleaseId = lastKnownGoodDigest is not null
            ? activeReleaseId
            : previous?.LastKnownGoodReleaseId ?? activeReleaseId;
        var state = new ProfileUpdaterState
        {
            SchemaVersion = 1,
            ActiveReleaseId = activeReleaseId,
            ActiveLockDigest = activeDigest,
            LatestCandidateReleaseId = clearCandidate
                ? null
                : candidateReleaseId ?? previous?.LatestCandidateReleaseId,
            LatestCandidateDigest = clearCandidate
                ? null
                : candidateDigest ?? previous?.LatestCandidateDigest,
            UpdateAvailable = updateAvailable,
            LastKnownGoodReleaseId = effectiveLastKnownGoodReleaseId,
            LastKnownGoodDigest = effectiveLastKnownGoodDigest,
            LastCheckedAt = stage.Stage is ProfileUpdateStage.Check or ProfileUpdateStage.Resolve
                ? stage.CompletedAt
                : previous?.LastCheckedAt,
            UpdatedAt = UtcNow(),
            LastStage = stage
        };
        await AtomicFile.WriteAllBytesAsync(StatePath, SerializeJson(state), cancellationToken);
        await AtomicFile.WriteAllBytesAsync(
            PublicStatusPath,
            SerializeJson(CreatePublicStatus(state)),
            cancellationToken);
    }

    private static ProfileUpdateStatusDocument CreatePublicStatus(ProfileUpdaterState state)
    {
        var failed = state.LastStage.Status == ProfileUpdateStageStatus.Failed;
        return new ProfileUpdateStatusDocument
        {
            SchemaVersion = 1,
            Status = failed
                ? state.LastStage.Stage == ProfileUpdateStage.Check
                    ? ProfileUpdateStatusKind.Unknown
                    : ProfileUpdateStatusKind.CandidateFailed
                : state.LastStage.Stage switch
                {
                    ProfileUpdateStage.Check => state.UpdateAvailable
                        ? ProfileUpdateStatusKind.UpdateAvailable
                        : ProfileUpdateStatusKind.UpToDate,
                    ProfileUpdateStage.Resolve or ProfileUpdateStage.Build or ProfileUpdateStage.Test =>
                        ProfileUpdateStatusKind.CandidateInProgress,
                    ProfileUpdateStage.Promote => ProfileUpdateStatusKind.CandidateApproved,
                    _ => ProfileUpdateStatusKind.Unknown
                },
            Checked = state.LastCheckedAt is not null,
            Active = new ProfileUpdateReleaseStatus
            {
                ReleaseId = state.ActiveReleaseId,
                LockDigest = state.ActiveLockDigest
            },
            LastKnownGood = CreateReleaseStatus(
                state.LastKnownGoodReleaseId,
                state.LastKnownGoodDigest),
            Candidate = CreateReleaseStatus(
                state.LatestCandidateReleaseId,
                state.LatestCandidateDigest),
            UpdateAvailable = failed && state.LastStage.Stage == ProfileUpdateStage.Check
                ? null
                : state.UpdateAvailable,
            CheckedAt = state.LastCheckedAt,
            UpdatedAt = state.UpdatedAt,
            LastStage = new ProfileUpdatePublicStageStatus
            {
                Stage = PublicStage(state.LastStage.Stage),
                Outcome = state.LastStage.Status == ProfileUpdateStageStatus.Succeeded
                    ? ProfileUpdatePublicStageOutcome.Succeeded
                    : ProfileUpdatePublicStageOutcome.Failed,
                StartedAt = state.LastStage.StartedAt,
                CompletedAt = state.LastStage.CompletedAt,
                Error = failed ? PublicError(state.LastStage.Stage) : null
            }
        };
    }

    private static ProfileUpdateReleaseStatus? CreateReleaseStatus(string? releaseId, string? digest) =>
        string.IsNullOrWhiteSpace(releaseId) || string.IsNullOrWhiteSpace(digest)
            ? null
            : new ProfileUpdateReleaseStatus
            {
                ReleaseId = releaseId,
                LockDigest = digest
            };

    private static ProfileUpdatePublicError PublicError(ProfileUpdateStage stage) => stage switch
    {
        ProfileUpdateStage.Check => new ProfileUpdatePublicError
        {
            Code = "profile-update.check-failed",
            Message = "Profile update check failed; update availability is unknown."
        },
        ProfileUpdateStage.Resolve => new ProfileUpdatePublicError
        {
            Code = "profile-update.resolve-failed",
            Message = "Profile candidate resolution failed; the approved release remains active."
        },
        ProfileUpdateStage.Build => new ProfileUpdatePublicError
        {
            Code = "profile-update.build-failed",
            Message = "Profile candidate build failed; the approved release remains active."
        },
        ProfileUpdateStage.Test => new ProfileUpdatePublicError
        {
            Code = "profile-update.test-failed",
            Message = "Profile candidate validation failed; the approved release remains active."
        },
        ProfileUpdateStage.Promote => new ProfileUpdatePublicError
        {
            Code = "profile-update.promote-failed",
            Message = "Profile candidate promotion failed; the previous approved release remains active."
        },
        _ => new ProfileUpdatePublicError
        {
            Code = "profile-update.failed",
            Message = "Profile update failed; the approved release remains active."
        }
    };

    private static ProfileUpdatePublicStage PublicStage(ProfileUpdateStage stage) => stage switch
    {
        ProfileUpdateStage.Check => ProfileUpdatePublicStage.Check,
        ProfileUpdateStage.Resolve => ProfileUpdatePublicStage.Resolve,
        ProfileUpdateStage.Build => ProfileUpdatePublicStage.Build,
        ProfileUpdateStage.Test => ProfileUpdatePublicStage.Test,
        ProfileUpdateStage.Promote => ProfileUpdatePublicStage.Promote,
        _ => ProfileUpdatePublicStage.None
    };

    private async Task<ProfileUpdaterState?> LoadStateAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(StatePath))
        {
            return null;
        }

        return await DeserializeAsync<ProfileUpdaterState>(StatePath, cancellationToken);
    }

    private async Task SaveReceiptAsync(
        ProfileUpdateReceipt receipt,
        CancellationToken cancellationToken) =>
        await AtomicFile.WriteAllBytesAsync(
            GetReceiptPath(receipt.CandidateDigest),
            SerializeJson(receipt),
            cancellationToken);

    private async Task<ProfileUpdateReceipt> LoadReceiptAsync(
        string digest,
        CancellationToken cancellationToken) =>
        await DeserializeAsync<ProfileUpdateReceipt>(GetReceiptPath(digest), cancellationToken);

    private static async Task<T> DeserializeAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException($"JSON document '{path}' is empty.");
    }

    private static async Task<LockContext> ReadLockAsync(string path, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var document = await CatalogLoader.LoadReleaseLockAsync(path, cancellationToken);
        return new LockContext(document, ComputeDigest(bytes), bytes);
    }

    private static async Task<string> ComputeMaterialDigestAsync(
        CandidateReleaseMaterial material,
        bool includePackageLocks,
        CancellationToken cancellationToken)
    {
        var files = new List<string>
        {
            material.LockPath,
            material.CatalogPath,
            material.VersionsPath
        };
        files.AddRange(material.RuntimeProfiles.Select(static profile => profile.Path));
        if (includePackageLocks)
        {
            files.AddRange(EnumeratePackageLocks(material.WorkspaceRoot));
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in files.Distinct(PathComparer).OrderBy(
                     path => Path.GetRelativePath(material.WorkspaceRoot, path),
                     StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(material.WorkspaceRoot, path).Replace('\\', '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData([0]);
            await using var stream = File.OpenRead(path);
            var fileHash = await SHA256.HashDataAsync(stream, cancellationToken);
            hash.AppendData(fileHash);
        }
        return $"sha256:{Convert.ToHexStringLower(hash.GetHashAndReset())}";
    }

    private static async Task RequireMaterialDigestAsync(
        CandidateContext candidate,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(candidate.Receipt.MaterialDigest))
            throw new ProfileUpdateValidationException("Candidate receipt has no built material digest.");
        var actual = await ComputeMaterialDigestAsync(candidate.Material, includePackageLocks: true, cancellationToken);
        if (!string.Equals(actual, candidate.Receipt.MaterialDigest, StringComparison.Ordinal))
        {
            throw new ProfileUpdateValidationException(
                $"Candidate material digest mismatch: expected '{candidate.Receipt.MaterialDigest}', actual '{actual}'.");
        }
    }

    private async Task<IReadOnlyList<(string Path, ReadOnlyMemory<byte> Content)>> CreatePromotionReplacementsAsync(
        CandidateContext candidate,
        byte[] candidateLock,
        CancellationToken cancellationToken)
    {
        var replacements = new List<(string Path, ReadOnlyMemory<byte> Content)>
        {
            (catalogPath, await File.ReadAllBytesAsync(candidate.Material.CatalogPath, cancellationToken)),
            (Path.Combine(repositoryRoot, "profiles", "versions.props"),
                await File.ReadAllBytesAsync(candidate.Material.VersionsPath, cancellationToken))
        };
        foreach (var runtimeProfile in candidate.Material.RuntimeProfiles)
        {
            replacements.Add((
                Path.Combine(repositoryRoot, runtimeProfile.RelativePath),
                await File.ReadAllBytesAsync(runtimeProfile.Path, cancellationToken)));
        }
        foreach (var packageLock in EnumeratePackageLocks(candidate.WorkspaceRoot))
        {
            var relative = Path.GetRelativePath(candidate.WorkspaceRoot, packageLock);
            replacements.Add((
                Path.Combine(repositoryRoot, relative),
                await File.ReadAllBytesAsync(packageLock, cancellationToken)));
        }
        replacements.Add((lockPath, candidateLock));
        return replacements;
    }

    private async Task WriteActiveMaterialSnapshotAsync(
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        await AtomicFile.WriteAllBytesAsync(
            Path.Combine(destinationRoot, "lock.json"),
            await File.ReadAllBytesAsync(lockPath, cancellationToken),
            cancellationToken);
        await AtomicFile.WriteAllBytesAsync(
            Path.Combine(destinationRoot, "catalog.json"),
            await File.ReadAllBytesAsync(catalogPath, cancellationToken),
            cancellationToken);
        var versionsPath = Path.Combine(repositoryRoot, "profiles", "versions.props");
        await AtomicFile.WriteAllBytesAsync(
            Path.Combine(destinationRoot, "versions.props"),
            await File.ReadAllBytesAsync(versionsPath, cancellationToken),
            cancellationToken);
        foreach (var runtimeProfile in CandidateReleaseMaterializer.Locate(repositoryRoot).RuntimeProfiles)
        {
            await AtomicFile.WriteAllBytesAsync(
                Path.Combine(destinationRoot, "runtimes", Path.GetFileName(runtimeProfile.Path)),
                await File.ReadAllBytesAsync(runtimeProfile.Path, cancellationToken),
                cancellationToken);
        }
        foreach (var packageLock in EnumeratePackageLocks(repositoryRoot))
        {
            var relative = Path.GetRelativePath(repositoryRoot, packageLock);
            await AtomicFile.WriteAllBytesAsync(
                Path.Combine(destinationRoot, "package-locks", relative),
                await File.ReadAllBytesAsync(packageLock, cancellationToken),
                cancellationToken);
        }
    }

    private static async Task WriteCandidateMaterialSnapshotAsync(
        CandidateContext candidate,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        await AtomicFile.WriteAllBytesAsync(
            Path.Combine(destinationRoot, "lock.json"),
            await File.ReadAllBytesAsync(candidate.Material.LockPath, cancellationToken),
            cancellationToken);
        await AtomicFile.WriteAllBytesAsync(
            Path.Combine(destinationRoot, "catalog.json"),
            await File.ReadAllBytesAsync(candidate.Material.CatalogPath, cancellationToken),
            cancellationToken);
        await AtomicFile.WriteAllBytesAsync(
            Path.Combine(destinationRoot, "versions.props"),
            await File.ReadAllBytesAsync(candidate.Material.VersionsPath, cancellationToken),
            cancellationToken);
        foreach (var runtimeProfile in candidate.Material.RuntimeProfiles)
        {
            await AtomicFile.WriteAllBytesAsync(
                Path.Combine(destinationRoot, "runtimes", Path.GetFileName(runtimeProfile.Path)),
                await File.ReadAllBytesAsync(runtimeProfile.Path, cancellationToken),
                cancellationToken);
        }
        foreach (var packageLock in EnumeratePackageLocks(candidate.WorkspaceRoot))
        {
            var relative = Path.GetRelativePath(candidate.WorkspaceRoot, packageLock);
            await AtomicFile.WriteAllBytesAsync(
                Path.Combine(destinationRoot, "package-locks", relative),
                await File.ReadAllBytesAsync(packageLock, cancellationToken),
                cancellationToken);
        }
    }

    private static IEnumerable<string> EnumeratePackageLocks(string root) =>
        Directory.EnumerateFiles(root, "packages*.lock.json", SearchOption.AllDirectories)
            .Where(path => IsPackageLockFileName(Path.GetFileName(path)) && !IsGeneratedPath(root, path))
            .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.Ordinal);

    private static bool IsPackageLockFileName(string fileName) =>
        string.Equals(fileName, "packages.lock.json", StringComparison.Ordinal) ||
        fileName.StartsWith("packages.", StringComparison.Ordinal) &&
        fileName.EndsWith(".lock.json", StringComparison.Ordinal);

    private static bool IsGeneratedPath(string root, string path)
    {
        var segments = Path.GetRelativePath(root, path)
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(static segment => segment is "bin" or "obj" or "node_modules" or "artifacts" or ".git");
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private ProfileUpdateExternalCommand Command(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null,
        string? workingDirectory = null,
        bool alwaysRun = false) =>
        new(fileName, arguments, workingDirectory ?? repositoryRoot, environment, alwaysRun);

    private string GetCandidateLockPath(string digest) =>
        Path.Combine(stateRoot, "candidates", DigestHex(digest), "lock.json");

    private string GetCandidateWorkspacePath(string digest) =>
        Path.Combine(stateRoot, "candidates", DigestHex(digest), "workspace");

    private string GetReceiptPath(string digest) =>
        Path.Combine(stateRoot, "candidates", DigestHex(digest), "receipt.json");

    private string StatePath => Path.Combine(stateRoot, "state.json");

    private string PublicStatusPath => Path.Combine(stateRoot, "status.public.json");

    private DateTimeOffset UtcNow() => timeProvider.GetUtcNow();

    private static byte[] SerializeLock(ReleaseLockDocument document)
    {
        var canonical = document with
        {
            Components = document.Components
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal)
        };
        return SerializeJson(canonical);
    }

    private static byte[] SerializeJson<T>(T value) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }

    private static string ComputeDigest(ReadOnlySpan<byte> bytes) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";

    private static string NormalizeDigest(string digest)
    {
        var normalized = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? $"sha256:{digest[7..].ToLowerInvariant()}"
            : $"sha256:{digest.ToLowerInvariant()}";
        _ = DigestHex(normalized);
        return normalized;
    }

    private static string DigestHex(string digest)
    {
        if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71)
        {
            throw new ProfileUpdateValidationException($"'{digest}' is not a SHA-256 digest.");
        }

        foreach (var character in digest.AsSpan(7))
        {
            if (!char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character))
            {
                throw new ProfileUpdateValidationException($"'{digest}' is not a SHA-256 digest.");
            }
        }

        return digest[7..];
    }

    private static void RequireLatestStageSucceeded(ProfileUpdateReceipt receipt, ProfileUpdateStage stage)
    {
        var latest = receipt.Stages.LastOrDefault(item => item.Stage == stage);
        if (latest is null || latest.Status != ProfileUpdateStageStatus.Succeeded)
        {
            throw new ProfileUpdateValidationException(
                $"Stage '{stage.ToString().ToLowerInvariant()}' must succeed before continuing.");
        }
    }

    private static void ValidateConfiguration(string configuration)
    {
        if (configuration is not ("Debug" or "Release"))
        {
            throw new ProfileUpdateValidationException("Configuration must be Debug or Release.");
        }
    }

    private static ProfileUpdateExecutedCommand CommandReceipt(
        ProfileUpdateExternalCommand command,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        int exitCode) =>
        new()
        {
            FileName = command.FileName,
            Arguments = command.Arguments,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            ExitCode = exitCode
        };

    private static ProfileUpdateStageReceipt SucceededStage(
        ProfileUpdateStage stage,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt) =>
        new()
        {
            Stage = stage,
            Status = ProfileUpdateStageStatus.Succeeded,
            StartedAt = startedAt,
            CompletedAt = completedAt
        };

    private static ProfileUpdateStageReceipt FailedStage(
        ProfileUpdateStage stage,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        string error) =>
        new()
        {
            Stage = stage,
            Status = ProfileUpdateStageStatus.Failed,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            Error = Limit(error)
        };

    private static string Limit(string value) => value.Length <= 4096 ? value : value[..4096];

    private sealed record CandidateContext(
        ReleaseLockDocument Document,
        string Digest,
        string Path,
        ProfileUpdateReceipt Receipt,
        string WorkspaceRoot,
        CandidateReleaseMaterial Material);

    private sealed record LockContext(ReleaseLockDocument Document, string Digest, byte[] Bytes);
}

public static class BakeEnvironmentResolver
{
    private const string BaseImageDigestMarker = "@sha256:";
    private static readonly JsonSerializerOptions BaseImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly Dictionary<string, string> RequiredBaseImages =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["node-builder"] = "BASE_NODE_IMAGE",
            ["dotnet-sdk"] = "BASE_DOTNET_SDK_IMAGE",
            ["dotnet-aspnet"] = "BASE_DOTNET_ASPNET_IMAGE",
            ["const-generics-aspnet"] = "BASE_CONST_GENERICS_ASPNET_IMAGE",
            ["dotnet-runtime-deps"] = "BASE_DOTNET_RUNTIME_DEPS_IMAGE",
            ["dotnet-runtime-build"] = "BASE_DOTNET_RUNTIME_BUILD_IMAGE",
            ["mono-jsil"] = "BASE_MONO_JSIL_IMAGE"
        };

    public static async Task<Dictionary<string, string>> CreateAsync(
        string lockPath,
        string baseImageManifestPath,
        string sourceRevision,
        string sourceDateEpoch,
        string imagePrefix = "sharplabnext",
        string? controlRuntimeTargetFramework = null,
        CancellationToken cancellationToken = default)
    {
        var releaseLock = await CatalogLoader.LoadReleaseLockAsync(
            Path.GetFullPath(lockPath),
            cancellationToken);
        return Create(
            releaseLock,
            baseImageManifestPath,
            sourceRevision,
            sourceDateEpoch,
            imagePrefix,
            controlRuntimeTargetFramework);
    }

    public static Dictionary<string, string> Create(
        ReleaseLockDocument releaseLock,
        string baseImageManifestPath,
        string sourceRevision,
        string sourceDateEpoch,
        string imagePrefix = "sharplabnext",
        string? controlRuntimeTargetFramework = null)
    {
        ArgumentNullException.ThrowIfNull(releaseLock);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["RELEASE_ID"] = RequiredValue(releaseLock.ReleaseId, "releaseId"),
            ["IMAGE_PREFIX"] = RequiredValue(imagePrefix, "imagePrefix"),
            ["SOURCE_REVISION"] = RequiredValue(sourceRevision, "sourceRevision"),
            ["SOURCE_DATE_EPOCH"] = SourceDateEpochResolver.Validate(sourceDateEpoch),
            // The Wine images use the same shared control-plane bridge as the
            // rest of the runtime fleet.  Keep this selection in the Bake
            // environment rather than in a Dockerfile ARG default so a
            // release cannot silently switch frameworks.
            ["WINE_CONTROL_TFM"] = ValidateControlRuntimeTargetFramework(
                controlRuntimeTargetFramework ?? "net10.0")
        };

        var roslynStable = RequiredComponent(releaseLock, "roslyn-stable");
        AddPackage(
            environment,
            roslynStable,
            "roslyn-stable",
            "ROSLYN_STABLE_VERSION",
            "ROSLYN_STABLE_SOURCE_URI");

        var roslynMain = RequiredComponent(releaseLock, "roslyn-main");
        environment["ROSLYN_MAIN_VERSION"] = RequiredVersion(roslynMain, "roslyn-main");
        environment["ROSLYN_MAIN_COMMIT"] = RequiredValue(roslynMain.Commit, "roslyn-main.commit");
        environment["ROSLYN_MAIN_ARCHIVE_URL"] = RequiredValue(roslynMain.SourceUri, "roslyn-main.sourceUri");
        environment["ROSLYN_MAIN_ARCHIVE_SHA256"] = DigestHex(roslynMain.Digest, "roslyn-main.digest");
        environment["ROSLYN_MAIN_SOURCE_URI"] = RequiredValue(roslynMain.SourceUri, "roslyn-main.sourceUri");

        AddPackage(
            environment,
            RequiredComponent(releaseLock, "fsharp-stable"),
            "fsharp-stable",
            "FSHARP_COMPILER_SERVICE_VERSION",
            "FSHARP_COMPILER_SERVICE_SOURCE_URI");
        AddPackage(
            environment,
            RequiredComponent(releaseLock, "fsharp-core"),
            "fsharp-core",
            "FSHARP_CORE_VERSION",
            "FSHARP_CORE_SOURCE_URI");
        AddGSharp(environment, releaseLock);
        AddPeachPie(environment, releaseLock);
        AddJsil(environment, releaseLock);
        AddPackage(
            environment,
            RequiredComponent(releaseLock, "ilspy"),
            "ilspy",
            "ILSPY_VERSION",
            "ILSPY_SOURCE_URI");
        AddPackage(
            environment,
            RequiredComponent(releaseLock, "dotnet-ilverify"),
            "dotnet-ilverify",
            "ILVERIFICATION_VERSION",
            "ILVERIFICATION_SOURCE_URI");
        AddPackage(
            environment,
            RequiredComponent(releaseLock, "mobius-ilasm-stable"),
            "mobius-ilasm-stable",
            "MOBIUS_ILASM_VERSION",
            "MOBIUS_ILASM_SOURCE_URI");
        AddILSense(environment, releaseLock);
        environment["MINILANG_VERSION"] = RequiredVersion(
            RequiredComponent(releaseLock, "minilang-stable"),
            "minilang-stable");
        environment["ARTIFACTS_DEFAULT_VERSION"] = RequiredVersion(
            RequiredComponent(releaseLock, "artifacts-default"),
            "artifacts-default");
        environment["ARTIFACTS_CONST_GENERICS_VERSION"] = RequiredVersion(
            RequiredComponent(releaseLock, "artifacts-const-generics"),
            "artifacts-const-generics");
        environment["IL_ASSEMBLER_VERSION"] = RequiredVersion(
            RequiredComponent(releaseLock, "il-assembler"),
            "il-assembler");

        AddReferenceSet(environment, releaseLock, "net10-ref", "NET10_REFERENCE_PACK_VERSION", "NET10");
        AddReferenceSet(environment, releaseLock, "net11-preview-ref", "NET11_REFERENCE_VERSION", "NET11");
        AddReferenceSet(
            environment,
            releaseLock,
            "netfx48-managed-ref",
            "NETFX48_MANAGED_REFERENCE_VERSION",
            "NETFX48_MANAGED");
        AddFrameworkManagedReferenceDigests(environment, releaseLock);
        AddRuntime(environment, releaseLock, "dotnet-10-linux-x64", "DOTNET10");
        AddRuntime(environment, releaseLock, "dotnet-11-preview-linux-x64", "DOTNET11");
        AddJitProfilerProvenance(environment, releaseLock);
        AddConstGenerics(environment, releaseLock);
        AddCppCli(environment, releaseLock);
        AddJSharp(environment, releaseLock);
        AddBaseImages(environment, baseImageManifestPath);
        return environment;
    }

    private static void AddJitProfilerProvenance(
        Dictionary<string, string> environment,
        ReleaseLockDocument releaseLock)
    {
        var scaffold = RequiredComponent(releaseLock, "jit-profiler-clr-samples");
        var runtimeHeaders = RequiredComponent(releaseLock, "jit-profiler-runtime-headers");
        environment["JIT_PROFILER_CLR_SAMPLES_COMMIT"] = RequiredValue(
            scaffold.Commit,
            "jit-profiler-clr-samples.commit");
        environment["JIT_PROFILER_CLR_SAMPLES_SOURCE_URI"] = RequiredValue(
            scaffold.SourceUri,
            "jit-profiler-clr-samples.sourceUri");
        environment["JIT_PROFILER_RUNTIME_HEADERS_COMMIT"] = RequiredValue(
            runtimeHeaders.Commit,
            "jit-profiler-runtime-headers.commit");
        environment["JIT_PROFILER_RUNTIME_HEADERS_SOURCE_URI"] = RequiredValue(
            runtimeHeaders.SourceUri,
            "jit-profiler-runtime-headers.sourceUri");
    }

    private static void AddCppCli(
        Dictionary<string, string> environment,
        ReleaseLockDocument releaseLock)
    {
        const string dockerSourcePrefix = "docker://";
        var privateImage = RequiredComponent(releaseLock, "msvc-cppcli-private-image");
        var privateImageDigest = RequiredDigest(
            privateImage.Digest,
            "msvc-cppcli-private-image.digest");
        var privateImageSource = RequiredValue(
            privateImage.SourceUri,
            "msvc-cppcli-private-image.sourceUri");
        if (!privateImageSource.StartsWith(dockerSourcePrefix, StringComparison.Ordinal))
        {
            throw new BakeEnvironmentValidationException(
                "msvc-cppcli-private-image.sourceUri must use docker://repository@sha256:<64 lowercase hex>.");
        }

        var privateImageReference = privateImageSource[dockerSourcePrefix.Length..];
        if (!privateImageReference.EndsWith($"@{privateImageDigest}", StringComparison.Ordinal) ||
            privateImageReference.Length <= privateImageDigest.Length + 1 ||
            privateImageReference.Any(char.IsWhiteSpace))
        {
            throw new BakeEnvironmentValidationException(
                "msvc-cppcli-private-image.sourceUri must be immutable and match its locked digest.");
        }

        var preparedBase = RequiredComponent(releaseLock, "msvc-cppcli-prepared-base");
        var preparedBaseDigest = RequiredDigest(
            preparedBase.Digest,
            "msvc-cppcli-prepared-base.digest");
        var preparedBaseSource = RequiredValue(
            preparedBase.SourceUri,
            "msvc-cppcli-prepared-base.sourceUri");
        if (!preparedBaseSource.StartsWith(dockerSourcePrefix, StringComparison.Ordinal))
        {
            throw new BakeEnvironmentValidationException(
                "msvc-cppcli-prepared-base.sourceUri must use docker://repository@sha256:<64 lowercase hex>.");
        }

        var preparedBaseReference = preparedBaseSource[dockerSourcePrefix.Length..];
        if (!preparedBaseReference.EndsWith($"@{preparedBaseDigest}", StringComparison.Ordinal) ||
            preparedBaseReference.Length <= preparedBaseDigest.Length + 1 ||
            preparedBaseReference.Any(char.IsWhiteSpace))
        {
            throw new BakeEnvironmentValidationException(
                "msvc-cppcli-prepared-base.sourceUri must be immutable and match its locked digest.");
        }

        var toolchain = RequiredComponent(releaseLock, "msvc-cppcli-netfx48");
        var msvcWineSource = RequiredComponent(releaseLock, "msvc-wine-source");
        var referenceSet = RequiredComponent(releaseLock, "netfx48-ref");
        var runtime = RequiredComponent(releaseLock, "wine-netfx48-linux-x64");
        environment["CPPCLI_PRIVATE_IMAGE_VERSION"] = RequiredVersion(
            privateImage,
            "msvc-cppcli-private-image");
        environment["CPPCLI_PRIVATE_IMAGE_DIGEST"] = privateImageDigest;
        environment["CPPCLI_PRIVATE_IMAGE_SOURCE_URI"] = privateImageSource;
        environment["CPPCLI_PREPARED_BASE_IMAGE"] = preparedBaseReference;
        environment["CPPCLI_PREPARED_BASE_VERSION"] = RequiredVersion(
            preparedBase,
            "msvc-cppcli-prepared-base");
        environment["CPPCLI_PREPARED_BASE_DIGEST"] = preparedBaseDigest;
        environment["CPPCLI_PREPARED_BASE_SOURCE_URI"] = preparedBaseSource;
        environment["CPPCLI_COMPILER_VERSION"] = RequiredVersion(toolchain, "msvc-cppcli-netfx48");
        environment["CPPCLI_TOOLCHAIN_DIGEST"] = RequiredDigest(
            toolchain.Digest,
            "msvc-cppcli-netfx48.digest");
        environment["CPPCLI_TOOLCHAIN_SOURCE_URI"] = RequiredValue(
            toolchain.SourceUri,
            "msvc-cppcli-netfx48.sourceUri");
        environment["MSVC_WINE_SOURCE_VERSION"] = RequiredVersion(msvcWineSource, "msvc-wine-source");
        environment["MSVC_WINE_SOURCE_COMMIT"] = RequiredValue(
            msvcWineSource.Commit,
            "msvc-wine-source.commit");
        environment["MSVC_WINE_SOURCE_DIGEST"] = RequiredDigest(
            msvcWineSource.Digest,
            "msvc-wine-source.digest");
        environment["MSVC_WINE_SOURCE_URI"] = RequiredValue(
            msvcWineSource.SourceUri,
            "msvc-wine-source.sourceUri");
        environment["NETFX48_REFERENCE_VERSION"] = RequiredVersion(referenceSet, "netfx48-ref");
        environment["NETFX48_REFERENCE_DIGEST"] = RequiredDigest(
            referenceSet.Digest,
            "netfx48-ref.digest");
        environment["NETFX48_REFERENCE_SOURCE_URI"] = RequiredValue(
            referenceSet.SourceUri,
            "netfx48-ref.sourceUri");
        environment["WINE_NETFX48_RUNTIME_VERSION"] = RequiredVersion(
            runtime,
            "wine-netfx48-linux-x64");
        environment["WINE_NETFX48_RUNTIME_DIGEST"] = RequiredDigest(
            runtime.Digest,
            "wine-netfx48-linux-x64.digest");
        environment["WINE_NETFX48_RUNTIME_SOURCE_URI"] = RequiredValue(
            runtime.SourceUri,
            "wine-netfx48-linux-x64.sourceUri");
    }

    private static void AddJSharp(
        Dictionary<string, string> environment,
        ReleaseLockDocument releaseLock)
    {
        const string dockerSourcePrefix = "docker://";
        var operatorImage = RequiredComponent(releaseLock, "jsharp20");
        var operatorDigest = RequiredDigest(operatorImage.Digest, "jsharp20.digest");
        var operatorSource = RequiredValue(operatorImage.SourceUri, "jsharp20.sourceUri");
        if (!operatorSource.StartsWith(dockerSourcePrefix, StringComparison.Ordinal))
        {
            throw new BakeEnvironmentValidationException(
                "jsharp20.sourceUri must use docker://repository@sha256:<64 lowercase hex>.");
        }

        var operatorReference = operatorSource[dockerSourcePrefix.Length..];
        if (!operatorReference.EndsWith($"@{operatorDigest}", StringComparison.Ordinal) ||
            operatorReference.Length <= operatorDigest.Length + 1 ||
            operatorReference.Any(char.IsWhiteSpace))
        {
            throw new BakeEnvironmentValidationException(
                "jsharp20.sourceUri must be immutable and match its locked digest.");
        }

        var preparedBase = RequiredComponent(releaseLock, "jsharp20-prepared-base");
        var preparedBaseDigest = RequiredDigest(
            preparedBase.Digest,
            "jsharp20-prepared-base.digest");
        var preparedBaseSource = RequiredValue(
            preparedBase.SourceUri,
            "jsharp20-prepared-base.sourceUri");
        if (!preparedBaseSource.StartsWith(dockerSourcePrefix, StringComparison.Ordinal))
        {
            throw new BakeEnvironmentValidationException(
                "jsharp20-prepared-base.sourceUri must use docker://repository@sha256:<64 lowercase hex>.");
        }

        var preparedBaseReference = preparedBaseSource[dockerSourcePrefix.Length..];
        if (!preparedBaseReference.EndsWith($"@{preparedBaseDigest}", StringComparison.Ordinal) ||
            preparedBaseReference.Length <= preparedBaseDigest.Length + 1 ||
            preparedBaseReference.Any(char.IsWhiteSpace))
        {
            throw new BakeEnvironmentValidationException(
                "jsharp20-prepared-base.sourceUri must be immutable and match its locked digest.");
        }

        var compiler = RequiredComponent(releaseLock, "vjc-jsharp20");
        var referenceSet = RequiredComponent(releaseLock, "jsharp20-ref");
        var runtime = RequiredComponent(releaseLock, "wine-jsharp20-linux-x64");
        RequireEqual(runtime.Digest, operatorDigest, "J# runtime operator-image digest");
        RequireEqual(runtime.SourceUri, operatorSource, "J# runtime operator-image source");
        RequireEqual(referenceSet.SourceUri, operatorSource, "J# reference-set operator-image source");

        environment["JSHARP_TOOLCHAIN_IMAGE"] = operatorReference;
        environment["JSHARP_WINE_BASE_IMAGE"] = preparedBaseReference;
        environment["JSHARP_WINE_BASE_VERSION"] = RequiredVersion(
            preparedBase,
            "jsharp20-prepared-base");
        environment["JSHARP_WINE_BASE_DIGEST"] = preparedBaseDigest;
        environment["JSHARP_WINE_BASE_SOURCE_URI"] = preparedBaseSource;
        environment["JSHARP_TOOLCHAIN_VERSION"] = RequiredVersion(operatorImage, "jsharp20");
        environment["JSHARP_COMPILER_VERSION"] = RequiredVersion(compiler, "vjc-jsharp20");
        environment["JSHARP_TOOLCHAIN_DIGEST"] = operatorDigest;
        environment["JSHARP_TOOLCHAIN_SOURCE_URI"] = operatorSource;
        environment["JSHARP_REFERENCE_VERSION"] = RequiredVersion(referenceSet, "jsharp20-ref");
        environment["JSHARP_REFERENCE_DIGEST"] = RequiredDigest(
            referenceSet.Digest,
            "jsharp20-ref.digest");
        environment["JSHARP_REFERENCE_SOURCE_URI"] = RequiredValue(
            referenceSet.SourceUri,
            "jsharp20-ref.sourceUri");
        environment["WINE_JSHARP20_RUNTIME_VERSION"] = RequiredVersion(
            runtime,
            "wine-jsharp20-linux-x64");
        environment["WINE_JSHARP20_RUNTIME_DIGEST"] = RequiredDigest(
            runtime.Digest,
            "wine-jsharp20-linux-x64.digest");
        environment["WINE_JSHARP20_RUNTIME_SOURCE_URI"] = RequiredValue(
            runtime.SourceUri,
            "wine-jsharp20-linux-x64.sourceUri");
    }

    private static void AddPackage(
        Dictionary<string, string> environment,
        LockedComponent component,
        string componentId,
        string versionVariable,
        string sourceVariable)
    {
        environment[versionVariable] = RequiredVersion(component, componentId);
        environment[sourceVariable] = RequiredValue(component.SourceUri, $"{componentId}.sourceUri");
    }

    private static void AddILSense(
        Dictionary<string, string> environment,
        ReleaseLockDocument releaseLock)
    {
        var component = RequiredComponent(releaseLock, "ilsense");
        var source = RequiredComponent(releaseLock, "ilsense-source");
        RequireEqual(component.ResolvedVersion, source.ResolvedVersion, "ILSense source version");
        RequireEqual(component.Commit, source.Commit, "ILSense source commit");
        RequireEqual(component.Digest, source.Digest, "ILSense source digest");
        environment["ILSENSE_VERSION"] = RequiredVersion(component, "ilsense");
        environment["ILSENSE_COMMIT"] = RequiredValue(source.Commit, "ilsense-source.commit");
        environment["ILSENSE_ARCHIVE_URL"] = RequiredValue(source.SourceUri, "ilsense-source.sourceUri");
        environment["ILSENSE_ARCHIVE_SHA256"] = DigestHex(source.Digest, "ilsense-source.digest");
        environment["ILSENSE_SOURCE_URI"] = RequiredValue(component.SourceUri, "ilsense.sourceUri");
    }

    private static void AddReferenceSet(
        Dictionary<string, string> environment,
        ReleaseLockDocument releaseLock,
        string componentId,
        string versionVariable,
        string variablePrefix)
    {
        var component = RequiredComponent(releaseLock, componentId);
        var sourceUri = RequiredValue(component.SourceUri, $"{componentId}.sourceUri");
        environment[versionVariable] = RequiredVersion(component, componentId);
        environment[$"{variablePrefix}_REFERENCE_URL"] = sourceUri;
        environment[$"{variablePrefix}_REFERENCE_SOURCE_URI"] = sourceUri;
        environment[$"{variablePrefix}_REFERENCE_SHA512"] = RequiredValue(
            component.Sha512,
            $"{componentId}.sha512");
        environment[$"{variablePrefix}_REFERENCE_PACKAGE_CONTENT_HASH"] = RequiredValue(
            component.PackageContentHash,
            $"{componentId}.packageContentHash");
    }

    private static void AddFrameworkManagedReferenceDigests(
        Dictionary<string, string> environment,
        ReleaseLockDocument releaseLock)
    {
        foreach (var (target, prefix, hasSource) in new (string Target, string Prefix, bool HasSource)[]
        {
            ("netfx20-managed-ref", "NETFX20_MANAGED_REFERENCE", true),
            ("netfx30-managed-ref", "NETFX30_MANAGED_REFERENCE", false),
            ("netfx35-managed-ref", "NETFX35_MANAGED_REFERENCE", true),
            ("netfx40-managed-ref", "NETFX40_MANAGED_REFERENCE", true),
            ("netfx45-managed-ref", "NETFX45_MANAGED_REFERENCE", true),
            ("netfx451-managed-ref", "NETFX451_MANAGED_REFERENCE", true),
            ("netfx452-managed-ref", "NETFX452_MANAGED_REFERENCE", true),
            ("netfx46-managed-ref", "NETFX46_MANAGED_REFERENCE", true),
            ("netfx461-managed-ref", "NETFX461_MANAGED_REFERENCE", true),
            ("netfx462-managed-ref", "NETFX462_MANAGED_REFERENCE", true),
            ("netfx47-managed-ref", "NETFX47_MANAGED_REFERENCE", true),
            ("netfx471-managed-ref", "NETFX471_MANAGED_REFERENCE", true),
            ("netfx472-managed-ref", "NETFX472_MANAGED_REFERENCE", true),
            ("netfx48-managed-ref", "NETFX48_MANAGED_REFERENCE", true)
        })
        {
            var component = RequiredComponent(releaseLock, target);
            environment[$"{prefix}_VERSION"] = RequiredVersion(component, target);
            if (hasSource)
            {
                environment[$"{prefix}_SOURCE_URI"] = RequiredValue(
                    component.SourceUri,
                    $"{target}.sourceUri");
            }
            environment[$"{prefix}_DIGEST"] = ReferenceSetIdentityResolver.ResolveLockedDigest(component, target);
        }
    }

    private static void AddGSharp(
        Dictionary<string, string> environment,
        ReleaseLockDocument releaseLock)
    {
        var toolchain = RequiredComponent(releaseLock, "gsharp-stable");
        var source = RequiredComponent(releaseLock, "gsharp-source");
        RequireEqual(toolchain.ResolvedVersion, source.ResolvedVersion, "G# source version");
        RequireEqual(toolchain.Commit, source.Commit, "G# source commit");
        RequireEqual(toolchain.Digest, source.Digest, "G# source digest");
        environment["GSHARP_VERSION"] = RequiredVersion(toolchain, "gsharp-stable");
        environment["GSHARP_COMMIT"] = RequiredValue(source.Commit, "gsharp-source.commit");
        environment["GSHARP_ARCHIVE_URL"] = RequiredValue(source.SourceUri, "gsharp-source.sourceUri");
        environment["GSHARP_ARCHIVE_SHA256"] = DigestHex(source.Digest, "gsharp-source.digest");
        environment["GSHARP_SOURCE_URI"] = RequiredValue(toolchain.SourceUri, "gsharp-stable.sourceUri");

        var legacyToolchain = RequiredComponent(releaseLock, "gsharp-legacy-0.3.8");
        var legacySource = RequiredComponent(releaseLock, "gsharp-legacy-0.3.8-source");
        RequireEqual(legacyToolchain.ResolvedVersion, legacySource.ResolvedVersion, "G# legacy source version");
        RequireEqual(legacyToolchain.Commit, legacySource.Commit, "G# legacy source commit");
        RequireEqual(legacyToolchain.Digest, legacySource.Digest, "G# legacy source digest");
        environment["GSHARP_LEGACY_VERSION"] = RequiredVersion(legacyToolchain, "gsharp-legacy-0.3.8");
        environment["GSHARP_LEGACY_COMMIT"] = RequiredValue(legacySource.Commit, "gsharp-legacy-0.3.8-source.commit");
        environment["GSHARP_LEGACY_ARCHIVE_URL"] = RequiredValue(legacySource.SourceUri, "gsharp-legacy-0.3.8-source.sourceUri");
        environment["GSHARP_LEGACY_ARCHIVE_SHA256"] = DigestHex(legacySource.Digest, "gsharp-legacy-0.3.8-source.digest");
        environment["GSHARP_LEGACY_SOURCE_URI"] = RequiredValue(legacyToolchain.SourceUri, "gsharp-legacy-0.3.8.sourceUri");
    }

    private static void AddPeachPie(
        Dictionary<string, string> environment,
        ReleaseLockDocument releaseLock)
    {
        const string commitIdentity = "PeachPie package commit";
        var codeAnalysis = RequiredComponent(releaseLock, "peachpie-stable");
        var runtime = RequiredComponent(releaseLock, "peachpie-runtime");
        var library = RequiredComponent(releaseLock, "peachpie-library");
        RequireEqual(codeAnalysis.ResolvedVersion, runtime.ResolvedVersion, "PeachPie runtime version");
        RequireEqual(codeAnalysis.ResolvedVersion, library.ResolvedVersion, "PeachPie library version");
        RequireEqual(codeAnalysis.Commit, runtime.Commit, commitIdentity);
        RequireEqual(codeAnalysis.Commit, library.Commit, commitIdentity);

        AddPeachPiePackage(environment, codeAnalysis, "peachpie-stable", "PEACHPIE_CODEANALYSIS");
        AddPeachPiePackage(environment, runtime, "peachpie-runtime", "PEACHPIE_RUNTIME");
        AddPeachPiePackage(environment, library, "peachpie-library", "PEACHPIE_LIBRARY");
        var commit = RequiredValue(codeAnalysis.Commit, "peachpie-stable.commit");
        environment["PEACHPIE_COMMIT"] = commit;
        environment["PEACHPIE_LICENSE_URL"] =
            $"https://raw.githubusercontent.com/peachpiecompiler/peachpie/{commit}/LICENSE.txt";
    }

    private static void AddJsil(
        Dictionary<string, string> environment,
        ReleaseLockDocument releaseLock)
    {
        var processor = RequiredComponent(releaseLock, "artifacts-jsil");
        var source = AddJsilSource(environment, releaseLock, "jsil-source", "JSIL");
        RequireEqual(processor.Commit, source.Commit, "JSIL processor source commit");
        RequireEqual(processor.Digest, source.Digest, "JSIL processor source digest");
        environment["ARTIFACTS_JSIL_VERSION"] = RequiredVersion(processor, "artifacts-jsil");
        environment["ARTIFACTS_JSIL_COMMIT"] = RequiredValue(processor.Commit, "artifacts-jsil.commit");
        environment["ARTIFACTS_JSIL_DIGEST"] = RequiredValue(processor.Digest, "artifacts-jsil.digest");
        environment["ARTIFACTS_JSIL_SOURCE_URI"] = RequiredValue(
            processor.SourceUri,
            "artifacts-jsil.sourceUri");
        environment["JSIL_VERSION"] = RequiredVersion(source, "jsil-source");
        AddJsilSource(environment, releaseLock, "jsil-meta-source", "JSIL_META");
        AddJsilSource(environment, releaseLock, "jsil-ilspy-source", "JSIL_ILSPY");
        AddJsilSource(environment, releaseLock, "jsil-nrefactory-source", "JSIL_NREFACTORY");
        AddJsilSource(environment, releaseLock, "jsil-cecil-source", "JSIL_CECIL");
    }

    private static LockedComponent AddJsilSource(
        Dictionary<string, string> environment,
        ReleaseLockDocument releaseLock,
        string componentId,
        string variablePrefix)
    {
        var component = RequiredComponent(releaseLock, componentId);
        environment[$"{variablePrefix}_VERSION"] = RequiredVersion(component, componentId);
        environment[$"{variablePrefix}_COMMIT"] = RequiredValue(component.Commit, $"{componentId}.commit");
        environment[$"{variablePrefix}_ARCHIVE_URL"] = RequiredValue(component.SourceUri, $"{componentId}.sourceUri");
        environment[$"{variablePrefix}_ARCHIVE_SHA256"] = DigestHex(component.Digest, $"{componentId}.digest");
        return component;
    }

    private static void AddPeachPiePackage(
        Dictionary<string, string> environment,
        LockedComponent component,
        string componentId,
        string variablePrefix)
    {
        var sourceUri = RequiredValue(component.SourceUri, $"{componentId}.sourceUri");
        environment[$"{variablePrefix}_VERSION"] = RequiredVersion(component, componentId);
        environment[$"{variablePrefix}_URL"] = sourceUri;
        environment[$"{variablePrefix}_SOURCE_URI"] = sourceUri;
        environment[$"{variablePrefix}_SHA512"] = RequiredValue(component.Sha512, $"{componentId}.sha512");
        environment[$"{variablePrefix}_PACKAGE_CONTENT_HASH"] = RequiredValue(
            component.PackageContentHash,
            $"{componentId}.packageContentHash");
    }

    private static void AddRuntime(
        Dictionary<string, string> environment,
        ReleaseLockDocument releaseLock,
        string componentId,
        string variablePrefix)
    {
        var component = RequiredComponent(releaseLock, componentId);
        var sourceUri = RequiredValue(component.SourceUri, $"{componentId}.sourceUri");
        environment[$"{variablePrefix}_RUNTIME_VERSION"] = RequiredVersion(component, componentId);
        environment[$"{variablePrefix}_RUNTIME_COMMIT"] = RequiredValue(component.Commit, $"{componentId}.commit");
        environment[$"{variablePrefix}_JIT_COMMIT"] = RequiredValue(component.JitCommit, $"{componentId}.jitCommit");
        environment[$"{variablePrefix}_RUNTIME_URL"] = sourceUri;
        environment[$"{variablePrefix}_RUNTIME_SOURCE_URI"] = sourceUri;
        environment[$"{variablePrefix}_RUNTIME_SHA512"] = RequiredValue(component.Sha512, $"{componentId}.sha512");
    }

    private static void AddConstGenerics(
        Dictionary<string, string> environment,
        ReleaseLockDocument releaseLock)
    {
        var runtimeSource = RequiredComponent(releaseLock, "const-generics-runtime-source");
        var runtime = RequiredComponent(releaseLock, "const-generics-linux-x64");
        var reference = RequiredComponent(releaseLock, "const-generics-ref");
        RequireEqual(runtimeSource.Commit, runtime.Commit, "const-generics runtime commit");
        RequireEqual(runtimeSource.Commit, runtime.JitCommit, "const-generics JIT commit");
        RequireEqual(runtimeSource.Commit, reference.Commit, "const-generics reference commit");
        RequireEqual(runtimeSource.Digest, reference.Digest, "const-generics reference digest");
        environment["CONST_GENERICS_RUNTIME_VERSION"] = RequiredVersion(runtime, "const-generics-linux-x64");
        environment["CONST_GENERICS_RUNTIME_COMMIT"] = RequiredValue(
            runtimeSource.Commit,
            "const-generics-runtime-source.commit");
        environment["CONST_GENERICS_RUNTIME_ARCHIVE_URL"] = RequiredValue(
            runtimeSource.SourceUri,
            "const-generics-runtime-source.sourceUri");
        environment["CONST_GENERICS_RUNTIME_ARCHIVE_SHA256"] = DigestHex(
            runtimeSource.Digest,
            "const-generics-runtime-source.digest");
        environment["CONST_GENERICS_RUNTIME_SOURCE_URI"] = RequiredValue(
            runtime.SourceUri,
            "const-generics-linux-x64.sourceUri");
        environment["CONST_GENERICS_REFERENCE_VERSION"] = RequiredVersion(reference, "const-generics-ref");
        environment["CONST_GENERICS_REFERENCE_DIGEST"] = RequiredDigest(
            reference.Digest,
            "const-generics-ref.digest");

        var versionTools = RequiredComponent(releaseLock, "const-generics-versiontools");
        environment["CONST_GENERICS_VERSIONTOOLS_VERSION"] = RequiredVersion(
            versionTools,
            "const-generics-versiontools");
        environment["CONST_GENERICS_VERSIONTOOLS_PACKAGE_SHA256"] = DigestHex(
            versionTools.Digest,
            "const-generics-versiontools.digest");
        environment["CONST_GENERICS_VERSIONTOOLS_SOURCE_URI"] = RequiredValue(
            versionTools.SourceUri,
            "const-generics-versiontools.sourceUri");

        var roslynSource = RequiredComponent(releaseLock, "const-generics-roslyn-source");
        var roslyn = RequiredComponent(releaseLock, "roslyn-const-generics");
        RequireEqual(roslynSource.Commit, roslyn.Commit, "const-generics Roslyn commit");
        RequireEqual(roslynSource.Digest, roslyn.Digest, "const-generics Roslyn digest");
        environment["CONST_GENERICS_ROSLYN_COMMIT"] = RequiredValue(
            roslynSource.Commit,
            "const-generics-roslyn-source.commit");
        environment["CONST_GENERICS_ROSLYN_ARCHIVE_URL"] = RequiredValue(
            roslynSource.SourceUri,
            "const-generics-roslyn-source.sourceUri");
        environment["CONST_GENERICS_ROSLYN_ARCHIVE_SHA256"] = DigestHex(
            roslynSource.Digest,
            "const-generics-roslyn-source.digest");
        var roslynComponentVersion = RequiredVersion(roslyn, "roslyn-const-generics");
        environment["CONST_GENERICS_ROSLYN_VERSION"] = ConstGenericsCompilerVersion(roslynComponentVersion);
        environment["CONST_GENERICS_ROSLYN_COMPONENT_VERSION"] = roslynComponentVersion;
        environment["CONST_GENERICS_ROSLYN_SOURCE_URI"] = RequiredValue(
            roslyn.SourceUri,
            "roslyn-const-generics.sourceUri");

        var ilspySource = RequiredComponent(releaseLock, "const-generics-ilspy-source");
        var artifacts = RequiredComponent(releaseLock, "artifacts-const-generics");
        RequireEqual(ilspySource.Commit, artifacts.Commit, "const-generics ILSpy commit");
        RequireEqual(ilspySource.Digest, artifacts.Digest, "const-generics ILSpy digest");
        environment["CONST_GENERICS_ILSPY_COMMIT"] = RequiredValue(
            ilspySource.Commit,
            "const-generics-ilspy-source.commit");
        environment["CONST_GENERICS_ILSPY_ARCHIVE_URL"] = RequiredValue(
            ilspySource.SourceUri,
            "const-generics-ilspy-source.sourceUri");
        environment["CONST_GENERICS_ILSPY_ARCHIVE_SHA256"] = DigestHex(
            ilspySource.Digest,
            "const-generics-ilspy-source.digest");
        environment["CONST_GENERICS_ILSPY_SOURCE_URI"] = RequiredValue(
            artifacts.SourceUri,
            "artifacts-const-generics.sourceUri");
    }

    private static void AddBaseImages(
        Dictionary<string, string> environment,
        string baseImageManifestPath)
    {
        var fullPath = Path.GetFullPath(RequiredValue(baseImageManifestPath, "baseImageManifestPath"));
        BaseImageManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<BaseImageManifest>(
                File.ReadAllText(fullPath),
                BaseImageJsonOptions)
                ?? throw new BakeEnvironmentValidationException("Base image manifest is empty.");
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            throw new BakeEnvironmentValidationException(
                $"Could not load base image manifest '{fullPath}': {exception.Message}",
                exception);
        }

        if (manifest.SchemaVersion != 1)
        {
            throw new BakeEnvironmentValidationException(
                $"Unsupported base image manifest schema version {manifest.SchemaVersion}.");
        }

        if (manifest.Images is null)
            throw new BakeEnvironmentValidationException("Base image manifest images are required.");

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenVariables = new HashSet<string>(StringComparer.Ordinal);
        foreach (var image in manifest.Images)
        {
            var id = RequiredValue(image.Id, "base-images.images[].id");
            var variable = RequiredValue(image.BakeVariable, $"base-images.images[{id}].bakeVariable");
            var reference = RequiredValue(image.Reference, $"base-images.images[{id}].reference");
            if (!seenIds.Add(id))
                throw new BakeEnvironmentValidationException($"Base image id '{id}' is duplicated.");
            if (!seenVariables.Add(variable))
                throw new BakeEnvironmentValidationException($"Base image Bake variable '{variable}' is duplicated.");
            if (!RequiredBaseImages.TryGetValue(id, out var expectedVariable))
                throw new BakeEnvironmentValidationException($"Unknown base image id '{id}'.");
            if (!string.Equals(variable, expectedVariable, StringComparison.Ordinal))
            {
                throw new BakeEnvironmentValidationException(
                    $"Base image '{id}' must use Bake variable '{expectedVariable}', not '{variable}'.");
            }
            ValidateBaseImageReference(reference, id);
            environment[variable] = reference;
        }

        foreach (var expected in RequiredBaseImages)
        {
            if (!seenIds.Contains(expected.Key))
                throw new BakeEnvironmentValidationException($"Base image manifest is missing '{expected.Key}'.");
        }
    }

    private static void ValidateBaseImageReference(string reference, string id)
    {
        if (reference.Any(char.IsWhiteSpace))
        {
            throw new BakeEnvironmentValidationException(
                $"Base image '{id}' reference must not contain whitespace.");
        }
        var marker = reference.LastIndexOf(BaseImageDigestMarker, StringComparison.Ordinal);
        var repository = marker > 0 ? reference[..marker] : string.Empty;
        if (marker <= 0 ||
            marker + BaseImageDigestMarker.Length + 64 != reference.Length ||
            repository.Contains("://", StringComparison.Ordinal) ||
            repository.Contains('@', StringComparison.Ordinal))
        {
            throw new BakeEnvironmentValidationException(
                $"Base image '{id}' reference must be repository[:tag]@sha256:<64 lowercase hex>.");
        }
        foreach (var character in reference.AsSpan(marker + BaseImageDigestMarker.Length))
        {
            if (!char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character))
            {
                throw new BakeEnvironmentValidationException(
                    $"Base image '{id}' reference must be repository[:tag]@sha256:<64 lowercase hex>.");
            }
        }
    }

    private static LockedComponent RequiredComponent(ReleaseLockDocument document, string componentId) =>
        document.Components.TryGetValue(componentId, out var component)
            ? component
            : throw new BakeEnvironmentValidationException(
                $"Release lock is missing required Bake component '{componentId}'.");

    private static string RequiredVersion(LockedComponent component, string componentId) =>
        RequiredValue(component.ResolvedVersion, $"{componentId}.resolvedVersion");

    private static string RequiredDigest(string? digest, string field)
    {
        _ = DigestHex(digest, field);
        return digest!;
    }

    private static string DigestHex(string? digest, string field)
    {
        var value = RequiredValue(digest, field);
        if (!value.StartsWith("sha256:", StringComparison.Ordinal) || value.Length != 71)
            throw new BakeEnvironmentValidationException($"Release lock field '{field}' is not a SHA-256 digest.");
        foreach (var character in value.AsSpan(7))
        {
            if (!char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character))
                throw new BakeEnvironmentValidationException($"Release lock field '{field}' is not a SHA-256 digest.");
        }
        return value[7..];
    }

    private static string ConstGenericsCompilerVersion(string resolvedVersion)
    {
        const string marker = "-const-generics.";
        var markerIndex = resolvedVersion.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex <= 0)
        {
            throw new BakeEnvironmentValidationException(
                "roslyn-const-generics.resolvedVersion must contain '-const-generics.' for Bake compiler version derivation.");
        }
        return resolvedVersion[..markerIndex];
    }

    private static void RequireEqual(string? left, string? right, string identity)
    {
        if (!string.Equals(left, right, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(left))
            throw new BakeEnvironmentValidationException($"Release lock has inconsistent {identity} values.");
    }

    private static string RequiredValue(string? value, string field) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new BakeEnvironmentValidationException(
                $"Release lock/base image field '{field}' is required for Bake.");

    private static string ValidateControlRuntimeTargetFramework(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 32 ||
            !value.StartsWith("net", StringComparison.Ordinal) ||
            value[3..].Length == 0 ||
            value[3..].Any(static character =>
                !char.IsAsciiDigit(character) && character != '.'))
        {
            throw new BakeEnvironmentValidationException(
                $"Control runtime target framework '{value}' is invalid.");
        }
        return value;
    }

    private sealed record BaseImageManifest
    {
        public required int SchemaVersion { get; init; }
        public required IReadOnlyList<BaseImageEntry> Images { get; init; }
    }

    private sealed record BaseImageEntry
    {
        public required string Id { get; init; }
        public required string BakeVariable { get; init; }
        public required string Reference { get; init; }
    }
}

public sealed class BakeEnvironmentValidationException : Exception
{
    public BakeEnvironmentValidationException(string message)
        : base(message)
    {
    }

    public BakeEnvironmentValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
