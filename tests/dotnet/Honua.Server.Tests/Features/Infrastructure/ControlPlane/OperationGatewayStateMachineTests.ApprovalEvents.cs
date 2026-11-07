// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed partial class OperationGatewayStateMachineTests
{
    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ApplyApprovedProposal_ExecutionPending_ProposerReadsOneApprovalNamingApprover()
    {
        var proposal = CreateProposal("p-approval-event", OperationClass.Deploy, OperationProposalStatus.AwaitingApproval) with
        {
            RequestedBy = "proposing-agent"
        };
        var audit = new RecordingAuditLog();
        var executor = new PendingApprovalExecutor();
        var gateway = BuildGateway(new InMemoryProposalStore(proposal), executor,
            configure: services => services.AddSingleton<IAuditLog>(audit));
        var applying = gateway.ApplyApprovedProposalAsync(proposal.ProposalId, "human-approver");
        try
        {
            await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                gateway.ApplyApprovedProposalAsync(proposal.ProposalId, "another-approver"));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, proposal.RequestedBy!)], "test"));
            var authorization = Substitute.For<IAuthorizationService>();
            authorization.AuthorizeAsync(principal, Arg.Any<object>(), AuthenticationExtensions.OpsReadPolicy)
                .Returns(AuthorizationResult.Success());
            using var services = new ServiceCollection().BuildServiceProvider();
            var reader = new McpOpsObservabilityReader(Substitute.For<IOpsHealthSnapshotService>(),
                Substitute.For<IOpsFindingsEvidenceSource>(), ApprovalEventFeed(audit), authorization, services);

            var page = await reader.ListOperateEventsAsync(principal,
                new McpOperateEventsArgument { Kind = ["audit"], CorrelationId = proposal.Audit.CorrelationId },
                CancellationToken.None);

            var approval = Assert.Single(page.GetProperty("items").EnumerateArray());
            Assert.Equal("operation.approved", approval.GetProperty("title").GetString());
            Assert.Equal("human-approver", approval.GetProperty("actor").GetString());
            Assert.Equal($"operation_proposal/{proposal.ProposalId}", approval.GetProperty("resourceRef").GetString());
            Assert.False(applying.IsCompleted);
        }
        finally
        {
            executor.Completion.TrySetResult("execution-id");
            await applying;
        }
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ApplyApprovedProposal_ApprovalAuditFails_FailsWithoutActuating()
    {
        var audit = Substitute.For<IAuditLog>();
        audit.RecordAsync(Arg.Is<AuditEvent>(item => item.Action == "operation.approved"), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string?>(new IOException("Audit unavailable")));

        await AssertApprovalAuditFailureAsync(audit);
    }

    [UnitTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [Operation(Operations.TestInfrastructure)]
    public async Task ApplyApprovedProposal_ApprovalAuditHasNoIdentity_FailsWithoutActuating(string? auditId)
    {
        var audit = Substitute.For<IAuditLog>();
        audit.RecordAsync(Arg.Is<AuditEvent>(item => item.Action == "operation.approved"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(auditId));

        await AssertApprovalAuditFailureAsync(audit);
    }

    private static async Task AssertApprovalAuditFailureAsync(IAuditLog audit)
    {
        var proposal = CreateProposal("p-approval-audit-failed", OperationClass.Deploy, OperationProposalStatus.AwaitingApproval);
        var store = new InMemoryProposalStore(proposal);
        var executor = new PendingApprovalExecutor();
        executor.Completion.SetResult("execution-id");
        var gateway = BuildGateway(store, executor,
            configure: services => services.AddSingleton(audit));

        var result = await gateway.ApplyApprovedProposalAsync(proposal.ProposalId, "human-approver");

        Assert.Equal(OperationProposalStatus.Failed, result!.Status);
        var persisted = await store.GetAsync(proposal.ProposalId);
        Assert.Equal(OperationProposalStatus.Failed, persisted!.Status);
        Assert.False(executor.Entered.Task.IsCompleted);
        Assert.Contains("actuation did not begin", result.ResolutionReason, StringComparison.Ordinal);
        Assert.Equal(result.ResolutionReason, persisted.ResolutionReason);
        Assert.Contains(result.ResolutionReason!, result.Plan.BlockingReasons);
    }

    private static LocalOperateEventFeed ApprovalEventFeed(RecordingAuditLog audit)
    {
        var reader = Substitute.For<IAuditLogReader>();
        reader.ListAsync(Arg.Any<AuditLogFilter>(), Arg.Any<CancellationToken>()).Returns(_ => new AuditEventPage
        {
            Items = audit.Events.Select((item, index) => new AuditEventRecord
            {
                AuditId = index + 1,
                Timestamp = item.Timestamp,
                EventType = item.EventType,
                Actor = item.Actor,
                ActorType = item.ActorType,
                ResourceType = item.ResourceType,
                ResourceId = item.ResourceId,
                Action = item.Action,
                Outcome = item.Outcome,
                CorrelationId = item.CorrelationId,
                Details = item.Details
            }).ToArray()
        });
        return new LocalOperateEventFeed(NullLogger<LocalOperateEventFeed>.Instance, auditReader: reader);
    }

    private sealed class PendingApprovalExecutor : IOperationExecutor
    {
        public OperationClass OperationClass => OperationClass.Deploy;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<OperationProposalPlan?> PlanAsync(OperationGatewayRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult<OperationProposalPlan?>(null);

        public Task<string?> ExecuteAsync(OperationGatewayRequest request, string? executionPayload,
            CancellationToken cancellationToken = default)
        {
            Entered.SetResult();
            return Completion.Task.WaitAsync(cancellationToken);
        }
    }
}
