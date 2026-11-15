// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using NSubstitute;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed partial class DeployWorkflowServiceTests
{
    [UnitTheory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Reconciler_AdmissionHeldRamp_MonitorsExposedCandidate(bool providerFailure, bool telemetryFailure)
    {
        var store = new TestWorkflowOperationStore();
        var backend = Substitute.For<IDeployBackend, ICheckpointedDeployBackend>();
        backend.BackendName.Returns("honua-gitops-kubernetes");
        backend.TargetKind.Returns(DeployTargetKind.Kubernetes);
        ((ICheckpointedDeployBackend)backend).RequiresCheckpoint(Arg.Any<WorkflowOperationRecord>()).Returns(true);
        backend.StartAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<CancellationToken>())
            .Returns<Task<DeploySubmissionResult>>(_ => throw new DeployAdmissionBlockedException("Deployment alarms are not OK."));
        backend.ObserveAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<CancellationToken>())
            .Returns(new DeployObservation
            {
                Status = providerFailure ? WorkflowOperationStatus.Failed : WorkflowOperationStatus.Reconciling,
                PromotionRecommended = !providerFailure,
                RollbackRecommended = providerFailure,
                ObservedRevision = "sha256:new",
                Message = providerFailure ? "Candidate health failed." : "Candidate healthy."
            });
        backend.RollbackAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<CancellationToken>())
            .Returns(new DeployObservation { Status = WorkflowOperationStatus.RollbackRequested });
        var evaluator = Substitute.For<IDeployTelemetrySignalEvaluator>();
        evaluator.EvaluateAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<CancellationToken>())
            .Returns(new DeployTelemetryDecision
            {
                RollbackRecommended = telemetryFailure,
                Message = telemetryFailure ? "Error rate exceeded threshold." : "Healthy telemetry.",
                UpdatedDeployParameters = new Dictionary<string, string> { ["breach-count"] = "1" }
            });
        var operation = CreateOperationRecord(WorkflowOperationStatus.Reconciling);
        operation = operation with
        {
            CurrentPhase = ICheckpointedDeployBackend.RampPhase,
            Deploy = operation.Deploy! with
            {
                TrafficExposedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                CanaryRamp = new CanaryRampSpec
                {
                    StepWeights = [10, 50, 100],
                    CurrentStepIndex = 1,
                    StepBakeDuration = TimeSpan.FromMinutes(5)
                }
            }
        };
        await store.TryCreateAsync(operation);

        await CreateReconciler(store, backend, evaluator).ReconcileWorkflowOperationAsync(operation.OperationId);

        var retained = (await store.GetAsync(operation.OperationId))!;
        await backend.Received(1).ObserveAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<CancellationToken>());
        await backend.DidNotReceive().PromoteAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<CancellationToken>());
        retained.Deploy!.CanaryRamp!.CurrentStepStartedAt.Should().BeNull();
        if (providerFailure || telemetryFailure)
        {
            retained.Status.Should().Be(WorkflowOperationStatus.RollbackRequested);
            await backend.Received(1).RollbackAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<CancellationToken>());
        }
        else
        {
            retained.Status.Should().Be(WorkflowOperationStatus.Reconciling);
            retained.CurrentPhase.Should().Be(ICheckpointedDeployBackend.RampPhase);
            retained.ErrorMessage.Should().Be("Deployment alarms are not OK.");
            retained.CompletedAt.Should().BeNull();
            retained.Deploy.Parameters["breach-count"].Should().Be("1");
            await backend.DidNotReceive().RollbackAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<CancellationToken>());
        }
    }
}
