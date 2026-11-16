// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed partial class DeployWorkflowServiceTests
{
    [UnitTheory]
    [InlineData("alice")]
    [InlineData("bob")]
    public async Task SubmitAsync_ApprovalRequired_RequiresHumanAuthority(string actor)
    {
        var store = new TestWorkflowOperationStore();
        var backend = new ImmediateDeployBackend();
        var service = CreateService(store, backend);
        var operation = CreateOperationRecord(WorkflowOperationStatus.AwaitingApproval);
        operation = operation with { Deploy = operation.Deploy! with { RequiresApproval = true } };
        await store.TryCreateAsync(operation);

        var submit = () => service.SubmitAsync(operation.OperationId, actor, "Reviewed");

        await submit.Should().ThrowAsync<HumanApprovalRequiredException>();
        backend.StartCount.Should().Be(0);
        (await store.GetAsync(operation.OperationId))!.Status.Should().Be(WorkflowOperationStatus.AwaitingApproval);
    }

    [UnitTest]
    public async Task SubmitAsync_ComposePlan_RequiresHumanAuthority()
    {
        var store = new TestWorkflowOperationStore();
        var backend = new ImmediateDeployBackend();
        var service = CreateService(store, backend);
        var operation = CreateOperationRecord(WorkflowOperationStatus.Planned);
        operation = operation with
        {
            Deploy = operation.Deploy! with { TargetKind = DeployTargetKind.LocalCompose },
            Audit = operation.Audit with { RequestedByActorId = "requester" },
        };
        await store.TryCreateAsync(operation);

        var submit = () => service.SubmitAsync(operation.OperationId, "approver", "Reviewed");

        await submit.Should().ThrowAsync<HumanApprovalRequiredException>();
        backend.StartCount.Should().Be(0);
    }

    [UnitTest]
    public async Task SubmitAsync_WithoutApprovalRequirement_PreservesApprovalAudit()
    {
        var store = new TestWorkflowOperationStore();
        var backend = new ImmediateDeployBackend();
        var service = CreateService(store, backend);
        var operation = CreateOperationRecord(WorkflowOperationStatus.Planned);
        await store.TryCreateAsync(operation);

        var result = await service.SubmitAsync(operation.OperationId, "alice", "Submit");

        backend.StartCount.Should().Be(1);
        result!.Audit.ApprovedBy.Should().BeNull();
    }

    [UnitTest]
    public async Task PromoteAsync_ManualCutover_RequiresHumanAuthority()
    {
        var store = new TestWorkflowOperationStore();
        var backend = new ImmediateDeployBackend();
        var service = CreateService(store, backend);
        var operation = CreateOperationRecord(WorkflowOperationStatus.Reconciling);
        await store.TryCreateAsync(operation);

        var promote = () => service.PromoteAsync(operation.OperationId, "alice", "Reviewed");

        await promote.Should().ThrowAsync<HumanApprovalRequiredException>();
        (await store.GetAsync(operation.OperationId))!.Status.Should().Be(WorkflowOperationStatus.Reconciling);
    }

    [UnitTest]
    public async Task SubmitAsync_CurrentHumanReview_RecordsApproverAndStartsDeployment()
    {
        using var host = new OperationHumanApprovalTestHost();
        var human = await host.CreateHumanAsync();
        var store = new TestWorkflowOperationStore();
        var backend = new ImmediateDeployBackend();
        var operation = CreateOperationRecord(WorkflowOperationStatus.AwaitingApproval);
        operation = operation with { Deploy = operation.Deploy! with { RequiresApproval = true } };
        await store.TryCreateAsync(operation);
        operation = (await store.GetAsync(operation.OperationId))!;
        host.DeployReviewRequest(human, operation, "submit");
        var review = await host.Authority.OpenDeployReviewAsync(operation, "submit", host.Context);
        host.DeployApproveRequest(human, operation, "submit", review);
        var actor = OperationHumanApprovalTestHost.Approval(human).ApprovedBy;

        var result = await CreateService(store, backend, humanApprovalAuthority: host.Authority)
            .SubmitAsync(operation.OperationId, actor, "Reviewed");

        backend.StartCount.Should().Be(1);
        result!.Audit.ApprovedBy.Should().Be(actor);
    }

    [UnitTheory]
    [InlineData("version")]
    [InlineData("payload")]
    [InlineData("session")]
    [InlineData("action")]
    public async Task SubmitAsync_ReviewBindingChanged_LeavesDeploymentAwaitingReview(string change)
    {
        using var host = new OperationHumanApprovalTestHost();
        var human = await host.CreateHumanAsync();
        var store = new TestWorkflowOperationStore();
        var backend = new ImmediateDeployBackend();
        var operation = CreateOperationRecord(WorkflowOperationStatus.AwaitingApproval);
        operation = operation with { Deploy = operation.Deploy! with { RequiresApproval = true } };
        await store.TryCreateAsync(operation);
        operation = (await store.GetAsync(operation.OperationId))!;
        var reviewed = change == "action" ? operation with { Status = WorkflowOperationStatus.Reconciling } : operation;
        var action = change == "action" ? "promote" : "submit";
        host.DeployReviewRequest(human, reviewed, action);
        var review = await host.Authority.OpenDeployReviewAsync(reviewed, action, host.Context);
        if (change == "version")
            await store.TrySetAsync(operation with { UpdatedAt = operation.UpdatedAt.AddSeconds(1) });
        if (change == "payload")
            review = await host.Authority.OpenDeployReviewAsync(operation with
            { Deploy = operation.Deploy! with { DesiredRevision = "sha256:other" } }, "submit", host.Context);
        if (change == "session")
            await host.Sessions.RemoveAuthenticatedSessionAsync(human.SessionId, CancellationToken.None);
        host.DeployApproveRequest(human, operation, "submit", review);
        var service = CreateService(store, backend, humanApprovalAuthority: host.Authority);
        var submit = () => service.SubmitAsync(operation.OperationId,
            OperationHumanApprovalTestHost.Approval(human).ApprovedBy, "Reviewed");

        await submit.Should().ThrowAsync<HumanApprovalRequiredException>();
        backend.StartCount.Should().Be(0);
        (await store.GetAsync(operation.OperationId))!.Status.Should().Be(WorkflowOperationStatus.AwaitingApproval);
    }

    [UnitTest]
    public async Task PromoteAsync_CurrentHumanReview_RecordsApproverAndPromotesDeployment()
    {
        using var host = new OperationHumanApprovalTestHost();
        var human = await host.CreateHumanAsync();
        var store = new TestWorkflowOperationStore();
        var backend = new StagedDeployBackend();
        var operation = CreateOperationRecord(WorkflowOperationStatus.Reconciling);
        await store.TryCreateAsync(operation);
        operation = (await store.GetAsync(operation.OperationId))!;
        host.DeployReviewRequest(human, operation, "promote");
        var review = await host.Authority.OpenDeployReviewAsync(operation, "promote", host.Context);
        host.DeployApproveRequest(human, operation, "promote", review);
        var actor = OperationHumanApprovalTestHost.Approval(human).ApprovedBy;

        var result = await CreateService(store, backend, humanApprovalAuthority: host.Authority)
            .PromoteAsync(operation.OperationId, actor, "Reviewed");

        backend.PromoteCount.Should().Be(1);
        result.Operation!.Audit.ApprovedBy.Should().Be(actor);
    }
    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubmitAsync_ComposePlanWithoutDistinctRequester_RemainsAwaitingReview(bool sameActor)
    {
        using var host = new OperationHumanApprovalTestHost();
        var human = await host.CreateHumanAsync();
        var actor = OperationHumanApprovalTestHost.Approval(human).ApprovedBy;
        var store = new TestWorkflowOperationStore();
        var backend = new ImmediateDeployBackend();
        var operation = CreateOperationRecord(WorkflowOperationStatus.AwaitingApproval);
        operation = operation with
        {
            Deploy = operation.Deploy! with { RequiresApproval = true, TargetKind = DeployTargetKind.LocalCompose },
            Audit = operation.Audit with { RequestedByActorId = sameActor ? actor : null },
        };
        await store.TryCreateAsync(operation);
        operation = (await store.GetAsync(operation.OperationId))!;
        host.DeployReviewRequest(human, operation, "submit");
        var review = await host.Authority.OpenDeployReviewAsync(operation, "submit", host.Context);
        host.DeployApproveRequest(human, operation, "submit", review);
        var service = CreateService(store, backend, humanApprovalAuthority: host.Authority);
        var submit = () => service.SubmitAsync(operation.OperationId, actor, "Reviewed");

        await submit.Should().ThrowAsync<Honua.Core.Exceptions.ResourceConflictException>();
        backend.StartCount.Should().Be(0);
        (await store.GetAsync(operation.OperationId))!.Status.Should().Be(WorkflowOperationStatus.AwaitingApproval);
    }

    [UnitTest]
    public async Task PromoteAsync_ProviderCancellation_PreservesDurableApproval()
    {
        using var host = new OperationHumanApprovalTestHost();
        var human = await host.CreateHumanAsync();
        var store = new TestWorkflowOperationStore();
        WorkflowOperationRecord? providerOperation = null;
        var backend = new StagedDeployBackend
        {
            PromotionObserved = operation =>
            {
                providerOperation = operation;
                throw new OperationCanceledException();
            }
        };
        var operation = CreateOperationRecord(WorkflowOperationStatus.Reconciling);
        await store.TryCreateAsync(operation);
        operation = (await store.GetAsync(operation.OperationId))!;
        host.DeployReviewRequest(human, operation, "promote");
        var review = await host.Authority.OpenDeployReviewAsync(operation, "promote", host.Context);
        host.DeployApproveRequest(human, operation, "promote", review);
        var actor = OperationHumanApprovalTestHost.Approval(human).ApprovedBy;
        var service = CreateService(store, backend, humanApprovalAuthority: host.Authority);
        var promote = () => service.PromoteAsync(operation.OperationId, actor, "Reviewed");

        await promote.Should().ThrowAsync<OperationCanceledException>();

        var saved = (await store.GetAsync(operation.OperationId))!;
        saved.Version.Should().BeGreaterThan(operation.Version);
        saved.Audit.ApprovedBy.Should().Be(actor);
        providerOperation!.Version.Should().Be(saved.Version);
        providerOperation.Audit.ApprovedBy.Should().Be(actor);
        saved.Audit.DeployHumanApproval.Should().NotBeNull();
        saved.Audit.DeployHumanApproval!.ActorId.Should().Be(actor);
        saved.Audit.DeployHumanApproval.SessionReference.Should().NotBeNullOrWhiteSpace();
        saved.Audit.DeployHumanApproval.CapabilityReference.Should().NotBeNullOrWhiteSpace();
        saved.Audit.DeployHumanApproval.WorkflowVersion.Should().Be(operation.Version);
        saved.Deploy!.ManualPromotionPending.Should().BeTrue();
        (await promote()).Outcome.Should().Be(DeployPromotionOutcome.PreconditionFailed);
        saved.Audit.DeployHumanApproval.Action.Should().Be("promote");
        backend.PromoteCount.Should().Be(1);

        await CreateReconciler(store, backend).ReconcileWorkflowOperationAsync(operation.OperationId);

        var observed = (await store.GetAsync(operation.OperationId))!;
        observed.Status.Should().Be(WorkflowOperationStatus.ManualInterventionRequired);
        observed.Deploy!.ManualPromotionPending.Should().BeFalse();
        observed.Audit.DeployHumanApproval.Should().Be(saved.Audit.DeployHumanApproval);
        backend.PromoteCount.Should().Be(1);
    }

    [UnitTest]
    public async Task PromoteAsync_ConcurrentRollback_PreservesNewerIntent()
    {
        using var host = new OperationHumanApprovalTestHost();
        var human = await host.CreateHumanAsync();
        var store = new TestWorkflowOperationStore();
        var backend = new StagedDeployBackend
        {
            PromotionObserved = async pending =>
            {
                (await store.TrySetAsync(pending with
                {
                    Status = WorkflowOperationStatus.RollbackRequested,
                    UpdatedAt = pending.UpdatedAt.AddSeconds(1)
                })).Should().BeTrue();
            }
        };
        var operation = CreateOperationRecord(WorkflowOperationStatus.Reconciling);
        await store.TryCreateAsync(operation);
        operation = (await store.GetAsync(operation.OperationId))!;
        host.DeployReviewRequest(human, operation, "promote");
        var review = await host.Authority.OpenDeployReviewAsync(operation, "promote", host.Context);
        host.DeployApproveRequest(human, operation, "promote", review);
        var actor = OperationHumanApprovalTestHost.Approval(human).ApprovedBy;

        var result = await CreateService(store, backend, humanApprovalAuthority: host.Authority)
            .PromoteAsync(operation.OperationId, actor, "Reviewed");

        backend.PromoteCount.Should().Be(1);
        result.Operation!.Status.Should().Be(WorkflowOperationStatus.RollbackRequested);
        var saved = (await store.GetAsync(operation.OperationId))!;
        saved.Status.Should().Be(WorkflowOperationStatus.RollbackRequested);
        saved.Audit.DeployHumanApproval!.ActorId.Should().Be(actor);
    }

}
