// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Operations.Policy;
using Honua.Core.Features.Security.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Authorization;

public sealed class AgentApprovalExemptionTests
{
    private static readonly OperatorAuthorizationRequest PublishRequest = new()
    {
        ResourceType = OperatorResourceType.Catalog,
        Operation = OperatorOperation.Publish,
    };

    [UnitTest]
    public void Evaluate_HumanAdministrator_Publish_IsExempt()
    {
        var result = CreateEvaluator().Evaluate(CreateAdmin(), PublishRequest);

        result.IsRequired.Should().BeFalse();
    }

    [UnitTest]
    public void Evaluate_AdministratorMarkedAsAgent_Publish_RequiresApproval()
    {
        var principal = CreateAdmin();
        principal.AddIdentity(new ClaimsIdentity([new Claim(ClaimTypes.Role, AgentApprovalRule.AgentRole)]));

        var result = CreateEvaluator().Evaluate(principal, PublishRequest);

        result.IsRequired.Should().BeTrue();
        result.PolicyRef.Should().Be("operator.publish");
    }

    [UnitTest]
    public void Evaluate_AdministratorKeyWithAgentGrant_ExplicitApproval_RequiresApproval()
    {
        var principal = CreateAdmin(new Claim(AdminApiKeyPermission.PermissionClaimType, AgentPrincipal.AgentGrant));
        var request = PublishRequest with
        {
            Operation = OperatorOperation.Update,
            RequiresExplicitApproval = true,
            ApprovalPolicyRef = "operator.agent.access-policy",
        };

        var result = CreateEvaluator().Evaluate(principal, request);

        result.IsRequired.Should().BeTrue();
        result.PolicyRef.Should().Be("operator.agent.access-policy");
    }

