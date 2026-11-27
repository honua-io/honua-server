// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
        AgentPrincipal.IsAgent(hop).Should().BeTrue();
    }

    [UnitTest]
    public async Task AgentGrantCaller_IsAnAgentOnTheLoopbackHop()
    {
        var store = new OperationLineageAttestationStore(TimeProvider.System);
        var origin = OriginOn("/api/v1/operations/submit", new Claim(AdminApiKeyPermission.PermissionClaimType, "agent"));

        var hop = await ForwardAsync(store, origin, new OperationPolicyContext());

        AgentPrincipal.IsAgent(hop.User).Should().BeTrue();
        AgentPrincipal.IsAgent(hop).Should().BeTrue();
    }

    [UnitTest]
    public async Task HumanCaller_IsNotMarkedOnTheLoopbackHop()
    {
        var store = new OperationLineageAttestationStore(TimeProvider.System);
        var hop = await ForwardAsync(store, OriginOn("/api/v1/operations/submit"), new OperationPolicyContext());

        AgentPrincipal.IsAgent(hop.User).Should().BeFalse();
        AgentPrincipal.IsAgent(hop).Should().BeFalse();
    }

    [UnitTest]
    public async Task ApprovedReplay_IsNotMarkedOnTheLoopbackHop()
    {
        var store = new OperationLineageAttestationStore(TimeProvider.System);
        var approved = new OperationPolicyContext { ApprovedProposalId = "proposal-approved" };

        var hop = await ForwardAsync(store, OriginOn("/mcp"), approved);

        AgentPrincipal.IsAgent(hop.User).Should().BeFalse();
        AgentPrincipal.IsAgent(hop).Should().BeFalse();
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
        AgentPrincipal.IsAgent(hop).Should().BeFalse();
    }

    [UnitTest]
    public async Task OidcClaimsTransformationReplacingPrincipalDuringUseAuthorization_PreservesAgentOriginForApprovalFilter()
    {
        var store = new OperationLineageAttestationStore(TimeProvider.System);
        await using var app = await BuildOidcLoopbackPipelineAppAsync(store);
        using var client = app.GetTestClient();

        using var agentHop = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/services/policy");
        OperationLineageHeaders.Apply(agentHop, new OperationLoopbackHop(new OperationPolicyContext(), OriginOn("/mcp")), store);
        using var agentResponse = await client.SendAsync(agentHop);
        var agentBody = await agentResponse.Content.ReadAsStringAsync();

        agentResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden, agentBody);
        agentBody.Should().Contain("urn:honua:approval-required");

        using var humanHop = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/services/policy");
        OperationLineageHeaders.Apply(humanHop, new OperationLoopbackHop(new OperationPolicyContext(), OriginOn("/api/v1/operations/submit")), store);
        using var humanResponse = await client.SendAsync(humanHop);

        humanResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [UnitTest]
    public async Task UseAgentPrincipalMarking_BlocksUnapprovedAgentFeatureWritesAndAllowsHumanWrites()
    {
        var store = new OperationLineageAttestationStore(TimeProvider.System);
        await using var app = await BuildOidcLoopbackPipelineAppAsync(store);
        using var client = app.GetTestClient();

        using var agentHop = new HttpRequestMessage(HttpMethod.Post, "/api/v1/features/write-check");
        OperationLineageHeaders.Apply(agentHop, new OperationLoopbackHop(new OperationPolicyContext(), OriginOn("/mcp")), store);
        using var agentResponse = await client.SendAsync(agentHop);
        agentResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var humanHop = new HttpRequestMessage(HttpMethod.Post, "/api/v1/features/write-check");
        OperationLineageHeaders.Apply(humanHop, new OperationLoopbackHop(new OperationPolicyContext(), OriginOn("/api/v1/operations/submit")), store);
        using var humanResponse = await client.SendAsync(humanHop);
        humanResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task<WebApplication> BuildOidcLoopbackPipelineAppAsync(OperationLineageAttestationStore store)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(store);
        builder.Services.Configure<OidcAuthenticationOptions>(_ => { });
        builder.Services.AddTransient<IClaimsTransformation, OidcClaimsTransformation>();
        builder.Services.AddAuthentication("ApiKey")
            .AddScheme<AuthenticationSchemeOptions, AnonymousSchemeHandler>("ApiKey", _ => { })
            .AddScheme<AuthenticationSchemeOptions, JwtRolesSchemeHandler>("OidcJwt", _ => { });
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy("Admin", policy =>
            {
                policy.AddAuthenticationSchemes("OidcJwt");
                policy.RequireAuthenticatedUser();
                policy.RequireRole("admin");
            });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseOperationLineageAttestation();
        AgentPrincipal.UseAgentPrincipalMarking(app);
        app.UseAuthorization();
        MapLoopbackEndpoints(app);

        await app.StartAsync();
        return app;
    }

    private static void MapLoopbackEndpoints(WebApplication app)
    {
        app.MapPost("/api/v1/admin/services/policy", () => Results.Ok("mutated"))
            .RequireAuthorization("Admin")
            .AddAgentApprovalFilter(AgentApprovalFilter.AccessPolicy);

        app.MapPost("/api/v1/features/write-check", () =>
        {
            try
            {
                AgentFeatureWriteGuard.EnsureAllowed();
                return Results.Ok("written");
            }
            catch (AgentFeatureWriteApprovalRequiredException)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
        }).RequireAuthorization("Admin");
    }

    private sealed class AnonymousSchemeHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(AuthenticateResult.NoResult());
    }

    // Emits a JWT-shaped ClaimsIdentity whose RoleClaimType is "roles", causing OidcClaimsTransformation
    // to build and return a new ClaimsPrincipal during UseAuthorization.
    private sealed class JwtRolesSchemeHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
                [new Claim("sub", "oidc-admin-1"), new Claim("roles", "admin")],
                authenticationType: "OidcJwt",
                nameType: "name",
                roleType: "roles");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
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
