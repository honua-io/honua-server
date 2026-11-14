// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Microsoft.AspNetCore.Http;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed partial class OperationGatewayStateMachineTests
{
    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task AgentAuthority_AuthenticatedEntryWithoutAgentMetadata_DeniesMissingCeiling()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, "canonical-agent"),
                new Claim(ClaimTypes.Role, "agent")], "test"))
        };
        using var requestServices = new ServiceCollection().BuildServiceProvider();
        context.RequestServices = requestServices;
        var ceiling = Substitute.For<IAgentAuthorityStore>();
        var executor = new PendingApprovalExecutor();
        executor.Completion.SetResult("execution-id");
        var gateway = BuildGateway(new InMemoryProposalStore(), executor,
            configure: services => services.AddSingleton(ceiling)
                .AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = context }));

        var result = await gateway.RouteAsync(new OperationGatewayRequest
        { Kind = OperationClass.Deploy, RequestedBy = "caller-supplied" });

        Assert.Equal(OperationGatewayOutcome.Blocked, result.Outcome);
        Assert.False(executor.Entered.Task.IsCompleted);
        await ceiling.Received().GetAsync(null, "canonical-agent", Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ApplyApprovedAgentProposal_RemovedCeiling_DeniesWithoutActuating()
    {
        var proposal = CanonicalOperationGatewayTestComposition.WithVersionedInitiation(
            CreateProposal("agent-authority-replay", OperationClass.Deploy, OperationProposalStatus.AwaitingApproval) with
            {
                RequestedBy = "canonical-agent",
                RequestedByAgent = "canonical-agent",
                TenantId = "tenant-a"
            });
        var ceiling = Substitute.For<IAgentAuthorityStore>();
        ceiling.GetAsync(null, "canonical-agent", Arg.Any<CancellationToken>()).Returns(new AgentAuthorityCeiling
        {
            PrincipalId = "canonical-agent", AllowedOperationIds = ["control-plane.deploy"],
            MaximumBlastRadius = OperationBlastRadiusClass.DeploymentScope,
            MaximumSideEffect = OperationSideEffectClass.DestroysState
        });
        var executor = new PendingApprovalExecutor();
        executor.Completion.SetResult("execution-id");
        var gateway = BuildGateway(new InMemoryProposalStore(proposal), executor,
            configure: services => services.AddSingleton(ceiling));

        var result = await gateway.ApplyApprovedProposalAsync(proposal.ProposalId, ApprovedBy("human-approver"));

        Assert.Equal(OperationProposalStatus.Failed, result!.Status);
        Assert.False(executor.Entered.Task.IsCompleted);
        await ceiling.Received().GetAsync("tenant-a", "canonical-agent", Arg.Any<CancellationToken>());
    }
}
