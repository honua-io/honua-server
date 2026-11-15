// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
using NSubstitute;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed partial class DeployWorkflowServiceTests
{
    [UnitTest]
    public async Task SubmitAsync_TransientPlanWarning_AllowsSubmissionAfterRecovery()
    {
        var store = new TestWorkflowOperationStore();
        var backend = Substitute.For<IDeployBackend>();
        backend.BackendName.Returns("honua-gitops-kubernetes");
        backend.TargetKind.Returns(DeployTargetKind.Kubernetes);
        backend.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new DeployBackendCapabilities());
        backend.PlanAsync(Arg.Any<DeployOperationSpec>(), Arg.Any<CancellationToken>())
            .Returns(new DeployPlan
            {
                IsReadyToSubmit = false,
                Warnings = ["Deployment alarms are not OK."]
            });
        var service = CreateService(store, backend);

        var operation = await service.CreateAsync("prod-api", "sha256:new", "sha256:old", "alice", "Deploy",
            "alarm-plan", "corr", OperationPriority.Normal, submitImmediately: true, parameterOverrides: null);

        operation!.Status.Should().Be(WorkflowOperationStatus.Planned);
        operation.BlockingReasons.Should().BeEmpty();
        await backend.DidNotReceive().StartAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<CancellationToken>());
        backend.StartAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<CancellationToken>())
            .Returns(new DeploySubmissionResult { Status = WorkflowOperationStatus.Submitted });

        var submitted = await service.SubmitAsync(operation.OperationId, "alice", "alarms recovered");

        submitted!.Status.Should().Be(WorkflowOperationStatus.Submitted);
        await backend.Received(1).StartAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<CancellationToken>());
    }

    [UnitTheory]
    [InlineData("create")]
    [InlineData("submit")]
    public async Task SubmitAsync_AdmissionRefusedAfterPlanning_PreservesRetry(string entryPoint)
    {
        var store = new TestWorkflowOperationStore();
        var backend = Substitute.For<IDeployBackend>();
        backend.BackendName.Returns("honua-gitops-kubernetes");
        backend.TargetKind.Returns(DeployTargetKind.Kubernetes);
        backend.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new DeployBackendCapabilities());
        backend.PlanAsync(Arg.Any<DeployOperationSpec>(), Arg.Any<CancellationToken>())
            .Returns(new DeployPlan { IsReadyToSubmit = true });
        backend.ObserveAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<CancellationToken>())
            .Returns(new DeployObservation { Status = WorkflowOperationStatus.Reconciling });
        backend.StartAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<CancellationToken>())
            .Returns<Task<DeploySubmissionResult>>(_ => throw new DeployAdmissionBlockedException("Deployment alarms are not OK."));
        var service = CreateService(store, backend);
        var operation = await service.CreateAsync("prod-api", "sha256:new", "sha256:old", "alice", "Deploy",
            "alarm-race", "corr", OperationPriority.Normal, submitImmediately: entryPoint == "create", parameterOverrides: null);
        if (entryPoint == "submit")
        {
            operation = await service.SubmitAsync(operation!.OperationId, "alice", "submit");
        }

        operation!.Status.Should().Be(WorkflowOperationStatus.Planned);
        operation.CompletedAt.Should().BeNull();
        (await store.GetAsync(operation.OperationId))!.Status.Should().Be(WorkflowOperationStatus.Planned);
        backend.StartAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<CancellationToken>())
            .Returns(new DeploySubmissionResult { Status = WorkflowOperationStatus.Submitted });

        var retried = await service.SubmitAsync(operation.OperationId, "alice", "retry");

        retried!.Status.Should().Be(WorkflowOperationStatus.Submitted);
        await backend.Received(2).StartAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task AdvanceOrPromoteAsync_AdmissionRefused_KeepsObservedStateAndRecordsReason()
    {
        var backend = Substitute.For<IDeployBackend>();
        backend.PromoteAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<CancellationToken>())
            .Returns<Task<DeployObservation>>(_ => throw new DeployAdmissionBlockedException("Deployment alarms are not OK."));
        var current = CreateOperationRecord(WorkflowOperationStatus.Reconciling) with
        {
            CurrentPhase = "Candidate healthy.",
            ObservedState = "rev-2"
        };

        var advanced = await DeployWorkflowReconciler.AdvanceOrPromoteAsync(current, backend, CancellationToken.None);

        advanced.Status.Should().Be(WorkflowOperationStatus.Reconciling);
        advanced.CurrentPhase.Should().Be("Candidate healthy.");
        advanced.ObservedState.Should().Be("rev-2");
        advanced.ErrorMessage.Should().Be("Deployment alarms are not OK.");
        advanced.CompletedAt.Should().BeNull();
    }

    [UnitTest]
    public async Task Reconciler_CompletionAdmissionRefused_PersistsHealthyTelemetryReset()
    {
        var store = new TestWorkflowOperationStore();
        var backend = Substitute.For<IDeployBackend>();
        backend.BackendName.Returns("honua-gitops-kubernetes");
        backend.TargetKind.Returns(DeployTargetKind.Kubernetes);
        backend.ObserveAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<CancellationToken>())
            .Returns(new DeployObservation { Status = WorkflowOperationStatus.Reconciling });
        backend.CompleteProtectionAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<CancellationToken>())
            .Returns<Task<DeployObservation>>(_ => throw new DeployAdmissionBlockedException("Deployment alarms are not OK."));
        var operation = CreateOperationRecord(WorkflowOperationStatus.Reconciling,
            parameters: new Dictionary<string, string> { [DeployTelemetrySignalEvaluator.BreachStreakParameterKey] = "1" });
        operation = operation with { Deploy = operation.Deploy! with { Protection = new DeployProtectionState
        {
            Phase = DeployProtectionPhase.Observing,
            PreviousRevision = "sha256:old", CandidateRevision = "sha256:new",
            FirstExposureAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            ObservationDeadline = DateTimeOffset.UtcNow.AddSeconds(-1), PolicyDigest = "test"
        } } };
        await store.TryCreateAsync(operation);
        var evaluator = new StubDeployTelemetrySignalEvaluator(new DeployTelemetryDecision
        {
            UpdatedDeployParameters = new Dictionary<string, string> { [DeployTelemetrySignalEvaluator.BreachStreakParameterKey] = "0" }
        });

        await CreateReconciler(store, backend, evaluator).ReconcileWorkflowOperationAsync(operation.OperationId);

        var retained = (await store.GetAsync(operation.OperationId))!;
        retained.Status.Should().Be(WorkflowOperationStatus.Reconciling);
        retained.CompletedAt.Should().BeNull();
        retained.Deploy!.Protection!.Phase.Should().Be(DeployProtectionPhase.Observing);
        retained.Deploy.Parameters[DeployTelemetrySignalEvaluator.BreachStreakParameterKey].Should().Be("0");
        retained.ErrorMessage.Should().Be("Deployment alarms are not OK.");
        await backend.Received(1).CompleteProtectionAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task PromoteAsync_AdmissionRefusedDuringCanary_PreservesRollback()
    {
        var store = new TestWorkflowOperationStore();
        var backend = Substitute.For<IDeployBackend>();
        backend.BackendName.Returns("honua-gitops-kubernetes");
        backend.TargetKind.Returns(DeployTargetKind.Kubernetes);
        backend.PromoteAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<CancellationToken>())
            .Returns<Task<DeployObservation>>(_ => throw new DeployAdmissionBlockedException("Deployment alarms are not OK."));
        backend.RollbackAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<CancellationToken>())
            .Returns(new DeployObservation { Status = WorkflowOperationStatus.RollbackRequested });
        var operation = CreateOperationRecord(WorkflowOperationStatus.Reconciling);
        await store.TryCreateAsync(operation);
        var service = CreateService(store, backend);

        var refused = await service.PromoteAsync(operation.OperationId, "alice", "promote");

        refused.Outcome.Should().Be(DeployWorkflowService.DeployPromotionOutcome.PreconditionFailed);
        (await store.GetAsync(operation.OperationId))!.Status.Should().Be(WorkflowOperationStatus.Reconciling);
        var rollback = await service.RequestRollbackAsync(operation.OperationId, "alice", "alarm recovery");
        rollback!.Status.Should().Be(WorkflowOperationStatus.RollbackRequested);
        await backend.Received(1).RollbackAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<CancellationToken>());
    }

    [UnitTheory]
    [InlineData(ICheckpointedDeployBackend.PendingPhase, WorkflowOperationStatus.Submitted)]
    [InlineData(ICheckpointedDeployBackend.PendingPhase, WorkflowOperationStatus.Reconciling)]
    [InlineData(ICheckpointedDeployBackend.PreparedPhase, WorkflowOperationStatus.Reconciling)]
    [InlineData(ICheckpointedDeployBackend.CutoverPhase, WorkflowOperationStatus.Submitted)]
    [InlineData(ICheckpointedDeployBackend.CutoverPhase, WorkflowOperationStatus.Reconciling)]
    [InlineData(ICheckpointedDeployBackend.RampPhase, WorkflowOperationStatus.Reconciling)]
    public async Task Reconciler_AdmissionRefusedDuringCheckpoint_PreservesRollback(
        string phase, WorkflowOperationStatus status)
    {
        var store = new TestWorkflowOperationStore();
        var backend = Substitute.For<IDeployBackend, ICheckpointedDeployBackend>();
        backend.BackendName.Returns("honua-gitops-kubernetes");
        backend.TargetKind.Returns(DeployTargetKind.Kubernetes);
        ((ICheckpointedDeployBackend)backend).RequiresCheckpoint(Arg.Any<WorkflowOperationRecord>()).Returns(true);
        backend.ObserveAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<CancellationToken>())
            .Returns(new DeployObservation { Status = WorkflowOperationStatus.Reconciling });
        backend.StartAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<CancellationToken>())
            .Returns<Task<DeploySubmissionResult>>(_ => throw new DeployAdmissionBlockedException("Deployment alarms are not OK."));
        ((ICheckpointedDeployBackend)backend).EnsurePreparationAdmittedAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new DeployAdmissionBlockedException("Deployment alarms are not OK."));
        ((ICheckpointedDeployBackend)backend).ActivateCheckpointAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<CancellationToken>())
            .Returns<Task<DeployObservation>>(_ => throw new DeployAdmissionBlockedException("Deployment alarms are not OK."));
        backend.PromoteAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<CancellationToken>())
            .Returns<Task<DeployObservation>>(_ => throw new DeployAdmissionBlockedException("Deployment alarms are not OK."));
        backend.RollbackAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<CancellationToken>())
            .Returns(new DeployObservation { Status = WorkflowOperationStatus.RollbackRequested });
        var operation = CreateOperationRecord(status) with
        {
            CurrentPhase = phase
        };
        await store.TryCreateAsync(operation);

        await CreateReconciler(store, backend).ReconcileWorkflowOperationAsync(operation.OperationId);

        var retained = await store.GetAsync(operation.OperationId);
        retained!.Status.Should().Be(status);
        retained.CurrentPhase.Should().Be(phase);
        retained.CompletedAt.Should().BeNull();
        retained.ErrorMessage.Should().Be("Deployment alarms are not OK.");
        await ((ICheckpointedDeployBackend)backend).DidNotReceive()
            .PrepareCheckpointAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<CancellationToken>());
        var rollback = await CreateService(store, backend).RequestRollbackAsync(operation.OperationId, "alice", "recover");
        rollback!.Status.Should().Be(WorkflowOperationStatus.RollbackRequested);
        await backend.Received(1).RollbackAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task Reconciler_PreparationAdmittedAfterRefusal_ClearsRefusalMessage()
    {
        var store = new TestWorkflowOperationStore();
        var backend = Substitute.For<IDeployBackend, ICheckpointedDeployBackend>();
        backend.BackendName.Returns("honua-gitops-kubernetes");
        backend.TargetKind.Returns(DeployTargetKind.Kubernetes);
        var checkpointed = (ICheckpointedDeployBackend)backend;
        checkpointed.RequiresCheckpoint(Arg.Any<WorkflowOperationRecord>()).Returns(true);
        checkpointed.PrepareCheckpointAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<CancellationToken>())
            .Returns(new DeployCheckpoint { CandidateRevision = "rev-2", StableRevision = "rev-1", OriginalServiceRevision = "rev-0" });
        var operation = CreateOperationRecord(WorkflowOperationStatus.Submitted) with
        {
            CurrentPhase = ICheckpointedDeployBackend.PendingPhase,
            ErrorMessage = "Deployment alarms are not OK."
        };
        await store.TryCreateAsync(operation);

        await CreateReconciler(store, backend).ReconcileWorkflowOperationAsync(operation.OperationId);

        var prepared = await store.GetAsync(operation.OperationId);
        prepared!.CurrentPhase.Should().Be(ICheckpointedDeployBackend.PreparedPhase);
        prepared.ErrorMessage.Should().BeNull();
    }
}
