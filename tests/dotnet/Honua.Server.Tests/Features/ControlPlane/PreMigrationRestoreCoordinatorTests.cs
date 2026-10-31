// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.ControlPlane.Executors;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.ControlPlane.Restore;
using Honua.Core.Features.Guardrails.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.ControlPlane;

public sealed class PreMigrationRestoreCoordinatorTests
{
    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void VerifyClassification_TamperedDeclaration_FailsClosed()
    {
        var declaration = Honua.Core.Features.Infrastructure.Migrations.MigrationReaderCompatibility.Declare("143_AddTable.sql", "CREATE TABLE example (id integer);");
        Assert.Equal("expand", SchemaJournalReader.VerifyClassification(declaration.ScriptName, declaration.DeclarationFormat,
            declaration.Classification, declaration.MinReaderSchema, declaration.ScriptSha256, declaration.ScriptText));
        Assert.Null(SchemaJournalReader.VerifyClassification(declaration.ScriptName, declaration.DeclarationFormat + 1,
            declaration.Classification, declaration.MinReaderSchema, declaration.ScriptSha256, declaration.ScriptText));
        Assert.Null(SchemaJournalReader.VerifyClassification(declaration.ScriptName, declaration.DeclarationFormat,
            declaration.Classification, declaration.MinReaderSchema, declaration.ScriptSha256, "DROP TABLE example;"));
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task CloudRestore_WithoutAuthorizedSecurityGroups_DoesNotMutate()
    {
        var harness = NewHarness();
        harness.Cell.RdsSecurityGroupIds.Clear();
        var completion = await harness.Coordinator.ExecuteAsync(Payload(SnapshotId), Request(), harness.Starter, CancellationToken.None);
        Assert.Equal(PreMigrationRestoreCodes.ScopeViolation, completion.Code);
        harness.Events.Should().NotContain("restore");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task CloudRestore_PrefixTooLongForUniqueInstance_DoesNotMutate()
    {
        var harness = NewHarness();
        var prefix = new string('a', 54) + "-";
        harness.Cell.CellPrefix = prefix;
        harness.Cell.ConnectionSecretId = prefix + "db";
        harness.Cell.DbSubnetGroupName = prefix + "subnet";
        harness.Snapshot = harness.Snapshot with { SnapshotId = prefix + "snap", CellPrefix = prefix };
        var completion = await harness.Coordinator.ExecuteAsync(Payload(prefix + "snap"), Request(), harness.Starter, CancellationToken.None);
        Assert.NotNull(completion.Code);
        harness.Events.Should().NotContain("restore");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task MissingBackup_DoesNotRestoreOrStart()
    {
        var harness = NewHarness();
        var completion = await harness.Coordinator.ExecuteAsync(Payload(backupId: null), Request(), harness.Starter, CancellationToken.None);

        ReaderIncompatibleRestorePolicy.IsReportedSuccess(completion).Should().BeFalse();
        completion.Code.Should().Be(PreMigrationRestoreCodes.BackupRequired);
        harness.Events.Should().NotContain("restore");
        harness.Events.Should().NotContain("start");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task SnapshotOutsideTheCell_DoesNotRestore()
    {
        var harness = NewHarness();
        var completion = await harness.Coordinator.ExecuteAsync(Payload("other-snap"), Request(), harness.Starter, CancellationToken.None);

        completion.Code.Should().Be(PreMigrationRestoreCodes.ScopeViolation);
        harness.Events.Should().NotContain("describe");
        harness.Events.Should().NotContain("restore");
        harness.Events.Should().NotContain("start");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task SecretOutsideTheCell_DoesNotRestore()
    {
        var harness = NewHarness();
        harness.Cell.ConnectionSecretId = "other-secret";
        var completion = await harness.Coordinator.ExecuteAsync(Payload(SnapshotId), Request(), harness.Starter, CancellationToken.None);

        completion.Code.Should().Be(PreMigrationRestoreCodes.ScopeViolation);
        harness.Events.Should().NotContain("restore");
        harness.Events.Should().NotContain("start");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task WrongSchemaAfterRestore_DoesNotRetargetOrStart()
    {
        var harness = NewHarness();
        harness.Probed = Journal(142, "reader-incompatible");
        var completion = await harness.Coordinator.ExecuteAsync(Payload(SnapshotId), Request(), harness.Starter, CancellationToken.None);

        completion.Code.Should().Be(PreMigrationRestoreCodes.RestoreIncomplete);
        harness.Events.Should().ContainInOrder("restore", "probe");
        harness.Events.Should().NotContain("point");
        harness.Events.Should().NotContain("start");
        ReaderIncompatibleRestorePolicy.IsReportedSuccess(completion).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ProviderFailure_DoesNotStartThePriorImage()
    {
        var harness = NewHarness();
        harness.Restored = new RdsNewInstance(false, null, PreMigrationRestoreCodes.ProviderFailed);
        var completion = await harness.Coordinator.ExecuteAsync(Payload(SnapshotId), Request(), harness.Starter, CancellationToken.None);

        completion.Code.Should().Be(PreMigrationRestoreCodes.ProviderFailed);
        harness.Events.Should().Contain("restore");
        harness.Events.Should().NotContain("start");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task CloudRestore_PointsTheSecretAndReadsBackBeforeStarting()
    {
        var harness = NewHarness();
        var completion = await harness.Coordinator.ExecuteAsync(Payload(SnapshotId), Request(), harness.Starter, CancellationToken.None);

        ReaderIncompatibleRestorePolicy.IsReportedSuccess(completion).Should().BeTrue();
        harness.RestoreRequest!.SecurityGroupIds.Should().Equal("sg-01234567");
        completion.OperationId.Should().Be("op-1");
        harness.Events.Should().ContainInOrder("describe", "restore", "probe", "point", "start", "customer", "audit");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task IncompleteCustomerReadback_DoesNotReportSuccess()
    {
        var harness = NewHarness();
        harness.CustomerOk = false;
        var completion = await harness.Coordinator.ExecuteAsync(Payload(SnapshotId), Request(), harness.Starter, CancellationToken.None);

        completion.Code.Should().Be(PreMigrationRestoreCodes.ReadbackIncomplete);
        completion.ImageStarted.Should().BeTrue();
        ReaderIncompatibleRestorePolicy.IsReportedSuccess(completion).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task AuditFailure_DoesNotReportSuccess()
    {
        var harness = NewHarness();
        harness.AuditOk = false;
        var completion = await harness.Coordinator.ExecuteAsync(Payload(SnapshotId), Request(), harness.Starter, CancellationToken.None);

        completion.Code.Should().Be(PreMigrationRestoreCodes.AuditFailed);
        completion.OperationId.Should().BeNull();
        ReaderIncompatibleRestorePolicy.IsReportedSuccess(completion).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ComposeSocketMissing_DoesNotStartThePriorImage()
    {
        using var root = new TempBackup();
        var harness = NewHarness();
        harness.TargetKind = DeployTargetKind.SelfHostedRolling;
        harness.Cell.ComposeBackupDirectory = root.Path;
        harness.Cell.ComposeProject = "honua";
        harness.Cell.ComposeSocketPath = string.Empty;
        var completion = await harness.Coordinator.ExecuteAsync(Payload(root.BackupId), Request(), harness.Starter, CancellationToken.None);

        completion.Code.Should().Be(PreMigrationRestoreCodes.TransportUnavailable);
        harness.Events.Should().NotContain("compose");
        harness.Events.Should().NotContain("start");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ComposeRestore_PriorImageAppliesItsMigrations_CompletesReadback()
    {
        using var root = new TempBackup();
        var harness = NewHarness();
        harness.TargetKind = DeployTargetKind.SelfHostedRolling;
        harness.Cell.ComposeBackupDirectory = root.Path;
        harness.Cell.ComposeProject = "honua";
        harness.Cell.ComposeSocketPath = "/tmp/honua-compose.sock";
        harness.Cell.Revisions = [new RevisionSchemaOption { Revision = "rev-141", MaxSchema = 142 }];
        harness.JournalSnapshot = Journal(143, "reader-incompatible");
        harness.Probed = Journal(142, "expand");
        harness.ComposeConfigured = true;
        harness.ComposeResult = new ComposeRollbackResult(true, null, true, 141, true);
        var completion = await harness.Coordinator.ExecuteAsync(Payload(root.BackupId), Request(), harness.Starter, CancellationToken.None);
        ReaderIncompatibleRestorePolicy.IsReportedSuccess(completion).Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ComposeSuccess_DoesNotCallTheDeployStarter()
    {
        using var root = new TempBackup();
        var harness = NewHarness();
        harness.TargetKind = DeployTargetKind.SelfHostedRolling;
        harness.Cell.ComposeBackupDirectory = root.Path;
        harness.Cell.ComposeProject = "honua";
        harness.Cell.ComposeSocketPath = "/tmp/honua-compose.sock";
        harness.ComposeConfigured = true;
        harness.ComposeResult = new ComposeRollbackResult(true, null, true, 141, true);
        var completion = await harness.Coordinator.ExecuteAsync(Payload(root.BackupId), Request(), harness.Starter, CancellationToken.None);

        ReaderIncompatibleRestorePolicy.IsReportedSuccess(completion).Should().BeTrue();
        harness.Events.Should().Contain("compose");
        harness.Events.Should().NotContain("start");
        harness.Events.Should().NotContain("restore");
        completion.OperationId.Should().StartWith("restore-");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task CompatibleRollback_StartsWithoutRestoring()
    {
        var harness = NewHarness();
        harness.JournalSnapshot = Journal(141, "expand");
        harness.Cell.Revisions = [new RevisionSchemaOption { Revision = "rev-141", MaxSchema = 141 }];
        var completion = await harness.Coordinator.ExecuteAsync(Payload(backupId: null), Request(), harness.Starter, CancellationToken.None);

        ReaderIncompatibleRestorePolicy.IsReportedSuccess(completion).Should().BeTrue();
        harness.Events.Should().ContainInOrder("start", "audit");
        harness.Events.Should().NotContain("restore");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task NonRollbackDeploy_DoesNotEnterTheRestoreGate()
    {
        var harness = NewHarness();
        var guard = Guard(harness.Coordinator);
        var payload = new DeployExecutionPayload { TargetId = "cell", DesiredRevision = "rev-141", Rollback = false }.Serialize();

        var id = await guard.ExecuteAsync(Request(), payload, CancellationToken.None);

        id.Should().BeNull();
        harness.Events.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task RollbackGuard_ThrowsWhenRestoreDoesNotSucceed()
    {
        var harness = NewHarness();
        var guard = Guard(harness.Coordinator);
        var payload = Payload(backupId: null).Serialize();

        var act = () => guard.ExecuteAsync(Request(), payload, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(PreMigrationRestoreCodes.BackupRequired);
        harness.Events.Should().NotContain("start");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void HostSwap_ReplacesOnlyTheHost()
    {
        ConnectionStringHostSwap.TrySwapHost("Host=old.example;Username=honua;Password=secret", "db.internal", out var swapped).Should().BeTrue();
        ConnectionStringHostSwap.ReadHost(swapped).Should().Be("db.internal");
        swapped.Should().Contain("Password=secret");
        ConnectionStringHostSwap.TrySwapHost("Host=old.example;Password=secret", "bad host", out _).Should().BeFalse();
        ConnectionStringHostSwap.TrySwapHost("Host=old.example;Password=secret", "a;Password=x", out _).Should().BeFalse();
    }

    [UnitTheory]
    [InlineData("{\"connectionString\":\"Host=old.example;Username=user;Password=test;Database=db\",\"extra\":\"retained\"}")]
    [InlineData("{\"ConnectionString\":\"Host=old.example;Username=user;Password=test;Database=db\",\"extra\":\"retained\"}")]
    [InlineData("{\"host\":\"old.example\",\"username\":\"user\",\"password\":\"a;b=c\",\"dbname\":\"db\",\"extra\":\"retained\"}")]
    [Operation(Operations.TestInfrastructure)]
    public void HostSwap_JsonSecret_PreservesEnvelopeAndOtherFields(string secret)
    {
        ConnectionStringHostSwap.TrySwapHost(secret, "db.internal", out var swapped).Should().BeTrue();
        ConnectionStringHostSwap.ReadHost(swapped).Should().Be("db.internal");
        using var document = JsonDocument.Parse(swapped);
        document.RootElement.GetProperty("extra").GetString().Should().Be("retained");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task CompatibleAzureRollback_StartsWithoutRestoring()
    {
        var harness = NewHarness();
        harness.TargetKind = DeployTargetKind.AzureContainerApps;
        harness.JournalSnapshot = Journal(141, "expand");
        harness.Cell.Revisions = [new RevisionSchemaOption { Revision = "rev-141", MaxSchema = 141 }];
        var completion = await harness.Coordinator.ExecuteAsync(Payload(backupId: null), Request(), harness.Starter, CancellationToken.None);

        ReaderIncompatibleRestorePolicy.IsReportedSuccess(completion).Should().BeTrue();
        harness.Events.Should().Contain("start");
        harness.Events.Should().NotContain("restore");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task LambdaRestoreRequired_FailsClosedWhenNoDurableWorkerIsAvailable()
    {
        var harness = NewHarness();
        harness.TargetKind = DeployTargetKind.AwsLambda;
        var completion = await harness.Coordinator.ExecuteAsync(Payload(SnapshotId), Request(), harness.Starter, CancellationToken.None);

        completion.Code.Should().Be(PreMigrationRestoreCodes.RetargetUnsupported);
        harness.Events.Should().NotContain("restore");
        harness.Events.Should().NotContain("start");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task AzureRestoreRequired_DoesNotStartThePriorImage()
    {
        var harness = NewHarness();
        harness.TargetKind = DeployTargetKind.AzureFunctions;
        var completion = await harness.Coordinator.ExecuteAsync(Payload(SnapshotId), Request(), harness.Starter, CancellationToken.None);

        completion.Code.Should().Be(PreMigrationRestoreCodes.RetargetUnsupported);
        harness.Events.Should().NotContain("restore");
        harness.Events.Should().NotContain("start");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task CloudStartDidNotFinish_DoesNotProbeTheCustomer()
    {
        var harness = NewHarness();
        harness.StartId = null;
        var completion = await harness.Coordinator.ExecuteAsync(Payload(SnapshotId), Request(), harness.Starter, CancellationToken.None);

        completion.Code.Should().Be(PreMigrationRestoreCodes.ProviderFailed);
        harness.Events.Should().Contain("start");
        harness.Events.Should().NotContain("customer");
        ReaderIncompatibleRestorePolicy.IsReportedSuccess(completion).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ComposeOmitsBackupId_SelectsNewestVerifyingManifest()
    {
        using var root = new TempBackup();
        root.Write("backup-" + new string('a', 32), "rev-999", "2026-10-31T00:00:00Z", "dump");
        var harness = NewHarness();
        harness.TargetKind = DeployTargetKind.SelfHostedRolling;
        harness.Cell.ComposeBackupDirectory = root.Path;
        harness.Cell.ComposeProject = "honua";
        harness.Cell.ComposeSocketPath = "/tmp/honua-compose.sock";
        harness.ComposeConfigured = true;
        harness.ComposeResult = new ComposeRollbackResult(true, null, true, 141, true);
        var completion = await harness.Coordinator.ExecuteAsync(Payload(backupId: null), Request(), harness.Starter, CancellationToken.None);

        ReaderIncompatibleRestorePolicy.IsReportedSuccess(completion).Should().BeTrue();
        harness.ComposeBackupId.Should().Be(root.BackupId);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task PriorImage_WaitsUntilTheWorkflowSucceeds()
    {
        var reads = 0;
        var ready = await PriorImageReadiness.WaitAsync(
            _ =>
            {
                reads++;
                var state = reads == 1 ? OperationBackendState.Queued : OperationBackendState.Succeeded;
                return Task.FromResult<OperationBackendStatus?>(new OperationBackendStatus { State = state });
            },
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(1),
            CancellationToken.None);

        ready.Should().BeTrue();
        reads.Should().Be(2);

        var failed = await PriorImageReadiness.WaitAsync(
            _ => Task.FromResult<OperationBackendStatus?>(new OperationBackendStatus { State = OperationBackendState.Failed }),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(10),
            CancellationToken.None);
        failed.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ComposeAudit_ReportsSucceededWhenTheDeployStoreHasNoRecord()
    {
        var directory = Directory.CreateTempSubdirectory("honua-audit-").FullName;
        try
        {
            var path = Path.Combine(directory, "audit.jsonl");
            var audit = new FilePreMigrationRestoreAudit(Options.Create(new RestoreCellOptions { AuditPath = path }));
            var line = new PreMigrationRestoreAuditLine
            {
                Time = DateTimeOffset.Parse("2026-10-30T00:00:00Z", CultureInfo.InvariantCulture),
                Op = "rollback",
                OperationId = "restore-0123456789abcdef0123456789abcdef",
                Ok = true,
                ReadbackComplete = true,
                Image = "rev-141",
            };
            (await audit.AppendAsync(line, CancellationToken.None)).Should().BeTrue();

            var status = await audit.FindAsync(line.OperationId!, CancellationToken.None);
            status!.State.Should().Be(OperationBackendState.Succeeded);

            var registry = Substitute.For<IDeployTargetRegistry>();
            var store = Substitute.For<IWorkflowOperationStore>();
            var approval = Substitute.For<IOperatorApprovalEvaluator>();
            var workflow = new DeployWorkflowService(registry, [store], [], approval, NullLogger<DeployWorkflowService>.Instance);
            var guard = new RestoreGuardedDeployOperationExecutor(
                new DeployOperationExecutor(workflow),
                NewHarness().Coordinator,
                audit);
            var observed = await guard.GetBackendStatusAsync(line.OperationId!, CancellationToken.None);
            observed!.State.Should().Be(OperationBackendState.Succeeded);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Execute_CataloguedPriorImageWithoutRollbackFlag_IsRefused()
    {
        var harness = NewHarness();
        var workflow = new DeployWorkflowService(Substitute.For<IDeployTargetRegistry>(),
            [Substitute.For<IWorkflowOperationStore>()], [], Substitute.For<IOperatorApprovalEvaluator>(),
            NullLogger<DeployWorkflowService>.Instance);
        var guard = new RestoreGuardedDeployOperationExecutor(new DeployOperationExecutor(workflow), harness.Coordinator,
            restoreOptions: Options.Create(harness.Cell));
        var payload = Payload(SnapshotId) with { Rollback = false };
        await Assert.ThrowsAsync<InvalidOperationException>(() => guard.ExecuteAsync(Request(), payload.Serialize()));
        harness.Events.Should().BeEmpty();
    }

    private const string SnapshotId = "honua-cert-snap";

    private static DeployExecutionPayload Payload(string? backupId)
        => new()
        {
            TargetId = "cell",
            DesiredRevision = "rev-141",
            Rollback = true,
            BackupId = backupId,
        };

    private static OperationGatewayRequest Request()
        => new()
        {
            Kind = OperationClass.Deploy,
            IdempotencyKey = "rollback-key",
            CorrelationId = "corr-1",
        };

    private static SchemaJournalSnapshot Journal(int number, string classification)
        => new(true, [new AppliedSchemaMigration(number, classification, 0)]);

    private static RestoreGuardedDeployOperationExecutor Guard(PreMigrationRestoreCoordinator coordinator)
    {
        var registry = Substitute.For<IDeployTargetRegistry>();
        var store = Substitute.For<IWorkflowOperationStore>();
        var approval = Substitute.For<IOperatorApprovalEvaluator>();
        var workflow = new DeployWorkflowService(registry, [store], [], approval, NullLogger<DeployWorkflowService>.Instance);
        return new RestoreGuardedDeployOperationExecutor(new DeployOperationExecutor(workflow), coordinator);
    }

    private static Harness NewHarness() => new();

    private sealed class Harness
    {
        public List<string> Events { get; } = [];

        public RestoreCellOptions Cell { get; } = new()
        {
            CellPrefix = "honua-cert-",
            ConnectionSecretId = "honua-cert-db",
            DbSubnetGroupName = "honua-cert-cert",
            RdsSecurityGroupIds = ["sg-01234567"],
            EnvironmentTag = "cert",
            AuditPath = "audit.jsonl",
            Revisions = [new RevisionSchemaOption { Revision = "rev-141", MaxSchema = 141 }],
        };

        public DeployTargetKind TargetKind { get; set; } = DeployTargetKind.AwsEcs;

        public SchemaJournalSnapshot JournalSnapshot { get; set; } = new(true,
        [
            new AppliedSchemaMigration(141, "expand", 0),
            new AppliedSchemaMigration(142, "reader-incompatible", 0),
        ]);

        public SchemaJournalSnapshot Probed { get; set; } = new(true, [new AppliedSchemaMigration(141, "expand", 0)]);

        public RdsSnapshotEvidence Snapshot { get; set; } = new()
        {
            SnapshotId = SnapshotId,
            CellPrefix = "honua-cert-",
            Status = "available",
            SchemaVersion = 141,
            CellTagMatches = true,
        };

        public RdsNewInstance Restored { get; set; } = new(true, "db.internal", null);

        public RdsRestoreRequest? RestoreRequest { get; set; }

        public bool CustomerOk { get; set; } = true;

        public bool AuditOk { get; set; } = true;

        public bool ComposeConfigured { get; set; }

        public ComposeRollbackResult ComposeResult { get; set; } = new(false, null, false, null, false);

        public string? ComposeBackupId { get; set; }

        public string? StartId { get; set; } = "op-1";

        public OperationBackendStatus? AuditStatus { get; set; }

        public RecordingStarter Starter => new(this);

        public PreMigrationRestoreCoordinator Coordinator => new(
            new PreMigrationRestoreAssessmentService(
                new RecordingJournal(this), new RecordingRevisions(this), new RecordingRds(this),
                new RecordingSecrets(this), new StaticMonitor(this), Options.Create(Cell)),
            new PreMigrationRestoreExecutionService(
                new RecordingRevisions(this), new RecordingRds(this), new RecordingSecrets(this),
                new RecordingCompose(this), new RecordingProbe(this), new RecordingCustomer(this),
                new RecordingAudit(this), Options.Create(Cell)));

    }

    private sealed class RecordingStarter(Harness harness) : IPriorImageStarter
    {
        public Task<string?> StartAsync(OperationGatewayRequest request, string? executionPayload, CancellationToken cancellationToken)
        {
            harness.Events.Add("start");
            return Task.FromResult(harness.StartId);
        }
    }

    private sealed class RecordingJournal(Harness harness) : ISchemaJournal
    {
        public Task<SchemaJournalSnapshot> ReadAsync(CancellationToken cancellationToken)
        {
            harness.Events.Add("journal");
            return Task.FromResult(harness.JournalSnapshot);
        }
    }

    private sealed class RecordingRevisions(Harness harness) : IRevisionSchemaCatalog
    {
        public int? MaxSchema(string revision)
            => harness.Cell.Revisions.FirstOrDefault(entry => string.Equals(entry.Revision, revision, StringComparison.Ordinal))?.MaxSchema;
    }

    private sealed class RecordingRds(Harness harness) : IRdsSnapshotRestore
    {
        public Task<RdsSnapshotEvidence?> DescribeAsync(string snapshotId, string cellPrefix, CancellationToken cancellationToken)
        {
            harness.Events.Add("describe");
            return Task.FromResult<RdsSnapshotEvidence?>(harness.Snapshot);
        }

        public Task<RdsNewInstance> RestoreNewAsync(RdsRestoreRequest request, CancellationToken cancellationToken)
        {
            harness.Events.Add("restore");
            harness.RestoreRequest = request;
            return Task.FromResult(harness.Restored);
        }
    }

    private sealed class RecordingSecrets(Harness harness) : IConnectionSecretRetarget
    {
        public bool IsConfigured => true;

        public Task<bool> PointAtAsync(string secretId, string host, CancellationToken cancellationToken)
        {
            harness.Events.Add("point");
            return Task.FromResult(true);
        }

        public Task<string?> ReadHostAsync(string secretId, CancellationToken cancellationToken)
            => Task.FromResult<string?>("db.internal");
    }

    private sealed class RecordingCompose(Harness harness) : IComposeRollbackTransport
    {
        public bool IsConfigured => harness.ComposeConfigured;

        public Task<ComposeRollbackResult> RollbackAsync(string image, string? backupId, string? requestId, CancellationToken cancellationToken)
        {
            harness.Events.Add("compose");
            harness.ComposeBackupId = backupId;
            return Task.FromResult(harness.ComposeResult);
        }
    }

    private sealed class RecordingProbe(Harness harness) : IRestoredSchemaProbe
    {
        public Task<SchemaJournalSnapshot> ProbeAsync(string? endpoint, CancellationToken cancellationToken)
        {
            harness.Events.Add("probe");
            return Task.FromResult(harness.Probed);
        }
    }

    private sealed class RecordingCustomer(Harness harness) : IServingCustomerProbe
    {
        public Task<bool> SucceededAsync(CancellationToken cancellationToken)
        {
            harness.Events.Add("customer");
            return Task.FromResult(harness.CustomerOk);
        }
    }

    private sealed class RecordingAudit(Harness harness) : IPreMigrationRestoreAudit
    {
        public Task<bool> AppendAsync(PreMigrationRestoreAuditLine line, CancellationToken cancellationToken)
        {
            harness.Events.Add("audit");
            return Task.FromResult(harness.AuditOk);
        }

        public Task<OperationBackendStatus?> FindAsync(string operationId, CancellationToken cancellationToken)
            => Task.FromResult(harness.AuditStatus);
    }

    private sealed class StaticMonitor(Harness harness) : IOptionsMonitor<ControlPlaneOptions>
    {
        public ControlPlaneOptions CurrentValue { get; } = new()
        {
            DeployTargets = [new DeployTargetOptions { TargetId = "cell", TargetKind = harness.TargetKind }],
        };

        public ControlPlaneOptions Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<ControlPlaneOptions, string?> listener) => new Noop();

        private sealed class Noop : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class TempBackup : IDisposable
    {
        public TempBackup()
        {
            Path = Directory.CreateTempSubdirectory("honua-restore-").FullName;
            var bytes = Encoding.UTF8.GetBytes("dump");
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            BackupId = "backup-" + hash[..32];
            var directory = System.IO.Path.Combine(Path, BackupId);
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(System.IO.Path.Combine(directory, "database.dump"), bytes);
            var manifest = new ComposeManifestFile
            {
                BackupId = BackupId,
                Project = "honua",
                Image = "rev-141",
                SchemaVersion = 141,
                Sha256 = hash,
                DumpFile = "database.dump",
                CreatedAt = DateTimeOffset.Parse("2026-10-30T00:00:00Z", CultureInfo.InvariantCulture),
            };
            File.WriteAllText(
                System.IO.Path.Combine(directory, "manifest.json"),
                JsonSerializer.Serialize(manifest, PreMigrationRestoreJsonContext.Default.ComposeManifestFile));
        }

        public string Path { get; }

        public string BackupId { get; }

        public void Write(string backupId, string image, string createdAt, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var directory = System.IO.Path.Combine(Path, backupId);
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(System.IO.Path.Combine(directory, "database.dump"), bytes);
            var manifest = new ComposeManifestFile
            {
                BackupId = backupId,
                Project = "honua",
                Image = image,
                SchemaVersion = 141,
                Sha256 = hash,
                DumpFile = "database.dump",
                CreatedAt = DateTimeOffset.Parse(createdAt, CultureInfo.InvariantCulture),
            };
            File.WriteAllText(
                System.IO.Path.Combine(directory, "manifest.json"),
                JsonSerializer.Serialize(manifest, PreMigrationRestoreJsonContext.Default.ComposeManifestFile));
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
