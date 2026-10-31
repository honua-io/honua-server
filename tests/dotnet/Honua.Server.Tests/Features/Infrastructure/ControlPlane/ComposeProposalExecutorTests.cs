// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.ControlPlane;
using Honua.ControlPlane.Executors;
using Honua.Core.Exceptions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Migrations;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Fail-closed contract for the Compose proposal executor. These tests do not start Docker.
/// </summary>
public sealed class ComposeProposalExecutorTests
{
    private static string Digest(char hex) => "sha256:" + new string(hex, 64);

    [UnitTest]
    public void ExtraProjectOrCommand_IsScopeViolation_AndDoesNotParse()
    {
        Assert.False(ComposeExecutorProtocol.TryReadRequest(
            "{\"op\":\"status\",\"project\":\"other\"}",
            out _,
            out var projectFailure));
        Assert.Equal(ComposeExecutorCodes.ScopeViolation, projectFailure!.Code);

        Assert.False(ComposeExecutorProtocol.TryReadRequest(
            "{\"op\":\"status\",\"command\":\"rm\"}",
            out _,
            out var commandFailure));
        Assert.Equal(ComposeExecutorCodes.ScopeViolation, commandFailure!.Code);
    }

    [UnitTest]
    public async Task ChecksumMismatch_DoesNotCallDocker()
    {
        var root = Path.Combine(Path.GetTempPath(), "honua-compose-unit", Guid.NewGuid().ToString("N"));
        var release = Digest('a');
        var prior = Digest('b');
        var options = ValidOptions(root, release);
        var backupId = "backup-" + new string('c', 32);
        var directory = Path.Combine(options.BackupDirectory, backupId);
        Directory.CreateDirectory(directory);
        var dump = Path.Combine(directory, ComposeExecutorProtocol.DumpFileName);
        await File.WriteAllTextAsync(dump, "not-the-hashed-bytes");
        var manifest = new ComposeBackupManifest
        {
            BackupId = backupId,
            Project = options.ProjectName,
            Service = options.ServiceName,
            Image = prior,
            SchemaVersion = 141,
            Sha256 = new string('d', 64),
            CreatedAt = DateTimeOffset.UtcNow,
            DumpFile = ComposeExecutorProtocol.DumpFileName
        };
        await File.WriteAllTextAsync(
            Path.Combine(directory, "manifest.json"),
            System.Text.Json.JsonSerializer.Serialize(manifest, ComposeExecutorJsonContext.Default.ComposeBackupManifest));

        var docker = new RecordingDocker();
        using var controller = new ComposeProjectController(options, docker);
        var response = await controller.ExecuteAsync(new ComposeExecutorRequest
        {
            Op = "rollback",
            Image = prior,
            BackupId = backupId,
            RequestId = "checksum"
        });

        Assert.Empty(docker.Argv);
        Assert.False(response.Ok);
        Assert.False(response.ReadbackComplete);
        Assert.Equal(ComposeExecutorCodes.BackupChecksumMismatch, response.Code);
    }

    [UnitTest]
    public async Task Plan_BlocksScopeImageAndConfig_BeforeCallingTheExecutor()
    {
        var client = new RecordingClient();
        var backend = Backend(client, Digest('a'), socketPath: "/tmp/honua-compose-unit.sock");

        var scope = await backend.PlanAsync(Spec(Digest('a'), new Dictionary<string, string> { ["compose.project"] = "other" }));
        var image = await backend.PlanAsync(Spec("honua:latest", new Dictionary<string, string>()));
        var config = await backend.PlanAsync(Spec(Digest('a'), new Dictionary<string, string> { ["telemetry.healthz.url"] = "http://127.0.0.1/healthz" }));
        var unconfigured = Backend(client, releaseImage: string.Empty, socketPath: string.Empty);
        var missing = await unconfigured.PlanAsync(Spec(Digest('a'), new Dictionary<string, string>()));

        Assert.Contains(ComposeExecutorCodes.ScopeViolation, scope.BlockingReasons);
        Assert.Contains(ComposeExecutorCodes.ImageNotImmutable, image.BlockingReasons);
        Assert.Contains(ComposeExecutorCodes.ConfigUnsupported, config.BlockingReasons);
        Assert.Contains(ComposeExecutorCodes.ExecutorUnconfigured, missing.BlockingReasons);
        Assert.Empty(client.Ops);
    }

