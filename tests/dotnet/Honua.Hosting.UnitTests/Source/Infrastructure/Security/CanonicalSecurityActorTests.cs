// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Infrastructure.Security;

[Protocol(TestProtocols.TestQuality)]
public sealed class CanonicalSecurityActorTests
{
    [UnitTest]
    [Operation(Operations.Security)]
    public void Resolve_ApiKeyWithoutId_DoesNotFallBackToNameIdentifier()
    {
        var principal = ApiKeyPrincipal(new Claim(ClaimTypes.NameIdentifier, "shared-subject"));

        Assert.Null(CanonicalSecurityActor.Resolve(principal));
    }

    [UnitTest]
    [Operation(Operations.Security)]
    public void Resolve_ApiKeyWithInvalidId_DoesNotFallBackToSubject()
    {
        var principal = ApiKeyPrincipal(
            new Claim("api_key_id", "not-a-guid"),
            new Claim("sub", "shared-subject"));

        Assert.Null(CanonicalSecurityActor.Resolve(principal));
    }

    [UnitTest]
    [Operation(Operations.Security)]
    public void Resolve_ApiKeyWithValidId_PrefersImmutableIdOverSubject()
    {
        const string apiKeyId = "11111111-2222-3333-4444-555555555555";
        var principal = ApiKeyPrincipal(
            new Claim("api_key_id", apiKeyId),
            new Claim(ClaimTypes.NameIdentifier, "shared-subject"));

        var actor = Assert.IsType<CanonicalSecurityActorIdentity>(CanonicalSecurityActor.Resolve(principal));

        Assert.Equal($"apikey:api-key:{apiKeyId}", actor.ActorId);
        Assert.Equal(apiKeyId, actor.ApiKeyId);
    }

    [UnitTest]
    [Operation(Operations.Security)]
    public void ResolveAuthorityFingerprint_McpAgentProjection_PreservesCredentialAuthority()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "owner"), new Claim(ClaimTypes.Role, "reader")], "Bearer"));
        var fingerprint = CanonicalSecurityActor.ResolveAuthorityFingerprint(principal);
        var context = new DefaultHttpContext { User = principal };
        context.Request.Path = "/mcp";

        AgentPrincipal.MarkRequestPrincipal(context);

        Assert.True(AgentPrincipal.IsAgent(principal));
        Assert.Equal(fingerprint, CanonicalSecurityActor.ResolveAuthorityFingerprint(principal));
    }

    [UnitTheory]
    [InlineData("agent")]
    [InlineData("admin")]
    [Operation(Operations.Security)]
    public void ResolveAuthorityFingerprint_CredentialRoleChanges_ChangesFingerprint(string role)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "owner")], "Bearer"));
        var fingerprint = CanonicalSecurityActor.ResolveAuthorityFingerprint(principal);

        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(ClaimTypes.Role, role));

        Assert.NotEqual(fingerprint, CanonicalSecurityActor.ResolveAuthorityFingerprint(principal));
    }

    [UnitTest]
    [Operation(Operations.Security)]
    public void StampRequestBinding_RepeatedWithSameValues_PreservesStampedClaimsWithoutDuplicates()
    {
        var identity = new ClaimsIdentity([new Claim("sub", "user-1")], "Bearer");
        var principal = new ClaimsPrincipal(identity);

        CanonicalSecurityActor.StampRequestBinding(principal, "tenant-a");
        var firstActorClaim = Assert.Single(identity.FindAll(CanonicalSecurityActor.CanonicalActorClaim));

        CanonicalSecurityActor.StampRequestBinding(principal, "tenant-a");
        var secondActorClaim = Assert.Single(identity.FindAll(CanonicalSecurityActor.CanonicalActorClaim));

        Assert.Same(firstActorClaim, secondActorClaim);
        Assert.Equal("tenant-a", CanonicalSecurityActor.FindStampedValue(principal, CanonicalSecurityActor.EffectiveTenantClaim));

        CanonicalSecurityActor.StampRequestBinding(principal, "tenant-b");
        Assert.Single(identity.FindAll(CanonicalSecurityActor.EffectiveTenantClaim));
        Assert.Equal("tenant-b", CanonicalSecurityActor.FindStampedValue(principal, CanonicalSecurityActor.EffectiveTenantClaim));
    }

    [UnitTest]
    [Operation(Operations.Security)]
    public void RemoveUnstampedAuthorityClaims_DistinguishesStampedAndUnstamped()
    {
        var stamped = CanonicalSecurityActor.CreateStampedClaim("permission", "admin:*");
        var unstamped = new Claim("permission", "admin:*");
        var identity = new ClaimsIdentity([stamped, unstamped], "Bearer");
        var principal = new ClaimsPrincipal(identity);

        Assert.True(CanonicalSecurityActor.HasUnstampedAuthorityClaim(identity));
        Assert.True(CanonicalSecurityActor.RemoveUnstampedAuthorityClaims(principal));
        Assert.False(CanonicalSecurityActor.HasUnstampedAuthorityClaim(identity));
        var remaining = Assert.Single(identity.FindAll("permission"));
        Assert.True(CanonicalSecurityActor.IsFrameworkOwnedClaim(remaining));
        Assert.Equal("admin:*", remaining.Value);
        Assert.False(CanonicalSecurityActor.RemoveUnstampedAuthorityClaims(principal));
    }

    [UnitTheory]
    [InlineData("adminsession:subject:https%3A%2F%2Fidp.example.com:alice", "subject:https%3A%2F%2Fidp.example.com:alice")]
    [InlineData("jwtbearer:subject:https%3A%2F%2Fidp.example.com:alice", "subject:https%3A%2F%2Fidp.example.com:alice")]
    [InlineData("operatorbearer:subject:https%3A%2F%2Fidp.example.com:alice", "subject:https%3A%2F%2Fidp.example.com:alice")]
    [InlineData("subject:https%3A%2F%2Fidp.example.com:alice@tenant:tenant-a", "subject:https%3A%2F%2Fidp.example.com:alice")]
    [InlineData("apikey:api-key:11111111-2222-3333-4444-555555555555", null)]
    [InlineData("agent:requester", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [Operation(Operations.Security)]
    public void ResolveIdentityKey_NormalizesSubjectAcrossSchemesAndTenants(string? actorId, string? expected)
    {
        Assert.Equal(expected, CanonicalSecurityActor.ResolveIdentityKey(actorId));
    }

    private static ClaimsPrincipal ApiKeyPrincipal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, AuthenticationExtensions.ApiKeyScheme));
}
