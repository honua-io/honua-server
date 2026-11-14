// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Security;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Infrastructure.Middleware;

public sealed class AuditAgentIdentityTests
{
    [Theory]
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
        Assert.Equal(AuditActorType.Agent, actorType);
    }

    [Fact]
    public void ResolveActor_UnauthenticatedAgentRole_RemainsAnonymous()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "agent"), new Claim("sub", "spoofed")])),
        };

        var actor = AuditContextResolver.ResolveActor(context, out var actorType);

        Assert.Equal(AuditEvent.AnonymousActor, actor);
        Assert.Equal(AuditActorType.Anonymous, actorType);
    }
}