    [UnitTest]
    public void Evaluate_ServerMintedApprovedOperation_Publish_IsNotRequiredAgain()
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, AdminApiKeyPermission.ApprovedOperationRole)], "ApiKey");
        CanonicalSecurityActor.StampFrameworkClaim(identity, AdminApiKeyPermission.PermissionClaimType,
            AdminApiKeyPermission.CreateApprovedOperationGrant("POST", "/api/v1/admin/connections/c1/layers"));

        var result = CreateEvaluator().Evaluate(new ClaimsPrincipal(identity), PublishRequest);

        result.IsRequired.Should().BeFalse();
    }

    [UnitTest]
    public void Evaluate_ApprovedOperationRoleFromIdentityProvider_Publish_StillRequiresApproval()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Role, AdminApiKeyPermission.ApprovedOperationRole),
                new Claim(AdminApiKeyPermission.PermissionClaimType,
                    AdminApiKeyPermission.CreateApprovedOperationGrant("POST", "/api/v1/admin/connections/c1/layers")),
            ],
            "oidc"));

        var result = CreateEvaluator().Evaluate(principal, PublishRequest);

        result.IsRequired.Should().BeTrue();
    }

    [UnitTest]
    public void MarkRequestPrincipal_UppercaseMcpRoute_AddsAgentRole()
    {
        var context = CreateContext("/MCP", CreateAdmin());

        AgentPrincipal.MarkRequestPrincipal(context);

        AgentPrincipal.IsAgent(context.User).Should().BeTrue();
    }

    [UnitTest]
    public void EffectiveRoles_AgentGrantWithoutRole_IncludesAgentRoleOnce()
    {
        var principal = CreateAdmin(
            new Claim(AdminApiKeyPermission.PermissionClaimType, AgentPrincipal.AgentGrant),
            new Claim(ClaimTypes.Role, "admin"));

        AgentPrincipal.EffectiveRoles(principal).Should().BeEquivalentTo(["admin", AgentApprovalRule.AgentRole]);
        AgentPrincipal.EffectiveRoles(CreateAdmin(new Claim(ClaimTypes.Role, "admin"))).Should().Equal("admin");
    }

    [UnitTest]
    public void MarkRequestPrincipal_McpRoute_AddsAgentRoleOnce()
    {
        var context = CreateContext("/mcp", CreateAdmin());

        AgentPrincipal.MarkRequestPrincipal(context);
        AgentPrincipal.MarkRequestPrincipal(context);

        context.User.IsInRole(AgentApprovalRule.AgentRole).Should().BeTrue();
        context.User.FindAll(ClaimTypes.Role).Count(claim => claim.Value == AgentApprovalRule.AgentRole).Should().Be(1);
    }

    [UnitTest]
    public void MarkRequestPrincipal_AgentGrantOnAdminRoute_AddsAgentRole()
    {
        var principal = CreateAdmin(new Claim(AdminApiKeyPermission.PermissionClaimType, " Agent "));
        var context = CreateContext("/api/v1/admin/services/roads/access-policy", principal);

        AgentPrincipal.MarkRequestPrincipal(context);

        context.User.IsInRole(AgentApprovalRule.AgentRole).Should().BeTrue();
    }

    [UnitTest]
    public void MarkRequestPrincipal_HumanOnAdminRoute_LeavesPrincipalUnmarked()
    {
        var context = CreateContext("/api/v1/admin/services/roads/access-policy", CreateAdmin());

        AgentPrincipal.MarkRequestPrincipal(context);

        AgentPrincipal.IsAgent(context.User).Should().BeFalse();
    }

    [UnitTest]
    public void MarkRequestPrincipal_AnonymousMcpCaller_IsNotMarked()
    {
        var context = CreateContext("/mcp", new ClaimsPrincipal(new ClaimsIdentity()));

        AgentPrincipal.MarkRequestPrincipal(context);

        context.User.IsInRole(AgentApprovalRule.AgentRole).Should().BeFalse();
    }

    [UnitTest]
    public void HasCredentialManagementAuthority_FullAdminKeyWithAgentGrant_IsRefused()
    {
        var agentKey = CreateAdmin(
            new Claim(AdminApiKeyPermission.PermissionClaimType, "admin"),
            new Claim(AdminApiKeyPermission.PermissionClaimType, AgentPrincipal.AgentGrant));
        var humanKey = CreateAdmin(new Claim(AdminApiKeyPermission.PermissionClaimType, "admin"));

        AdminApiKeyPermission.HasCredentialManagementAuthority(agentKey, "POST", "/api/v1/admin/api-keys").Should().BeFalse();
        AdminApiKeyPermission.HasCredentialManagementAuthority(humanKey, "POST", "/api/v1/admin/api-keys").Should().BeTrue();
    }

    [UnitTest]
    public async Task EvaluateApproval_AgentRefusal_TellsAgentHowToRequestApproval()
    {
        var principal = CreateAdmin(new Claim(AdminApiKeyPermission.PermissionClaimType, AgentPrincipal.AgentGrant));
        var gate = new OperatorApprovalGate(
            new AllowingAuthorizationEvaluator(),
            CreateEvaluator(),
            NullLogger<OperatorApprovalGate>.Instance);
        var context = CreateContext("/api/v1/admin/connections/c1/layers", principal);
        context.Response.Body = new MemoryStream();

        var refusal = gate.EvaluateApproval(context, OperatorResourceType.Catalog, OperatorOperation.Publish);
        await refusal!.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(context.Response.Body);
        body.RootElement.GetProperty("detail").GetString().Should().Contain("Console Approvals")
            .And.Contain("/api/v1/operations/{operationId}/submit");
    }

    private static DefaultOperatorApprovalEvaluator CreateEvaluator() => new(
        Options.Create(new OperatorApprovalOptions()),
        Options.Create(new RbacOptions()),
        NullLogger<DefaultOperatorApprovalEvaluator>.Instance);

    private static ClaimsPrincipal CreateAdmin(params Claim[] extraClaims)
        => new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "admin-1"), new Claim("roles", "admin"), .. extraClaims],
            "TestScheme"));

    private static DefaultHttpContext CreateContext(string path, ClaimsPrincipal principal)
        => new()
        {
            User = principal,
            Request = { Path = path },
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };

    private sealed class AllowingAuthorizationEvaluator : IOperatorAuthorizationEvaluator
    {
        public Task<AccessDecision> EvaluateAsync(
            ClaimsPrincipal principal,
            OperatorAuthorizationRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(AccessDecision.Allowed());
    }
}
