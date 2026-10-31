// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.ControlPlane.Executors;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.ControlPlane;

public sealed class ApprovedDeployOperationExecutorTests
{
    [UnitTest]
    public async Task ExecuteAsync_ApprovedReplay_SubmitsTheWaitingWorkflow()
    {
        var (executor, backend, read) = Create();
        var id = await executor.ExecuteAsync(new OperationGatewayRequest
        {
            Kind = OperationClass.Deploy,
            RequestedBy = "requester",
            ApprovedBy = "approver"
        }, Payload());

        Assert.NotNull(id);
        await backend.Received(1).StartAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<CancellationToken>());
        Assert.Equal(WorkflowOperationStatus.Reconciling, read()!.Status);
    }

    [UnitTest]
    public async Task ExecuteAsync_WithoutApproval_DoesNotStartTheWaitingWorkflow()
    {
        var (executor, backend, read) = Create();
        await executor.ExecuteAsync(new OperationGatewayRequest
        {
            Kind = OperationClass.Deploy,
            RequestedBy = "requester"
        }, Payload());

        await backend.DidNotReceive().StartAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<CancellationToken>());
        Assert.Equal(WorkflowOperationStatus.AwaitingApproval, read()!.Status);
    }

    private static string Payload() => new DeployExecutionPayload
    {
        TargetId = "cell",
        DesiredRevision = "prior",
        CurrentRevision = "candidate",
        Rollback = true
    }.Serialize();

    private static (DeployOperationExecutor Executor, IDeployBackend Backend, Func<WorkflowOperationRecord?> Read) Create()
    {
        WorkflowOperationRecord? saved = null;
        var target = new DeployTargetDefinition
        {
            TargetId = "cell",
            Backend = "test",
            TargetKind = DeployTargetKind.AwsEcs,
            TargetName = "service",
            Environment = "test",
            RequiresApproval = true
        };
        var targets = Substitute.For<IDeployTargetRegistry>();
        targets.GetAsync("cell", Arg.Any<CancellationToken>()).Returns(target);
        var backend = Substitute.For<IDeployBackend>();
        backend.BackendName.Returns("test");
        backend.TargetKind.Returns(DeployTargetKind.AwsEcs);
        backend.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new DeployBackendCapabilities());
        backend.PlanAsync(Arg.Any<DeployOperationSpec>(), Arg.Any<CancellationToken>())
            .Returns(new DeployPlan { RequiresApproval = true });
        backend.StartAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<CancellationToken>())
            .Returns(new DeploySubmissionResult { Status = WorkflowOperationStatus.Reconciling });
        var store = Substitute.For<IWorkflowOperationStore>();
        store.TryCreateAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call => { saved = call.Arg<WorkflowOperationRecord>(); return true; });
        store.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => saved);
        store.TrySetAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call => { saved = call.Arg<WorkflowOperationRecord>(); return true; });
        store.SetAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call => { saved = call.Arg<WorkflowOperationRecord>(); return Task.CompletedTask; });
        var service = new DeployWorkflowService(targets, [store], [backend],
            Substitute.For<IOperatorApprovalEvaluator>(), NullLogger<DeployWorkflowService>.Instance);
        return (new DeployOperationExecutor(service), backend, () => saved);
    }
}
