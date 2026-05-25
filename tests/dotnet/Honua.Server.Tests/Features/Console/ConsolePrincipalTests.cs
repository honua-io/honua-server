// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Server.Features.Console;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Console;

/// <summary>
/// Verifies the Console principal-id resolver picks the correct claim for the
/// authentication schemes that reach Console endpoints (OIDC/JWT with
/// NameIdentifier/sub, admin API-key with ClaimTypes.Name plus optional
/// api_key_id / api_key_name claims).
/// </summary>
public class ConsolePrincipalTests
{
    [UnitTest]
    public void ResolveActorId_UnauthenticatedPrincipal_ReturnsNull()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity());

        Assert.Null(ConsolePrincipal.ResolveActorId(principal));
    }

    [UnitTest]
    public void ResolveActorId_PrefersNameIdentifierWhenPresent()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "user-1"),
                new Claim("sub", "oidc-sub"),
                new Claim("api_key_id", "api-key-1"),
                new Claim(ClaimTypes.Name, "admin"),
            },
            authenticationType: "Test"));

        Assert.Equal("user-1", ConsolePrincipal.ResolveActorId(principal));
    }

    [UnitTest]
    public void ResolveActorId_FallsBackToSubClaim()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[]
            {
                new Claim("sub", "oidc-sub"),
                new Claim("api_key_id", "api-key-1"),
            },
            authenticationType: "Test"));

        Assert.Equal("oidc-sub", ConsolePrincipal.ResolveActorId(principal));
    }

    [UnitTest]
    public void ResolveActorId_AdminApiKeyPrincipal_UsesApiKeyId()
    {
        // Matches the claim set produced by ApiKeyAuthenticationHandler when an
        // admin API key from the IAdminApiKeyStore authenticates.
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.Name, "admin"),
                new Claim(ClaimTypes.Role, "admin"),
                new Claim("auth_type", "admin-api-key"),
                new Claim("api_key_id", "11111111-2222-3333-4444-555555555555"),
                new Claim("api_key_name", "Console CI key"),
            },
            authenticationType: "Test"));

        Assert.Equal("11111111-2222-3333-4444-555555555555", ConsolePrincipal.ResolveActorId(principal));
    }

    [UnitTest]
    public void ResolveActorId_AdminApiKeyPrincipalWithoutId_FallsBackToApiKeyName()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.Name, "admin"),
                new Claim("auth_type", "admin-api-key"),
                new Claim("api_key_name", "Console CI key"),
            },
            authenticationType: "Test"));

        Assert.Equal("Console CI key", ConsolePrincipal.ResolveActorId(principal));
    }

    [UnitTest]
    public void ResolveActorId_LegacyAdminPrincipal_FallsBackToIdentityName()
    {
        // Matches the env-var admin and dev-bypass paths in
        // ApiKeyAuthenticationHandler — only ClaimTypes.Name/Role + auth_type
        // are present, no api_key_id/api_key_name.
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.Name, "admin"),
                new Claim(ClaimTypes.Role, "admin"),
                new Claim("auth_type", "admin"),
            },
            authenticationType: "Test"));

        Assert.Equal("admin", ConsolePrincipal.ResolveActorId(principal));
    }
}
