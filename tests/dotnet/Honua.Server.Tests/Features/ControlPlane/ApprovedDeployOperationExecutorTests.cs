// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.ControlPlane.Executors;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.TestKit.Attributes;
using Honua.Server.Tests.Infrastructure.ControlPlane;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.ControlPlane;

public sealed class ApprovedDeployOperationExecutorTests
{
    [UnitTest]
    public async Task ExecuteAsync_ApprovedReplay_SubmitsTheWaitingWorkflow()
    {
        using var host = new OperationHumanApprovalTestHost();
        var proposal = await DeployHumanApprovalReplayTests.CreateApprovedProposalAsync(host, false);
        var (executor, backend, read) = Create(proposal);
        var id = await executor.ExecuteAsync(DeployHumanApprovalReplayTests.CreateExecutionRequest(proposal),
            proposal.Plan.ExecutionPayload);

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

    [UnitTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_ComposeHumanApproval_AllowsRequesterOrAnotherHuman(bool requesterApproves)
    {
        using var host = new OperationHumanApprovalTestHost();
        var proposal = await DeployHumanApprovalReplayTests.CreateApprovedProposalAsync(host, requesterApproves);
        var (executor, backend, read) = Create(proposal, DeployTargetKind.LocalCompose);

        await executor.ExecuteAsync(DeployHumanApprovalReplayTests.CreateExecutionRequest(proposal), proposal.Plan.ExecutionPayload);

        await backend.Received(1).StartAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<CancellationToken>());
        Assert.Equal(proposal.ResolvedBy, read()!.Audit.ApprovedBy);
    }

    [UnitTheory]
    [InlineData("proof")]
    [InlineData("binding")]
    [InlineData("payload")]
    [InlineData("instance")]
    public async Task ExecuteAsync_ChangedHumanApproval_RefusesBeforeCreatingWorkflow(string change)
    {
        using var host = new OperationHumanApprovalTestHost();
        var original = await DeployHumanApprovalReplayTests.CreateApprovedProposalAsync(host, true);
        var proposal = change switch
        {
            "proof" => original with { HumanApproval = null },
            "binding" => original with { HumanApproval = original.HumanApproval! with { BindingDigest = new string('0', 64) } },
            _ => original,
        };
        var request = DeployHumanApprovalReplayTests.CreateExecutionRequest(original);
        if (change == "instance") request = request with { OperationInstanceId = "changed-instance" };
        var payload = change == "payload" ? Payload() : original.Plan.ExecutionPayload;
        var (executor, backend, read) = Create(proposal, DeployTargetKind.LocalCompose);

        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(request, payload));

        Assert.Null(read());
        await backend.DidNotReceive().StartAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<CancellationToken>());
    }

    private static string Payload() => new DeployExecutionPayload
    {
        TargetId = "cell",
        DesiredRevision = "prior",
        CurrentRevision = "candidate",
        Rollback = true
    }.Serialize();

    private static (DeployOperationExecutor Executor, IDeployBackend Backend, Func<WorkflowOperationRecord?> Read) Create(
        OperationProposal? proposal = null, DeployTargetKind targetKind = DeployTargetKind.AwsEcs)
    {
        WorkflowOperationRecord? saved = null;
        var target = new DeployTargetDefinition
        {
            TargetId = "cell",
            Backend = "test",
            TargetKind = targetKind,
            TargetName = "service",
            Environment = "test",
            RequiresApproval = true
        };
        var targets = Substitute.For<IDeployTargetRegistry>();
        targets.GetAsync("cell", Arg.Any<CancellationToken>()).Returns(target);
        var backend = Substitute.For<IDeployBackend>();
        backend.BackendName.Returns("test");
        backend.TargetKind.Returns(targetKind);
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
        var proposals = Substitute.For<IOperationProposalStore>();
        proposals.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(proposal);
        var service = new DeployWorkflowService(targets, [store], [backend],
            Substitute.For<IOperatorApprovalEvaluator>(), NullLogger<DeployWorkflowService>.Instance,
            proposalStore: proposals);
        return (new DeployOperationExecutor(service), backend, () => saved);
    }
}
