// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.ControlPlane;
using Microsoft.AspNetCore.Http;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using NSubstitute;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed partial class OperationGatewayStateMachineTests
{
    [UnitTheory]
    [InlineData("metadata")]
    [InlineData("http")]
    [Operation(Operations.TestInfrastructure)]
    public async Task AgentProposal_SubmissionAudit_KeepsCanonicalAgentIdentity(string identitySource)
    {
        var ladder = Substitute.For<IGuardrailLadder>();
        ladder.Resolve(OperationClass.Deploy).Returns(
            new GuardrailDecision(GuardrailTier.RequiresApproval, OperationClass.Deploy, HonuaEdition.Pro, "test"));
        using var requestServices = new ServiceCollection().BuildServiceProvider();
        var context = identitySource == "http" ? new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, "canonical-agent"),
                new Claim(ClaimTypes.Role, "agent")], "test")),
            RequestServices = requestServices
        } : null;
        var audit = new RecordingAuditLog();
        var gateway = BuildGateway(new InMemoryProposalStore(), new DeployExecutor(), ladder,
            configure: services => services.AddSingleton<IAuditLog>(audit)
                .AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = context }));

        var result = await gateway.RouteAsync(new OperationGatewayRequest
        {
            Kind = OperationClass.Deploy,
            RequestedBy = "canonical-agent",
            RequestedByAgent = identitySource == "metadata" ? "canonical-agent" : null
        });

        Assert.Equal(OperationGatewayOutcome.ProposalCreated, result.Outcome);
        Assert.Contains(audit.Events, item => item.Action == "operation.proposed"
            && item.Actor == "canonical-agent" && item.ActorType == AuditActorType.UserId
            && item.ActorAgent == "canonical-agent");
    }

    [UnitTheory]
    [InlineData("agent", true, "canonical-agent")]
    [InlineData("admin", true, null)]
    [InlineData("agent", false, null)]
    [Operation(Operations.TestInfrastructure)]
    public async Task ApprovalDeniedAudit_BindsOnlyAuthenticatedAgentIdentity(
        string role, bool authenticated, string? expectedAgent)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, "canonical-agent"),
                new Claim(ClaimTypes.Role, role)], authenticated ? "test" : null))
        };
        var audit = new RecordingAuditLog();
        using var services = new ServiceCollection()
            .AddSingleton<IAuditLog>(audit)
            .AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = context })
            .BuildServiceProvider();
        var proposal = CreateProposal("agent-denied-approval", OperationClass.Deploy,
            OperationProposalStatus.AwaitingApproval);

        await OperationApprovalDenialRecorder.RecordAsync(services.GetRequiredService<IServiceScopeFactory>(),
            proposal, "canonical-agent", OperationApprovalDenialReason.HumanSessionRequired, CancellationToken.None);

        var record = Assert.Single(audit.Events);
        Assert.Equal("operation.approval_denied", record.Action);
        Assert.Equal(AuditOutcome.Denied, record.Outcome);
        Assert.Equal(expectedAgent, record.ActorAgent);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ApplyApprovedAgentProposal_ExecutionKeepsCanonicalAgentIdentity()
    {
        var proposal = CanonicalOperationGatewayTestComposition.WithVersionedInitiation(
            CreateProposal("agent-audit-replay", OperationClass.Deploy, OperationProposalStatus.AwaitingApproval) with
            {
                RequestedBy = "canonical-agent",
                RequestedByAgent = "canonical-agent"
            });
        var audit = new RecordingAuditLog();
        var executor = new PendingApprovalExecutor();
        executor.Completion.SetResult("execution-id");
        var gateway = BuildGateway(new InMemoryProposalStore(proposal), executor,
            configure: services => services.AddSingleton<IAuditLog>(audit));

        await gateway.ApplyApprovedProposalAsync(proposal.ProposalId, ApprovedBy("human-approver"));

        Assert.True(executor.Entered.Task.IsCompleted);
        Assert.Contains(audit.Events, item => item.Action == "operation.submitted"
            && item.Actor == "canonical-agent" && item.ActorType == AuditActorType.UserId
            && item.ActorAgent == "canonical-agent");
        Assert.Contains(audit.Events, item => item.Action == "operation.approved"
            && item.Actor == "human-approver" && item.ActorType == AuditActorType.UserId
            && item.ActorAgent == null);
    }
}
