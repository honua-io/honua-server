// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Features.OperationLineage;

// The loopback hop forwards the caller's own credential; an agent caller must stay an agent on it.
public sealed class OperationLineageAgentOriginTests
{
    [UnitTest]
    public async Task McpCallerWithoutAgentGrant_IsAnAgentOnTheLoopbackHop()
    {
        var store = new OperationLineageAttestationStore(TimeProvider.System);
        var hop = await ForwardAsync(store, OriginOn("/mcp"), new OperationPolicyContext { OperationInstanceId = "opinst-mcp" });

        AgentPrincipal.IsAgent(hop.User).Should().BeTrue();
    }

    [UnitTest]
    public async Task AgentGrantCaller_IsAnAgentOnTheLoopbackHop()
    {
        var store = new OperationLineageAttestationStore(TimeProvider.System);
        var origin = OriginOn("/api/v1/operations/submit", new Claim(AdminApiKeyPermission.PermissionClaimType, "agent"));

        var hop = await ForwardAsync(store, origin, new OperationPolicyContext());

        AgentPrincipal.IsAgent(hop.User).Should().BeTrue();
    }

    [UnitTest]
    public async Task HumanCaller_IsNotMarkedOnTheLoopbackHop()
    {
        var store = new OperationLineageAttestationStore(TimeProvider.System);
        var hop = await ForwardAsync(store, OriginOn("/api/v1/operations/submit"), new OperationPolicyContext());

        AgentPrincipal.IsAgent(hop.User).Should().BeFalse();
    }

    [UnitTest]
    public async Task ApprovedReplay_IsNotMarkedOnTheLoopbackHop()
    {
        var store = new OperationLineageAttestationStore(TimeProvider.System);
        var approved = new OperationPolicyContext { ApprovedProposalId = "proposal-approved" };

        var hop = await ForwardAsync(store, OriginOn("/mcp"), approved);

        AgentPrincipal.IsAgent(hop.User).Should().BeFalse();
    }

    [UnitTest]
    public async Task ApprovedReplayOfAnAgentProposal_IssuesAgentCredentials()
    {
        var store = new OperationLineageAttestationStore(TimeProvider.System);
        var approved = new OperationPolicyContext { ApprovedProposalId = "proposal-agent", Roles = ["agent"] };

        var hop = await ForwardAsync(store, OriginOn("/api/v1/admin/proposals/proposal-agent/approve"), approved);

        AgentPrincipal.IsAgent(hop.User).Should().BeFalse("the approval gate does not apply to the replay twice");
        AdminApiKeyKind.CallerOf(hop).Should().Be(AdminApiKeyCaller.Agent);
    }

    [UnitTest]
    public async Task ApprovedReplayOfAHumanProposal_IssuesStandardCredentials()
    {
        var store = new OperationLineageAttestationStore(TimeProvider.System);
        var approved = new OperationPolicyContext { ApprovedProposalId = "proposal-human", Roles = ["admin"] };

        var hop = await ForwardAsync(store, OriginOn("/api/v1/admin/proposals/proposal-human/approve"), approved);

        AdminApiKeyKind.CallerOf(hop).Should().Be(AdminApiKeyCaller.Person);
    }

    [UnitTest]
    public async Task UnattestedAgentRequest_IsNotMarked()
    {
        var hop = HopContext(attestation: null);
        var middleware = new OperationLineageAttestationMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(hop, new OperationLineageAttestationStore(TimeProvider.System));

        AgentPrincipal.IsAgent(hop.User).Should().BeFalse();
    }

    private static async Task<DefaultHttpContext> ForwardAsync(
        OperationLineageAttestationStore store, HttpContext origin, OperationPolicyContext policy)
    {
        using var message = new HttpRequestMessage();
        OperationLineageHeaders.Apply(message, new OperationLoopbackHop(policy, origin), store);
        var hop = HopContext(message.Headers.GetValues(OperationLineageHeaders.Attestation).Single());
        await new OperationLineageAttestationMiddleware(_ => Task.CompletedTask).InvokeAsync(hop, store);
        return hop;
    }

    private static DefaultHttpContext OriginOn(string path, params Claim[] claims)
    {
        var context = new DefaultHttpContext { User = Authenticated(claims) };
        context.Request.Path = path;
        return context;
    }

    private static DefaultHttpContext HopContext(string? attestation)
    {
        var context = new DefaultHttpContext { User = Authenticated() };
        context.Request.Path = "/api/v1/admin/services";
        if (attestation is not null)
        {
            context.Request.Headers[OperationLineageHeaders.Attestation] = attestation;
        }

        return context;
    }

    private static ClaimsPrincipal Authenticated(params Claim[] claims)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.Role, "admin"), .. claims], "ApiKey"));
}
