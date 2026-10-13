// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

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

    private static ClaimsPrincipal ApiKeyPrincipal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, AuthenticationExtensions.ApiKeyScheme));
}
