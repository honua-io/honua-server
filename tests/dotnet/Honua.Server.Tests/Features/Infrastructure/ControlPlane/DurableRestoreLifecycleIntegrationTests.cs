// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.ControlPlane.Executors;
using Honua.Core.Exceptions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.ControlPlane.Restore;
using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>Exercises the approved restore continuation through the real Redis stores and gateway.</summary>
[Collection(RedisFixture.CollectionName)]
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.TestInfrastructure)]
public sealed class DurableRestoreLifecycleIntegrationTests(RedisFixture redis)
{
    [IntegrationTest]
    public async Task ApprovedRestore_ReplacementWorker_WaitsForProtectionAndVerifiesReadback()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var database = connection.GetDatabase();
        await database.KeyDeleteAsync("controlplane:workflow:restore-cell-exclusive-owner");
        try
        {
            var workflows = new RedisWorkflowOperationStore(connection, NullLogger<RedisWorkflowOperationStore>.Instance);
            var proposals = new RedisOperationProposalStore(connection, NullLogger<RedisOperationProposalStore>.Instance);
            var journal = Substitute.For<ISchemaJournal>();
            journal.ReadAsync(Arg.Any<CancellationToken>()).Returns(new SchemaJournalSnapshot(true,
                [new AppliedSchemaMigration(142, "reader-incompatible", 0)]));
            var revisions = Substitute.For<IRevisionSchemaCatalog>();
            revisions.MaxSchema("prior").Returns(141);
            var snapshots = Substitute.For<IRdsSnapshotRestore>();
            snapshots.DescribeAsync("honua-cert-snapshot", "honua-cert-", Arg.Any<CancellationToken>()).Returns(
                new RdsSnapshotEvidence { SnapshotId = "honua-cert-snapshot", CellPrefix = "honua-cert-", Status = "available", SchemaVersion = 141, CellTagMatches = true });
            snapshots.RestoreNewAsync(Arg.Any<RdsRestoreRequest>(), Arg.Any<CancellationToken>()).Returns(new RdsNewInstance(true, "restored.internal", null));
            var secrets = Substitute.For<IConnectionSecretRetarget>();
            secrets.IsConfigured.Returns(true);
            secrets.PointAtAsync(Arg.Any<string>(), "restored.internal", Arg.Any<CancellationToken>()).Returns(true);
            secrets.ReadHostAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("restored.internal");
            var probe = Substitute.For<IRestoredSchemaProbe>();
            probe.ProbeAsync("restored.internal", Arg.Any<CancellationToken>()).Returns(new SchemaJournalSnapshot(true,
                [new AppliedSchemaMigration(141, "expand", 0)]));
            var customer = Substitute.For<IServingCustomerProbe>();
            customer.SucceededAsync(Arg.Any<CancellationToken>()).Returns(true);
            var audit = Substitute.For<IPreMigrationRestoreAudit>();
            audit.AppendAsync(Arg.Any<PreMigrationRestoreAuditLine>(), Arg.Any<CancellationToken>()).Returns(true);
            var cell = Options.Create(new RestoreCellOptions
            {
                CellPrefix = "honua-cert-",
                ConnectionSecretId = "honua-cert-db",
                DbSubnetGroupName = "honua-cert-subnet",
                RdsSecurityGroupIds = ["sg-01234567"],
                EnvironmentTag = "cert"
            });
            var monitor = Substitute.For<IOptionsMonitor<ControlPlaneOptions>>();
            monitor.CurrentValue.Returns(new ControlPlaneOptions
            {
                DeployTargets = [new DeployTargetOptions { TargetId = "cell", TargetKind = DeployTargetKind.AwsEcs }]
            });
            var coordinator = new PreMigrationRestoreCoordinator(
                new PreMigrationRestoreAssessmentService(journal, revisions, snapshots, secrets, monitor, cell),
                new PreMigrationRestoreExecutionService(revisions, snapshots, secrets, Substitute.For<IComposeRollbackTransport>(), probe, customer, audit, cell));
            var targets = Substitute.For<IDeployTargetRegistry>();
            targets.GetAsync("cell", Arg.Any<CancellationToken>()).Returns(new DeployTargetDefinition
            {
                TargetId = "cell",
                TargetKind = DeployTargetKind.AwsEcs,
                Backend = "test",
                Environment = "cert",
                TargetName = "test"
            });
            var backend = Substitute.For<IDeployBackend>();
            backend.BackendName.Returns("test");
            backend.TargetKind.Returns(DeployTargetKind.AwsEcs);
            backend.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new DeployBackendCapabilities { SupportsRollback = true, SupportsProgressPolling = true });
            backend.PlanAsync(Arg.Any<DeployOperationSpec>(), Arg.Any<CancellationToken>()).Returns(new DeployPlan { IsReadyToSubmit = true });
            backend.StartAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<CancellationToken>()).Returns(new DeploySubmissionResult { Status = WorkflowOperationStatus.Reconciling });
            var approvals = Substitute.For<IOperatorApprovalEvaluator>();
            approvals.Evaluate(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>()).Returns(ApprovalRequirement.NotRequired());
            var deploy = new DeployOperationExecutor(new DeployWorkflowService(targets, [workflows], [backend], approvals, NullLogger<DeployWorkflowService>.Instance, restoreOptions: cell));
            var queue = new DurableRestoreQueue(workflows);
            var guarded = new RestoreGuardedDeployOperationExecutor(deploy, coordinator, audit, cell, queue);
            var ladder = Substitute.For<IGuardrailLadder>();
            ladder.Resolve(OperationClass.Deploy).Returns(new GuardrailDecision(GuardrailTier.RequiresApproval, OperationClass.Deploy, HonuaEdition.Pro, "test"));
            var gateway = CanonicalOperationGatewayTestComposition.Build(proposals, ladder, [guarded]);
            var payload = new DeployExecutionPayload { TargetId = "cell", DesiredRevision = "prior", CurrentRevision = "candidate", Rollback = true, BackupId = "honua-cert-snapshot" }.Serialize();
            var routed = await gateway.CreateApprovalProposalAsync("opinst-restore-" + Guid.NewGuid().ToString("N"), new OperationGatewayRequest
            {
                Kind = OperationClass.Deploy,
                RequestedBy = "proposer",
                IdempotencyKey = Guid.NewGuid().ToString("N"),
                ExecutionPayload = payload,
                Plan = new OperationProposalPlan { Summary = "Restore prior image", ExecutionPayload = payload }
            });
            Assert.Equal(OperationGatewayOutcome.ProposalCreated, routed.Outcome);
            Assert.NotNull(routed.ProposalId);
            await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.ApplyApprovedProposalAsync(routed.ProposalId, "proposer"));
            await snapshots.DidNotReceive().RestoreNewAsync(Arg.Any<RdsRestoreRequest>(), Arg.Any<CancellationToken>());
            var approved = await gateway.ApplyApprovedProposalAsync(routed.ProposalId, "approver");
            Assert.NotNull(approved);
            Assert.Equal(OperationProposalStatus.Submitted, approved.Status);
            Assert.NotNull(approved.ExecutionOperationId);
            var secondRoute = await gateway.CreateApprovalProposalAsync("opinst-restore-" + Guid.NewGuid().ToString("N"), new OperationGatewayRequest
            {
                Kind = OperationClass.Deploy,
                RequestedBy = "proposer",
                IdempotencyKey = Guid.NewGuid().ToString("N"),
                ExecutionPayload = payload,
                Plan = new OperationProposalPlan { Summary = "Restore prior image", ExecutionPayload = payload }
            });
            Assert.NotNull(secondRoute.ProposalId);
            var second = await gateway.ApplyApprovedProposalAsync(secondRoute.ProposalId, "approver");
            Assert.NotNull(second);
            Assert.NotNull(second.ExecutionOperationId);
            var verifier = new DurableRestoreCompletionVerifier(probe, revisions, customer, audit, secrets, cell);
            DurableRestoreWorker NewWorker() => new(workflows, proposals, coordinator, deploy, verifier, audit, NullLogger<DurableRestoreWorker>.Instance);
            var activeDeploy = new WorkflowOperationRecord
            {
                OperationId = "active-deploy-" + Guid.NewGuid().ToString("N"),
                Kind = WorkflowOperationKind.Deploy,
                Status = WorkflowOperationStatus.Reconciling,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            Assert.True(await workflows.TryCreateAsync(activeDeploy));
            await NewWorker().ReconcileAsync(approved.ExecutionOperationId, CancellationToken.None);
            await snapshots.DidNotReceive().RestoreNewAsync(Arg.Any<RdsRestoreRequest>(), Arg.Any<CancellationToken>());
            var savedActive = await workflows.GetAsync(activeDeploy.OperationId);
            Assert.True(await workflows.TrySetAsync(savedActive! with { Status = WorkflowOperationStatus.Succeeded }));
            await NewWorker().ReconcileAsync(approved.ExecutionOperationId, CancellationToken.None);
            var checkpoint = await workflows.GetAsync(approved.ExecutionOperationId);
            Assert.Equal("awaiting-deploy", checkpoint?.PreMigrationRestore?.Phase);
            await Assert.ThrowsAsync<ResourceConflictException>(async () =>
            {
                await using var admission = await RestoreDeployAdmission.AcquireAsync(workflows, "unrelated-deploy", null, CancellationToken.None);
            });
            await using (var promotion = await RestoreDeployAdmission.AcquireAsync(workflows,
                checkpoint!.PreMigrationRestore!.DeployOperationId!, null, CancellationToken.None, allowRestorePromotion: true))
            {
                Assert.False(promotion.Token.IsCancellationRequested);
            }
            await snapshots.Received(1).RestoreNewAsync(Arg.Any<RdsRestoreRequest>(), Arg.Any<CancellationToken>());
            var replacement = NewWorker();
            Assert.False(await replacement.ClaimCellAsync("other-restore", CancellationToken.None));
            await replacement.ReconcileAsync(approved.ExecutionOperationId, CancellationToken.None);
            Assert.Equal(WorkflowOperationStatus.Reconciling, (await workflows.GetAsync(approved.ExecutionOperationId))?.Status);
            await customer.DidNotReceive().SucceededAsync(Arg.Any<CancellationToken>());
            var deployment = await workflows.GetAsync(checkpoint!.PreMigrationRestore!.DeployOperationId!);
            Assert.NotNull(deployment);
            Assert.True(await workflows.TrySetAsync(deployment with { Status = WorkflowOperationStatus.Succeeded }));
            await replacement.ReconcileAsync(approved.ExecutionOperationId, CancellationToken.None);
            Assert.Equal(WorkflowOperationStatus.Succeeded, (await workflows.GetAsync(approved.ExecutionOperationId))?.Status);
            await customer.Received(1).SucceededAsync(Arg.Any<CancellationToken>());
            journal.ReadAsync(Arg.Any<CancellationToken>()).Returns(new SchemaJournalSnapshot(true,
                [new AppliedSchemaMigration(141, "expand", 0)]));
            await replacement.ReconcileAsync(second.ExecutionOperationId, CancellationToken.None);
            var compatible = await workflows.GetAsync(second.ExecutionOperationId);
            Assert.Equal("awaiting-compatible-deploy", compatible?.PreMigrationRestore?.Phase);
            await snapshots.Received(1).RestoreNewAsync(Arg.Any<RdsRestoreRequest>(), Arg.Any<CancellationToken>());
            var compatibleDeployment = await workflows.GetAsync(compatible!.PreMigrationRestore!.DeployOperationId!);
            Assert.NotNull(compatibleDeployment);
            Assert.True(await workflows.TrySetAsync(compatibleDeployment with { Status = WorkflowOperationStatus.Succeeded }));
            await NewWorker().ReconcileAsync(second.ExecutionOperationId, CancellationToken.None);
            Assert.Equal(WorkflowOperationStatus.Succeeded, (await workflows.GetAsync(second.ExecutionOperationId))?.Status);
            await customer.Received(2).SucceededAsync(Arg.Any<CancellationToken>());
            Assert.True(await replacement.ClaimCellAsync("other-restore", CancellationToken.None));
        }
        finally
        {
            await database.KeyDeleteAsync("controlplane:workflow:restore-cell-exclusive-owner");
        }
    }
    [IntegrationTest]
    public async Task ApprovedLocalRollback_CanTakeOverOnlyTerminalLocalFailure()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var database = connection.GetDatabase();
        await database.KeyDeleteAsync("controlplane:workflow:restore-cell-exclusive-owner");
        try
        {
            var workflows = new RedisWorkflowOperationStore(connection, NullLogger<RedisWorkflowOperationStore>.Instance);
            var now = DateTimeOffset.UtcNow;
            var failedId = "restore-local-" + Guid.NewGuid().ToString("N");
            var workflowId = "deploy-local-" + Guid.NewGuid().ToString("N");
            Assert.True(await workflows.TryCreateAsync(new WorkflowOperationRecord
            {
                OperationId = workflowId, Kind = WorkflowOperationKind.Deploy,
                Status = WorkflowOperationStatus.Reconciling, CreatedAt = now, UpdatedAt = now
            }));
            Assert.True(await workflows.TryCreateAsync(new WorkflowOperationRecord
            {
                OperationId = failedId, Kind = WorkflowOperationKind.PreMigrationRestore,
                Status = WorkflowOperationStatus.Failed, CreatedAt = now, UpdatedAt = now,
                PreMigrationRestore = new PreMigrationRestoreContext
                {
                    Phase = "awaiting-local-deploy", DeployOperationId = workflowId,
                    Payload = new DeployExecutionPayload { TargetId = "local", DesiredRevision = "prior" }.Serialize()
                }
            }));
            Assert.True(await workflows.TryCreateAsync(new WorkflowOperationRecord
            {
                OperationId = "restore-cell-exclusive-owner", Kind = WorkflowOperationKind.PreMigrationRestore,
                Status = WorkflowOperationStatus.Succeeded, CreatedAt = now, UpdatedAt = now, CurrentPhase = failedId
            }));
            var service = new DeployWorkflowService(Substitute.For<IDeployTargetRegistry>(), [workflows], [],
                Substitute.For<IOperatorApprovalEvaluator>(), NullLogger<DeployWorkflowService>.Instance);
            var monitor = Substitute.For<IOptionsMonitor<ControlPlaneOptions>>();
            monitor.CurrentValue.Returns(new ControlPlaneOptions
            {
                DeployTargets = [new DeployTargetOptions { TargetId = "local", TargetKind = DeployTargetKind.LocalCompose }]
            });
            var worker = new DurableRestoreWorker(workflows, null!, null!, new DeployOperationExecutor(service), null!, null!,
                NullLogger<DurableRestoreWorker>.Instance, monitor);
            Assert.False(await worker.ClaimCellAsync("recovery", CancellationToken.None, allowLocalRecovery: true));
            var workflow = await workflows.GetAsync(workflowId);
            Assert.True(await workflows.TrySetAsync(workflow! with { Status = WorkflowOperationStatus.Failed }));
            Assert.False(await worker.ClaimCellAsync("ordinary-deploy", CancellationToken.None));
            Assert.True(await worker.ClaimCellAsync("recovery", CancellationToken.None, allowLocalRecovery: true));
        }
        finally
        {
            await database.KeyDeleteAsync("controlplane:workflow:restore-cell-exclusive-owner");
        }
    }

}
