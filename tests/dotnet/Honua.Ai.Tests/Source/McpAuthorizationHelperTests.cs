// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Security;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Tests for MCP principal key derivation and session-binding identity selection.
/// </summary>
public sealed class McpAuthorizationHelperTests
{
    [UnitTest]
    public void ResolvePrincipalKey_IncludesAuthenticationScheme_WithNameIdentifier()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "operator-123"),
            new Claim(ClaimTypes.Name, "Operator One")
        ], "JwtBearer"));

        McpAuthorizationHelper.ResolvePrincipalKey(principal).Should().Be("jwtbearer:subject:-:operator-123");
    }

    [UnitTest]
    public void ResolvePrincipalKey_IncludesAuthenticationScheme_WithNameWhenNoSubject()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "admin")
        ], "ApiKey"));

        McpAuthorizationHelper.ResolvePrincipalKey(principal).Should().Be("apikey:name:admin");
    }

    [UnitTest]
    public void ResolvePrincipalKey_UsesAnonymousForUnauthenticated()
    {
        var principal = new ClaimsPrincipal();

        McpAuthorizationHelper.ResolvePrincipalKey(principal).Should().Be(McpSessionManager.AnonymousPrincipalKey);
    }

    [UnitTest]
    public void ResolvePrincipalKey_DifferentSchemesYieldDifferentKeys()
    {
        var bearer = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "identity-1")
        ], "JwtBearer"));
        var apiKey = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "identity-1"),
            new Claim(ClaimTypes.Name, "identity-1")
        ], "ApiKey"));

        McpAuthorizationHelper.ResolvePrincipalKey(bearer).Should().Be("jwtbearer:subject:-:identity-1");
        McpAuthorizationHelper.ResolvePrincipalKey(apiKey).Should().Be("apikey:subject:-:identity-1");
    }

    [UnitTest]
    public void ResolveSessionBindingKey_SameSubjectAcrossIssuersAndTenants_DoesNotCollide()
    {
        var issuerA = CreateBearerContext("same-subject", "https://issuer-a.example", "tenant-a");
        var issuerB = CreateBearerContext("same-subject", "https://issuer-b.example", "tenant-a");
        var tenantB = CreateBearerContext("same-subject", "https://issuer-a.example", "tenant-b");

        var first = McpAuthorizationHelper.ResolveSessionBindingKey(issuerA);

        first.Should().NotBe(McpAuthorizationHelper.ResolveSessionBindingKey(issuerB));
        first.Should().NotBe(McpAuthorizationHelper.ResolveSessionBindingKey(tenantB));
    }

    [UnitTest]
    public void ResolveSessionBindingKey_DelimiterBearingComponents_AreCollisionFree()
    {
        var first = CreateBearerContext("alice:tenant:a", "https://issuer.example", "b");
        var second = CreateBearerContext("alice", "https://issuer.example", "a:tenant:b");

        McpAuthorizationHelper.ResolveSessionBindingKey(first)
            .Should().NotBe(McpAuthorizationHelper.ResolveSessionBindingKey(second));
    }

    [UnitTest]
    public void ResolveSessionBindingKey_ApiKeysUseImmutableKeyId()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var first = CreateApiKeyContext(firstId, "tenant-a");
        var second = CreateApiKeyContext(secondId, "tenant-a");

        var firstBinding = McpAuthorizationHelper.ResolveSessionBindingKey(first);
        var secondBinding = McpAuthorizationHelper.ResolveSessionBindingKey(second);

        firstBinding.Should().NotBe(secondBinding);
        firstBinding.Should().Contain(firstId.ToString("D"));
        secondBinding.Should().Contain(secondId.ToString("D"));
    }

    [UnitTest]
    public void ResolveSessionBindingKey_BearerCannotForgeApiKeyIdentity()
    {
        var apiKeyId = Guid.NewGuid();
        var apiKey = CreateApiKeyContext(apiKeyId, "tenant-a");
        var bearer = CreateBearerContext(
            "bearer-subject",
            "https://issuer.example",
            "tenant-a",
            new Claim("api_key_id", apiKeyId.ToString("D")));

        McpAuthorizationHelper.ResolveSessionBindingKey(bearer)
            .Should().NotBe(McpAuthorizationHelper.ResolveSessionBindingKey(apiKey));
    }

    [UnitTest]
    public void ResolveSessionBindingKey_AuthenticatedWithoutDurableActor_DoesNotBecomeAnonymous()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("iss", "https://issuer.example"),
            new Claim("client_id", "machine-client"),
            new Claim(ClaimTypes.Name, "Mutable Machine Display Name"),
        ], OidcAuthenticationExtensions.JwtBearerScheme));
        var context = CreateContext(principal, "tenant-a");

        McpAuthorizationHelper.ResolveSessionBindingKey(context).Should().BeNull();
    }

    [UnitTest]
    public void CreateTrustedBearerPrincipal_StripsFrameworkOwnedClaims()
    {
        var forged = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "subject-1"),
            new Claim("iss", "https://issuer.example"),
            new Claim(CanonicalSecurityActor.CanonicalActorClaim, "forged-actor"),
            new Claim(CanonicalSecurityActor.EffectiveTenantClaim, "forged-tenant"),
            new Claim(CanonicalSecurityActor.ScopeCeilingClaim, "forged-scope"),
            new Claim("honua:auth_scheme", "ApiKey"),
            new Claim("honua:issuer", "forged-issuer"),
        ], "issuer-controlled"));
        var result = AuthenticateResult.Success(new AuthenticationTicket(
            forged,
            OidcAuthenticationExtensions.JwtBearerScheme));

        var promoted = McpBearerAuthenticationEndpointExtensions.CreateTrustedBearerPrincipal(result);

        promoted.Should().NotBeNull();
        promoted!.Claims.Should().NotContain(claim =>
            claim.Type.StartsWith("honua:", StringComparison.OrdinalIgnoreCase));
        promoted.FindFirst("sub")?.Value.Should().Be("subject-1");
        promoted.FindFirst("iss")?.Value.Should().Be("https://issuer.example");
    }

    [UnitTest]
    public void EnsureBearerToolTenant_TenantlessBearer_IsRejected()
    {
        var context = CreateBearerContext("subject", "https://issuer.example", tenant: null);

        var act = () => McpAuthorizationHelper.EnsureBearerToolTenant(context);

        act.Should().Throw<Exception>()
            .WithMessage("A validated tenant is required to invoke MCP tools.");
    }

    [UnitTest]
    public void EnsureBearerToolTenant_ApiKeyPath_IsUnchanged()
    {
        var context = CreateApiKeyContext(Guid.NewGuid(), tenant: null);

        var act = () => McpAuthorizationHelper.EnsureBearerToolTenant(context);

        act.Should().NotThrow();
    }

    [UnitTest]
    public async Task BearerWithoutConfiguredAuthority_RemainsAnonymousAndCannotAuthorizeTool()
    {
        var services = new ServiceCollection();
        services.AddOptions<OidcAuthenticationOptions>();
        await using var provider = services.BuildServiceProvider();
        var app = new ApplicationBuilder(provider);
        Exception? authorizationFailure = null;
        var discoveryReached = false;

        app.UseMcpBearerAuthentication();
        app.Run(context =>
        {
            discoveryReached = true;
            authorizationFailure = Record.Exception(() => McpAuthorizationHelper.EnsurePrincipal(context));

            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Path = "/mcp";
        context.Request.Headers.Authorization = "Bearer cannot-be-validated";

        await app.Build()(context);

        discoveryReached.Should().BeTrue("anonymous discovery must remain reachable");
        (context.User.Identity?.IsAuthenticated ?? false).Should().BeFalse();
        McpBearerAuthenticationEndpointExtensions.HasAuthenticationFailure(context).Should().BeFalse();
        authorizationFailure.Should().NotBeNull("tool authorization must still reject the anonymous caller");
    }

    private static DefaultHttpContext CreateBearerContext(
        string subject,
        string issuer,
        string? tenant,
        params Claim[] additionalClaims)
    {
        var claims = new List<Claim>
        {
            new("sub", subject),
            new("iss", issuer),
        };
        claims.AddRange(additionalClaims);
        return CreateContext(new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")), tenant);
    }

    private static DefaultHttpContext CreateApiKeyContext(Guid apiKeyId, string? tenant)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("api_key_id", apiKeyId.ToString("D")),
            new Claim(ClaimTypes.Name, "admin"),
        ], "ApiKey"));
        return CreateContext(principal, tenant);
    }

    private static DefaultHttpContext CreateContext(ClaimsPrincipal principal, string? tenant)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new StubTenantContext(tenant));

        return new DefaultHttpContext
        {
            User = principal,
            RequestServices = services.BuildServiceProvider(),
        };
    }

    private sealed class StubTenantContext(string? tenantId) : ITenantContext
    {
        public string? TenantId => tenantId;

        public TenantContextSource Source => tenantId is null
            ? TenantContextSource.Anonymous
            : TenantContextSource.Claim;

        public bool RequireTenantId(out string tenantIdValue, out string? reason)
        {
            tenantIdValue = tenantId ?? string.Empty;
            reason = tenantId is null ? "Tenant required." : null;
            return tenantId is not null;
        }
    }

    [Fact]
    public void ResolveDistinctPrincipalKey_WithOnlyRawSubject_FailsClosed()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "identity-1"),
        ], "JwtBearer"));

        McpAuthorizationHelper.ResolvePrincipalKey(principal).Should().Be("JwtBearer:authenticated");
    }
}
