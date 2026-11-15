// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Security;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Infrastructure.Middleware;

public sealed class AuditAgentIdentityTests
{
    [UnitTheory]
    [InlineData("ApiKey", "role", "scheme:issuer:stable-subject")]
    [InlineData("Bearer", "role", "scheme:issuer:stable-subject")]
    [InlineData("ApiKey", "grant", "scheme:issuer:stable-subject")]
    [InlineData("Bearer", "mcp", "scheme:issuer:stable-subject")]
    [InlineData("ApiKey", "role", "stable-subject")]
    [InlineData("Bearer", "role", "stable-subject")]
    public void ResolveActor_Agent_PreservesCanonicalOrStableIdentity(string scheme, string classification, string expectedActor)
    {
        const string stableId = "stable-subject";
        const string canonicalId = "scheme:issuer:stable-subject";
        var claims = new List<Claim> { new(scheme == "ApiKey" ? "api_key_id" : "sub", stableId) };
        if (classification == "role")
        {
            claims.Add(new Claim(ClaimTypes.Role, "AGENT"));
        }
        if (classification == "grant")
        {
            claims.Add(new Claim(AdminApiKeyPermission.PermissionClaimType, "agent"));
        }
        if (expectedActor == canonicalId)
        {
            claims.Add(CanonicalSecurityActor.CreateStampedClaim(CanonicalSecurityActor.CanonicalActorClaim, canonicalId));
        }
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, scheme)) };
        context.Request.Path = classification == "mcp" ? "/mcp" : "/api/v1/admin/services";
        AgentPrincipal.MarkRequestPrincipal(context);

        var actor = AuditContextResolver.ResolveActor(context, out var actorType);

        Assert.Equal(expectedActor, actor);
        Assert.Equal(scheme == "ApiKey" ? AuditActorType.ApiKey : AuditActorType.UserId, actorType);
        Assert.Equal(AuditContextResolver.ResolveCanonicalActor(context).Actor, AuditContextResolver.ResolveAgent(context));
    }

    [UnitTheory]
    [InlineData("ApiKey", "api_key_id", "6f1c2d3e-4b5a-4c6d-8e9f-0a1b2c3d4e5f")]
    [InlineData("Bearer", "sub", "stable-subject")]
    public void ResolveAgent_BeforeTenantResolution_UsesCanonicalIdentity(string scheme, string claimType, string subject)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(claimType, subject), new Claim(ClaimTypes.Role, "agent")], scheme)),
        };

        Assert.Equal(CanonicalSecurityActor.Resolve(context.User)!.ActorId, AuditContextResolver.ResolveAgent(context));
    }

    [UnitTest]
    public void ResolveActor_UnauthenticatedAgentRole_RemainsAnonymous()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "agent"), new Claim("sub", "spoofed")])),
        };

        var actor = AuditContextResolver.ResolveActor(context, out var actorType);

        Assert.Equal(AuditEvent.AnonymousActor, actor);
        Assert.Equal(AuditActorType.Anonymous, actorType);
        Assert.Null(AuditContextResolver.ResolveAgent(context));
    }

    [UnitTheory]
    [InlineData("generic")]
    [InlineData("studio-policy")]
    [InlineData("studio-domain")]
    public async Task Denial_UnstampedAgentApiKey_RecordsCanonicalAgent(string path)
    {
        const string keyId = "6f1c2d3e-4b5a-4c6d-8e9f-0a1b2c3d4e5f";
        var events = new List<AuditEvent>();
        var audit = Substitute.For<IAuditLog>();
        audit.RecordAsync(Arg.Do<AuditEvent>(events.Add), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("audit-test"));
        using var services = new ServiceCollection().AddSingleton(audit)
            .AddSingleton(Substitute.For<IAuthenticationService>()).BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("api_key_id", keyId), new Claim(ClaimTypes.Role, "agent")], "ApiKey")),
        };

        if (path == "studio-domain")
        {
            await StudioAuthorizationAudit.RecordDecisionAsync(context, audit, TimeProvider.System,
                StudioAuthorizationOperation.CreateDraft, "studio-content-item", null,
                StudioAuthorizationDecision.Deny("studio_authorization/test", "denied"));
        }
        else
        {
            var policy = path == "studio-policy"
                ? new AuthorizationPolicyBuilder().AddRequirements(new StudioLifecycleRequirement()).Build()
                : new AuthorizationPolicyBuilder().RequireAssertion(static _ => true).Build();
            await new HonuaAuthorizationMiddlewareResultHandler().HandleAsync(
                _ => Task.CompletedTask, context, policy, PolicyAuthorizationResult.Forbid());
        }

        var auditEvent = Assert.Single(events);
        Assert.Equal($"apikey:api-key:{keyId}", auditEvent.ActorAgent);
        Assert.Equal(AuditActorType.ApiKey, auditEvent.ActorType);
    }
}
