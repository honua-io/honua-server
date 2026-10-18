// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Ai.StudioAiProxy;
using Honua.Ai.StudioAiProxy.Domain;
using Honua.Core.Features.AgentWorkspace.Abstractions;
using Honua.Core.Features.AgentWorkspace.Domain;
using Honua.TestKit.Attributes;
using NSubstitute;
using Honua.Core.Features.Authorization;
using Honua.Infrastructure.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Honua.Core.Features.Studio;

namespace Honua.Server.Tests.Features.StudioAiProxy;

/// <summary>Scoped agent signing requires an actual authorized turn and its exact immutable binding.</summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class StudioAiWorkspaceCertificationTests
{
    private static readonly ClaimsPrincipal Agent = new(new ClaimsIdentity([new Claim("sub", "agent")], "ApiKey"));

    [UnitTest]
    public async Task AuthorizedTurn_AllowsExactBinding()
    {
        var authorizer = CreateAuthorizer();
        Assert.True(await AuthorizeAsync(authorizer, Certification()));
        await authorizer.Received(1).AuthorizeAsync(Agent, "workspace", "turn", Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task MissingMembershipOrAuthorizer_DeniesSigning()
    {
        Assert.False(await AuthorizeAsync(null, Certification()));
        var authorizer = Substitute.For<IAgentWorkspaceTurnAuthorizer>();
        Assert.False(await AuthorizeAsync(authorizer, Certification()));
    }

    [Theory]
    [InlineData("candidate")]
    [InlineData("endpoint")]
    [InlineData("release")]
    [InlineData("action")]
    [InlineData("nonce")]
    public async Task WireRetargetingOrReplayBinding_DeniesSigning(string field)
    {
        var certification = new StudioAiTranscriptCertification
        {
            CandidateId = field == "candidate" ? "another-target" : "target",
            EndpointIdentity = field == "endpoint" ? "another-endpoint" : "target",
            ReleaseId = field == "release" ? "unqualified-release" : "release",
            ActionId = field == "action" ? "another-turn" : "turn",
            RunNonce = field == "nonce" ? "replayed-turn" : "turn"
        };
        Assert.False(await AuthorizeAsync(CreateAuthorizer(), certification));
    }

    [UnitTest]
    public async Task CrossTenantOrHeaderBinding_DeniesSigning()
    {
        var authorizer = CreateAuthorizer();
        Assert.False(await StudioAiWorkspaceCertification.AuthorizeAsync(authorizer, Agent,
            "workspace", "turn", "different-tenant", Certification(), CancellationToken.None));
        authorizer.AuthorizeAsync(Agent, "workspace", "turn", Arg.Any<CancellationToken>())
            .Returns(new AgentWorkspaceTurnAuthorization("other-workspace", "turn", "target", "tenant", "release"));
        Assert.False(await AuthorizeAsync(authorizer, Certification()));
    }

    [UnitTest]
    public async Task ProxyRoute_AdmitsOnlyExplicitWorkspaceAgentCredentialFamily()
    {
        var options = Substitute.For<IOptionsMonitor<AdminRoleOptions>>();
        options.CurrentValue.Returns(new AdminRoleOptions { AdminRoles = ["admin"] });
        var handler = new StudioAiProxyAuthorizationHandler(options);
        var unrelated = new AuthorizationHandlerContext([new StudioAiProxyRequirement()], Agent, resource: null);
        await handler.HandleAsync(unrelated);
        Assert.True(unrelated.HasFailed);

        var eligibleAgent = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("sub", "agent"), new Claim("permission", "agent:workspace")], "ApiKey"));
        var context = new AuthorizationHandlerContext([new StudioAiProxyRequirement()], eligibleAgent, resource: null);
        await handler.HandleAsync(context);
        Assert.True(context.HasSucceeded);
        // Passing this pre-tenant eligibility check does not make a transcript signable.
        Assert.False(await StudioAiWorkspaceCertification.AuthorizeAsync(null, eligibleAgent,
            "workspace", "turn", "tenant", Certification(), CancellationToken.None));
    }

    [UnitTest]
    public async Task ReadScopedAgent_ProxyPolicyDoesNotWidenGeneralStudioWrites()
    {
        var options = Substitute.For<IOptionsMonitor<StudioEndUserAuthorizationOptions>>();
        options.CurrentValue.Returns(new StudioEndUserAuthorizationOptions { Enabled = true });
        var adminOptions = Substitute.For<IOptionsMonitor<AdminRoleOptions>>();
        adminOptions.CurrentValue.Returns(new AdminRoleOptions { AdminRoles = ["admin"] });
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        var handler = new StudioLifecycleAuthorizationHandler(options, adminOptions,
            new HttpContextAccessor { HttpContext = http }, NullLogger<StudioLifecycleAuthorizationHandler>.Instance);
        var agent = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("permission", "admin:read"), new Claim("permission", "agent:workspace"),
            new Claim(ClaimTypes.Role, AdminApiKeyPermission.ScopedAdminRole)], "ApiKey"));
        var general = new AuthorizationHandlerContext([new StudioLifecycleRequirement()], agent, http);
        await handler.HandleAsync(general);
        Assert.True(general.HasFailed);

        var proxy = new AuthorizationHandlerContext([new StudioLifecycleRequirement(), new StudioAiProxyRequirement()], agent, http);
        await handler.HandleAsync(proxy);
        await new StudioAiProxyAuthorizationHandler(adminOptions).HandleAsync(proxy);
        Assert.True(proxy.HasSucceeded);

        options.CurrentValue.Returns(new StudioEndUserAuthorizationOptions { Enabled = false });
        var disabled = new AuthorizationHandlerContext([new StudioLifecycleRequirement(), new StudioAiProxyRequirement()], agent, http);
        await handler.HandleAsync(disabled);
        Assert.True(disabled.HasFailed);
    }

    private static Task<bool> AuthorizeAsync(IAgentWorkspaceTurnAuthorizer? authorizer, StudioAiTranscriptCertification certification)
        => StudioAiWorkspaceCertification.AuthorizeAsync(authorizer, Agent, "workspace", "turn", "tenant", certification, CancellationToken.None);

    private static IAgentWorkspaceTurnAuthorizer CreateAuthorizer()
    {
        var authorizer = Substitute.For<IAgentWorkspaceTurnAuthorizer>();
        authorizer.AuthorizeAsync(Agent, "workspace", "turn", Arg.Any<CancellationToken>())
            .Returns(new AgentWorkspaceTurnAuthorization("workspace", "turn", "target", "tenant", "release"));
        return authorizer;
    }

    private static StudioAiTranscriptCertification Certification() => new()
    {
        CandidateId = "target", EndpointIdentity = "target", ReleaseId = "release", ActionId = "turn", RunNonce = "turn"
    };
}
