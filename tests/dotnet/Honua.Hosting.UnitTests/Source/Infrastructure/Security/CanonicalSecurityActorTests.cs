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

    private static ClaimsPrincipal ApiKeyPrincipal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, AuthenticationExtensions.ApiKeyScheme));
}
