// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Honua.ControlPlane;
using Honua.ControlPlane.Executors;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.ControlPlane.Restore;
using Honua.Core.Features.Guardrails.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.ControlPlane;

public sealed class DurableRestoreWorkerTests
{
    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Reconcile_InterruptedMutation_RecordsFailureWithoutRepeatingIt()
    {
        var harness = new Harness("acting");
        await harness.Worker.ReconcileAsync(harness.RestoreId, CancellationToken.None);
        Assert.Equal(WorkflowOperationStatus.Failed, harness.Restore.Status);
        Assert.Equal(PreMigrationRestoreCodes.RestoreIncomplete, harness.Restore.ErrorMessage);
        await harness.Audit.DidNotReceive().AppendAsync(Arg.Any<PreMigrationRestoreAuditLine>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Reconcile_ProtectionWindowOpen_WaitsAndThenVerifiesBeforeSuccess()
    {
        var harness = new Harness("awaiting-deploy");
        await harness.Worker.ReconcileAsync(harness.RestoreId, CancellationToken.None);
        Assert.Equal(WorkflowOperationStatus.Reconciling, harness.Restore.Status);
        await harness.Customer.DidNotReceive().SucceededAsync(Arg.Any<CancellationToken>());

        harness.Deployment = harness.Deployment with { Status = WorkflowOperationStatus.Succeeded };
        await harness.Worker.ReconcileAsync(harness.RestoreId, CancellationToken.None);
        Assert.Equal(WorkflowOperationStatus.Succeeded, harness.Restore.Status);
        await harness.Customer.Received(1).SucceededAsync(Arg.Any<CancellationToken>());
        await harness.Audit.Received(1).AppendAsync(Arg.Is<PreMigrationRestoreAuditLine>(line => line.Ok && line.ReadbackComplete), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Reconcile_ChangedDatabaseBinding_FailsWithoutCustomerSuccess()
    {
        var harness = new Harness("awaiting-deploy");
        harness.Deployment = harness.Deployment with { Status = WorkflowOperationStatus.Succeeded };
        harness.Secrets.ReadHostAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("another-db.internal");
        await harness.Worker.ReconcileAsync(harness.RestoreId, CancellationToken.None);
        Assert.Equal(WorkflowOperationStatus.Failed, harness.Restore.Status);
        await harness.Customer.DidNotReceive().SucceededAsync(Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Reconcile_ProposalNotDurablySubmitted_DoesNotActuate()
    {
        var harness = new Harness("queued");
        await harness.Worker.ReconcileAsync(harness.RestoreId, CancellationToken.None);
        Assert.Equal(WorkflowOperationStatus.Submitted, harness.Restore.Status);
        await harness.Audit.DidNotReceive().AppendAsync(Arg.Any<PreMigrationRestoreAuditLine>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Reconcile_InterruptedBeforeDeploySubmission_FailsClosed()
    {
        var harness = new Harness("awaiting-deploy");
        harness.Deployment = harness.Deployment with { Status = WorkflowOperationStatus.AwaitingApproval };
        await harness.Worker.ReconcileAsync(harness.RestoreId, CancellationToken.None);
        Assert.Equal(WorkflowOperationStatus.Failed, harness.Restore.Status);
        await harness.Customer.DidNotReceive().SucceededAsync(Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Reconcile_AuditFailure_DoesNotReportSuccess()
    {
        var harness = new Harness("awaiting-deploy");
        harness.Deployment = harness.Deployment with { Status = WorkflowOperationStatus.Succeeded };
        harness.Audit.AppendAsync(Arg.Any<PreMigrationRestoreAuditLine>(), Arg.Any<CancellationToken>()).Returns(false);
        await harness.Worker.ReconcileAsync(harness.RestoreId, CancellationToken.None);
        Assert.Equal(WorkflowOperationStatus.Failed, harness.Restore.Status);
        Assert.Equal(PreMigrationRestoreCodes.AuditFailed, harness.Restore.ErrorMessage);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Enqueue_SelfApproval_IsRejectedBeforeWriting()
    {
        var store = Substitute.For<IWorkflowOperationStore>();
        var queue = new DurableRestoreQueue(store);
        var request = new OperationGatewayRequest
        {
            Kind = OperationClass.Deploy,
            RequestedBy = "same",
            ApprovedBy = "same",
            ApprovedProposalId = "proposal"
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.EnqueueAsync(request, "{}", CancellationToken.None));
        await store.DidNotReceive().TryCreateAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    private sealed class Harness
    {
        public string RestoreId => "restore-test";
        public IPreMigrationRestoreAudit Audit { get; } = Substitute.For<IPreMigrationRestoreAudit>();
        public IServingCustomerProbe Customer { get; } = Substitute.For<IServingCustomerProbe>();
        public IConnectionSecretRetarget Secrets { get; } = Substitute.For<IConnectionSecretRetarget>();
        public WorkflowOperationRecord Restore { get; private set; }
        public WorkflowOperationRecord Deployment { get; set; }
        public DurableRestoreWorker Worker { get; }

        public Harness(string phase)
        {
            const string host = "db.internal";
            var payload = new DeployExecutionPayload { TargetId = "cell", DesiredRevision = "prior", Rollback = true }.Serialize();
            Restore = new WorkflowOperationRecord
            {
                OperationId = RestoreId,
                Kind = WorkflowOperationKind.PreMigrationRestore,
                Status = phase == "queued" ? WorkflowOperationStatus.Submitted : WorkflowOperationStatus.Reconciling,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                PreMigrationRestore = new PreMigrationRestoreContext
                {
                    Payload = payload,
                    Phase = phase,
                    ApprovedBy = "approver",
                    DeployOperationId = "deploy-test",
                    ExpectedSchema = 140,
                    RestoredHostHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(host))).ToLowerInvariant()
                }
            };
            Deployment = new WorkflowOperationRecord
            {
                OperationId = "deploy-test",
                Kind = WorkflowOperationKind.Deploy,
                Status = WorkflowOperationStatus.Reconciling,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            var store = Substitute.For<IWorkflowOperationStore>();
            store.TryAcquireLeaseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(true);
            store.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
                call.Arg<string>() == RestoreId ? Restore : Deployment);
            store.TrySetAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                Restore = call.Arg<WorkflowOperationRecord>() with { Version = Restore.Version + 1 };
                return true;
            });
            var revisions = Substitute.For<IRevisionSchemaCatalog>();
            revisions.MaxSchema("prior").Returns(140);
            var probe = Substitute.For<IRestoredSchemaProbe>();
            probe.ProbeAsync(host, Arg.Any<CancellationToken>()).Returns(new SchemaJournalSnapshot(true, [new AppliedSchemaMigration(140, "expand", 0)]));
            Secrets.ReadHostAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(host);
            Customer.SucceededAsync(Arg.Any<CancellationToken>()).Returns(true);
            Audit.AppendAsync(Arg.Any<PreMigrationRestoreAuditLine>(), Arg.Any<CancellationToken>()).Returns(true);
            var verifier = new DurableRestoreCompletionVerifier(probe, revisions, Customer, Audit, Secrets,
                Options.Create(new RestoreCellOptions { ConnectionSecretId = "honua-cert-db" }));
            var workflow = new DeployWorkflowService(Substitute.For<IDeployTargetRegistry>(), [store], [],
                Substitute.For<IOperatorApprovalEvaluator>(), NullLogger<DeployWorkflowService>.Instance);
            Worker = new DurableRestoreWorker(store, Substitute.For<IOperationProposalStore>(), null!,
                new DeployOperationExecutor(workflow), verifier, Audit, NullLogger<DurableRestoreWorker>.Instance);
        }
    }
}