    [UnitTest]
    public async Task StartWithoutApproval_DoesNotConverge()
    {
        var client = new RecordingClient { Response = Ready(Digest('a')) };
        var backend = Backend(client, Digest('a'), "/tmp/honua-compose-unit.sock");
        var result = await backend.StartAsync(Operation(Digest('a'), approvedBy: null));

        Assert.Equal(WorkflowOperationStatus.Failed, result.Status);
        Assert.Equal(ComposeExecutorCodes.ApprovalRequired, result.Message);
        Assert.Empty(client.Ops);
    }

    [UnitTest]
    public async Task SamePrincipalSubmit_ThrowsBeforeConverge_AndADifferentPrincipalConverges()
    {
        var release = Digest('a');
        var prior = Digest('b');
        var client = new RecordingClient { Response = Ready(release) };
        var backend = Backend(client, release, "/tmp/honua-compose-unit.sock");
        var targets = new MemoryTargets(new DeployTargetDefinition
        {
            TargetId = "compose-local",
            TargetKind = DeployTargetKind.LocalCompose,
            Backend = ComposeDeployBackend.AdapterBackendName,
            Environment = "local",
            TargetName = "honua",
            ArtifactReference = release,
            RequiresApproval = true
        });
        var store = new MemoryWorkflows();
        var service = new DeployWorkflowService(
            targets,
            [store],
            [backend],
            new NotRequiredApproval(),
            NullLogger<DeployWorkflowService>.Instance);

        var created = await service.CreateAsync(
            "compose-local",
            release,
            prior,
            "agent",
            "converge",
            "compose-unit-create",
            "corr",
            OperationPriority.Normal,
            submitImmediately: true,
            parameterOverrides: null);

        Assert.NotNull(created);
        Assert.Equal(WorkflowOperationStatus.AwaitingApproval, created.Status);
        Assert.Equal(["status"], client.Ops);

        var same = await Assert.ThrowsAsync<ResourceConflictException>(() =>
            service.SubmitAsync(created.OperationId, "agent", "self", CancellationToken.None));
        Assert.Contains("Separation of duties", same.Message, StringComparison.Ordinal);
        Assert.Equal(["status"], client.Ops);

        var submitted = await service.SubmitAsync(created.OperationId, "approver", "approved", CancellationToken.None);
        Assert.NotNull(submitted);
        Assert.Equal(WorkflowOperationStatus.Reconciling, submitted.Status);
        Assert.Equal(prior, submitted.Deploy!.CurrentRevision);
        Assert.Equal(["status", "converge"], client.Ops);
    }

    [UnitTest]
    public async Task ApplyConfig_AndRestartScope_FailBeforeTheExecutor()
    {
        var client = new RecordingClient { Response = Ready(Digest('a')) };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IComposeExecutorClient>(client);
        using var provider = services.BuildServiceProvider();
        var applier = new OpsActionAdminConfigChangeApplier(
            provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<OpsActionAdminConfigChangeApplier>.Instance);

        var config = await Assert.ThrowsAsync<OpsActionException>(() =>
            applier.ApplyAsync("{\"action\":\"compose.apply_config\"}"));
        var scope = await Assert.ThrowsAsync<OpsActionException>(() =>
            applier.ApplyAsync("{\"action\":\"compose.restart_service\",\"target\":\"other\"}"));
        var parameters = await Assert.ThrowsAsync<OpsActionException>(() =>
            applier.ApplyAsync("{\"action\":\"compose.restart_service\",\"params\":{\"image\":\"sha256:abc\"}}"));

        Assert.Equal(ComposeExecutorCodes.ConfigUnsupported, config.Message);
        Assert.Equal(ComposeExecutorCodes.ScopeViolation, scope.Message);
        Assert.Equal(ComposeExecutorCodes.ConfigUnsupported, parameters.Message);
        Assert.Empty(client.Ops);
    }

    [UnitTest]
    public void QualifiedJournalNames_AndContractMigrations_RequireRestore()
    {
        const string incompatible = "Honua.Server.Migrations.142_CreateAiFinancialAccounting.sql";
        const string contract = "Honua.Server.Migrations.143_Narrow.sql";
        const string expand = "Honua.Server.Migrations.140_CreateAiExecutionAdmission.sql";
        const string newerExpand = "Honua.Server.Migrations.144_Expand.sql";

        Assert.Equal(142, MigrationReaderCompatibility.ReadSchemaNumber(incompatible));
        Assert.Null(ComposeReaderGate.SchemaVersion(["not-a-migration"]));
        Assert.Equal(142, ComposeReaderGate.SchemaVersion([incompatible, "Honua.Server.Migrations.141_Expand.sql"]));

        Assert.True(ComposeReaderGate.Assess(
            [incompatible],
            [new ComposeCompatibilityRow(incompatible, 1, MigrationReaderCompatibilityClassification.ReaderIncompatible, 142)],
            141).Required);
        Assert.True(ComposeReaderGate.Assess(
            [contract],
            [new ComposeCompatibilityRow(contract, 1, MigrationReaderCompatibilityClassification.Contract, 0)],
            141).Required);
        Assert.True(ComposeReaderGate.LabelMissingBlocks(
            [contract],
            [new ComposeCompatibilityRow(contract, 1, MigrationReaderCompatibilityClassification.Contract, 0)]));

        var shipped = ComposeReaderGate.Assess(
            [expand],
            [new ComposeCompatibilityRow(expand, 1, MigrationReaderCompatibilityClassification.Expand, 0)],
            141);
        Assert.False(shipped.Required);
        Assert.Empty(shipped.ExpandScriptsToVerify);

        var ahead = ComposeReaderGate.Assess(
            [newerExpand],
            [new ComposeCompatibilityRow(newerExpand, 1, MigrationReaderCompatibilityClassification.Expand, 0)],
            141);
        Assert.False(ahead.Required);
        Assert.Equal([newerExpand], ahead.ExpandScriptsToVerify);
        Assert.False(ComposeReaderGate.LabelMissingBlocks(
            [expand],
            [new ComposeCompatibilityRow(expand, 1, MigrationReaderCompatibilityClassification.Expand, 0)]));
    }

    [UnitTest]
    public async Task ConfiguredComposeFiles_ArePassedInOrderBeforeTheImageOverride()
    {
        var root = Path.Combine(Path.GetTempPath(), "honua-compose-unit", Guid.NewGuid().ToString("N"));
        var options = ValidOptions(root, Digest('a'));
        var overlay = Path.Combine(options.ProjectDirectory, "compose.executor.yaml");
        var imageOverride = Path.Combine(options.ProjectDirectory, ComposeExecutorProtocol.OverrideFileName);
        await File.WriteAllTextAsync(overlay, "services: {}\n");
        await File.WriteAllTextAsync(imageOverride, "services: {}\n");
        options.ComposeFiles = ["compose.yaml", "compose.executor.yaml"];

        var docker = new RecordingDocker();
        using var controller = new ComposeProjectController(options, docker);
        await controller.ExecuteAsync(new ComposeExecutorRequest { Op = "status", RequestId = "files" });

        Assert.NotEmpty(docker.Argv);
        var files = new List<string>();
        var args = docker.Argv[0];
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (args[index] == "-f")
            {
                files.Add(Path.GetFullPath(args[index + 1]));
            }
        }

        Assert.Equal(
            [
                Path.GetFullPath(Path.Combine(options.ProjectDirectory, "compose.yaml")),
                Path.GetFullPath(overlay),
                Path.GetFullPath(imageOverride)
            ],
            files);
    }

    private static ComposeExecutorOptions ValidOptions(string root, string release)
    {
        var project = Path.Combine(root, "project");
        var backup = Path.Combine(root, "backups");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(project, "compose.yaml"), "services: {}\n");
        return new ComposeExecutorOptions
        {
            ProjectDirectory = project,
            ProjectName = "honua-g44-unit",
            ComposeFile = "compose.yaml",
            BackupDirectory = backup,
            CustomerProbeUrl = "http://127.0.0.1:9/customer",
            HealthProbeUrl = "http://127.0.0.1:9/healthz/ready",
            ReleaseImage = release,
            SocketPath = Path.Combine(root, "executor.sock")
        };
    }

    private static ComposeDeployBackend Backend(RecordingClient client, string releaseImage, string socketPath)
        => new(
            client,
            Options.Create(new ComposeExecutorOptions
            {
                SocketPath = socketPath,
                ReleaseImage = releaseImage
            }),
            NullLogger<ComposeDeployBackend>.Instance);

    private static DeployOperationSpec Spec(string desired, Dictionary<string, string> parameters)
        => new()
        {
            TargetId = "compose-local",
            TargetKind = DeployTargetKind.LocalCompose,
            Backend = ComposeDeployBackend.AdapterBackendName,
            Environment = "local",
            TargetName = "honua",
            DesiredRevision = desired,
            RequiresApproval = true,
            Parameters = parameters
        };

    private static WorkflowOperationRecord Operation(string desired, string? approvedBy)
        => new()
        {
            OperationId = "op-compose",
            Kind = WorkflowOperationKind.Deploy,
            Status = WorkflowOperationStatus.Submitted,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Audit = new OperationAuditInfo { RequestedBy = "agent", ApprovedBy = approvedBy },
            Deploy = new DeployOperationSpec
            {
                TargetId = "compose-local",
                TargetKind = DeployTargetKind.LocalCompose,
                Backend = ComposeDeployBackend.AdapterBackendName,
                Environment = "local",
                TargetName = "honua",
                CurrentRevision = Digest('b'),
                DesiredRevision = desired
            }
        };

    private static ComposeExecutorResponse Ready(string image)
        => new()
        {
            Ok = true,
            ReadbackComplete = true,
            RunningImage = image,
            SchemaVersion = 141,
            Message = "Readback completed."
        };

    private sealed class RecordingDocker : IDockerComposeProcess
    {
        public List<string[]> Argv { get; } = [];

        public Task<DockerProcessResult> RunAsync(
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Argv.Add(arguments.ToArray());
            return Task.FromResult(new DockerProcessResult(0, string.Empty, string.Empty));
        }
    }

    private sealed class RecordingClient : IComposeExecutorClient
    {
        public List<string?> Ops { get; } = [];

        public ComposeExecutorResponse Response { get; set; } = new();

        public Task<ComposeExecutorResponse> ExecuteAsync(ComposeExecutorRequest request, CancellationToken cancellationToken)
        {
            Ops.Add(request.Op);
            var running = string.Equals(request.Op, "status", StringComparison.Ordinal)
                ? Response.RunningImage
                : request.Image ?? Response.RunningImage;
            return Task.FromResult(Response with { RunningImage = running });
        }
    }

    private sealed class NotRequiredApproval : IOperatorApprovalEvaluator
    {
        public ApprovalRequirement Evaluate(ClaimsPrincipal principal, OperatorAuthorizationRequest request)
            => ApprovalRequirement.NotRequired();
    }

    private sealed class MemoryTargets(DeployTargetDefinition target) : IDeployTargetRegistry
    {
        public Task<IReadOnlyList<DeployTargetDefinition>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DeployTargetDefinition>>([target]);

        public Task<DeployTargetDefinition?> GetAsync(string targetId, CancellationToken cancellationToken = default)
            => Task.FromResult<DeployTargetDefinition?>(target);
    }

    private sealed class MemoryWorkflows : IWorkflowOperationStore
    {
        private readonly Dictionary<string, WorkflowOperationRecord> _items = new(StringComparer.Ordinal);

        public Task<bool> TryCreateAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
            => Task.FromResult(_items.TryAdd(operation.OperationId, operation));

        public Task<WorkflowOperationRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult(_items.TryGetValue(operationId, out var item) ? item : null);

        public Task<WorkflowOperationRecord?> GetByMetadataPackageIdAsync(string packageId, CancellationToken cancellationToken = default)
            => Task.FromResult<WorkflowOperationRecord?>(null);

        public Task SetAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _items[operation.OperationId] = operation;
            return Task.CompletedTask;
        }

        public Task<bool> TrySetAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            if (!_items.TryGetValue(operation.OperationId, out var current) || current.Version != operation.Version)
            {
                return Task.FromResult(false);
            }

            _items[operation.OperationId] = operation with { Version = operation.Version + 1 };
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<WorkflowOperationRecord>> ListActiveAsync(WorkflowOperationKind? kind = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkflowOperationRecord>>(_items.Values.ToArray());

        public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
