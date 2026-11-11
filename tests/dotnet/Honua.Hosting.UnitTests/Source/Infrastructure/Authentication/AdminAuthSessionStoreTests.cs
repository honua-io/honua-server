// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Exceptions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// Unit tests for <see cref="AdminAuthSessionStore"/> covering session lifecycle
/// and the BH-028 distributed-cache exception fallback regression, plus the
/// <see cref="AdminAuthClaimsProjector"/> that decides which validated claims reach
/// an admin session and the principal projected back out of one.
/// </summary>
[SecurityTest]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class AdminAuthSessionStoreTests
{
    [UnitTest]
    public async Task GetAuthenticatedSessionAsync_OnASecondStoreOverTheSameTokens_HonoursTheSignIn()
    {
        var sharedTokens = new InMemorySecurityTokenStore();
        var signInNode = new AdminAuthSessionStore(sharedTokens, NullLogger<AdminAuthSessionStore>.Instance);
        var otherNode = new AdminAuthSessionStore(sharedTokens, NullLogger<AdminAuthSessionStore>.Instance);
        var sessionId = await signInNode.CreateAuthenticatedSessionAsync(
            "oidc", "access-token", null, [], DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None);

        (await otherNode.GetAuthenticatedSessionAsync(sessionId, CancellationToken.None)).Should().NotBeNull();

        await otherNode.RemoveAuthenticatedSessionAsync(sessionId, CancellationToken.None);
        (await signInNode.GetAuthenticatedSessionAsync(sessionId, CancellationToken.None))
            .Should().BeNull("a sign-out on one node is effective on the next read anywhere");
    }

    [UnitTest]
    public async Task GetPendingSessionAsync_IsNotAnswerableWithAnAuthenticatedSessionId()
    {
        var store = CreateStore();
        var sessionId = await store.CreateAuthenticatedSessionAsync(
            "oidc", "access-token", null, [], DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None);

        (await store.GetPendingSessionAsync(sessionId, CancellationToken.None)).Should().BeNull();
    }

    [UnitTest]
    public async Task CreateAuthenticatedSessionAsync_StoreUnavailable_Throws()
    {
        var tokens = Substitute.For<ISecurityTokenStore>();
        tokens.StoreAsync(default, default, default, default)
            .ReturnsForAnyArgs(Task.FromException(new InvalidOperationException("database unavailable")));
        var store = new AdminAuthSessionStore(tokens, NullLogger<AdminAuthSessionStore>.Instance);

        var create = () => store.CreateAuthenticatedSessionAsync(
            "oidc", "access-token", null, [], DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None);

        await create.Should().ThrowAsync<InvalidOperationException>();
    }

    [UnitTest]
    public async Task RemoveAuthenticatedSessionAsync_StoreUnavailable_RejectsTheSignOut()
    {
        var store = new AdminAuthSessionStore(RevocationUnavailable(), NullLogger<AdminAuthSessionStore>.Instance);

        var remove = () => store.RemoveAuthenticatedSessionAsync("a-session", CancellationToken.None);

        await remove.Should().ThrowAsync<ServiceUnavailableException>();
    }

    [UnitTest]
    public async Task RemovePendingSessionAsync_StoreUnavailable_IsBestEffort()
    {
        var store = new AdminAuthSessionStore(RevocationUnavailable(), NullLogger<AdminAuthSessionStore>.Instance);

        var remove = () => store.RemovePendingSessionAsync("a-pending-sign-in", CancellationToken.None);

        await remove.Should().NotThrowAsync();
    }

    private static ISecurityTokenStore RevocationUnavailable()
    {
        var tokens = Substitute.For<ISecurityTokenStore>();
        tokens.RevokeAsync(default, default)
            .ReturnsForAnyArgs(Task.FromException(new InvalidOperationException("database unavailable")));
        return tokens;
    }

    [UnitTest]
    public async Task CreateAndGetPendingSession_RoundTrips()
    {
        var store = CreateStore();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);

        var sessionId = await store.CreatePendingSessionAsync(
            "provider-1", "state-abc", "verifier-xyz", expiresAt, CancellationToken.None);

        sessionId.Should().NotBeNullOrWhiteSpace();

        var session = await store.GetPendingSessionAsync(sessionId, CancellationToken.None);

        session.Should().NotBeNull();
        session!.ProviderKey.Should().Be("provider-1");
        session.State.Should().Be("state-abc");
        session.CodeVerifier.Should().Be("verifier-xyz");
    }

    [UnitTest]
    public async Task RemovePendingSession_SubsequentGetReturnsNull()
    {
        var store = CreateStore();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);

        var sessionId = await store.CreatePendingSessionAsync(
            "provider-2", "state-def", "verifier-abc", expiresAt, CancellationToken.None);

        (await store.GetPendingSessionAsync(sessionId, CancellationToken.None)).Should().NotBeNull();

        await store.RemovePendingSessionAsync(sessionId, CancellationToken.None);

        (await store.GetPendingSessionAsync(sessionId, CancellationToken.None)).Should().BeNull();
    }

    [UnitTest]
    public async Task CreateAndGetAuthenticatedSession_RoundTrips()
    {
        var store = CreateStore();
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);

        var sessionId = await store.CreateAuthenticatedSessionAsync(
            "provider-1",
            accessToken: "at-value",
            idToken: "id-token-value",
            claims: [new AdminAuthSessionClaim { Type = "email", Value = "admin@honua.io" }],
            expiresAt: expiresAt,
            cancellationToken: CancellationToken.None);

        sessionId.Should().NotBeNullOrWhiteSpace();

        var session = await store.GetAuthenticatedSessionAsync(sessionId, CancellationToken.None);

        session.Should().NotBeNull();
        session!.AccessToken.Should().Be("at-value");
        session.IdToken.Should().Be("id-token-value");
        session.Claims.Should().ContainSingle(c => c.Type == "email" && c.Value == "admin@honua.io");
    }

    [UnitTest]
    public async Task GetPendingSession_UnknownId_ReturnsNull()
    {
        var store = CreateStore();
        var session = await store.GetPendingSessionAsync("unknown-session-id", CancellationToken.None);
        session.Should().BeNull();
    }

    // ─── Session claim projection (SEC-10) ──────────────────────────────────────

    [UnitTest]
    public void TryProjectValidatedClaims_DropsAuthorityClaimsFromTheValidatedToken()
    {
        // A session record stores only (type, value) pairs, so anything that lands here is
        // indistinguishable later from a claim this process minted. The framework authority
        // claim types must therefore never be copied out of a provider's ID token.
        var sourceClaims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "user-123"),
            new Claim("roles", "viewer"),
            new Claim("permission", "admin"),
            new Claim("auth_type", "admin"),
            new Claim("api_key_id", "11111111-1111-1111-1111-111111111111"),
            new Claim("api_key_name", "bootstrap"),
            new Claim("plan", "enterprise"),
            new Claim("honua_plan", "enterprise"),
            new Claim("portal_token_binding", "Referer"),
        };

        AdminAuthClaimsProjector.TryProjectValidatedClaims(sourceClaims, out var sessionClaims)
            .Should().BeTrue();

        sessionClaims.Should().NotContain(claim => claim.Type == "permission");
        sessionClaims.Should().NotContain(claim => claim.Type == "api_key_id");
        sessionClaims.Should().NotContain(claim => claim.Type == "api_key_name");
        sessionClaims.Should().NotContain(claim => claim.Type == "plan");
        sessionClaims.Should().NotContain(claim => claim.Type == "honua_plan");
        sessionClaims.Should().NotContain(claim => claim.Type == "portal_token_binding");

        // The projector chooses auth_type, not the token.
        sessionClaims.Should().ContainSingle(claim => claim.Type == "auth_type")
            .Which.Value.Should().Be("oidc");

        // Role normalization — the operator's supported grant path — is untouched.
        sessionClaims.Should().Contain(claim => claim.Type == ClaimTypes.Role && claim.Value == "viewer");
    }

    [UnitTest]
    public void TryProjectValidatedClaims_HonoursTheCallerSuppliedAuthType()
    {
        // The SAML login path projects into the same session shape and stays
        // distinguishable from an OIDC session.
        var sourceClaims = new[] { new Claim(ClaimTypes.NameIdentifier, "user-123") };

        AdminAuthClaimsProjector.TryProjectValidatedClaims(sourceClaims, out var sessionClaims, "saml")
            .Should().BeTrue();

        sessionClaims.Should().ContainSingle(claim => claim.Type == "auth_type")
            .Which.Value.Should().Be("saml");
    }

    [UnitTest]
    public void CreatePrincipal_NormalizesAuthorityClaimsFromAnOlderSessionRecord()
    {
        // A record written before the ingress sanitization existed can still hold an
        // issuer-supplied grant. Projecting it must not resurrect that authority.
        var sessionClaims = new[]
        {
            new AdminAuthSessionClaim { Type = ClaimTypes.NameIdentifier, Value = "user-123" },
            new AdminAuthSessionClaim { Type = "permission", Value = "admin" },
            new AdminAuthSessionClaim { Type = "auth_type", Value = "admin" },
        };

        var principal = AdminAuthClaimsProjector.CreatePrincipal(sessionClaims, "AdminSession");

        principal.FindFirst("permission").Should().BeNull();
        AdminApiKeyPermission.IsFullAdminPrincipal(principal).Should().BeFalse();

        var authType = principal.FindFirst("auth_type");
        authType.Should().NotBeNull();
        authType!.Value.Should().Be("oidc");
        // Stamped so the shared claims transformation keeps it.
        CanonicalSecurityActor.IsFrameworkOwnedClaim(authType).Should().BeTrue();
    }

    [UnitTest]
    public void CreatePrincipal_SamlSession_KeepsItsRecordedAuthType()
    {
        AdminAuthClaimsProjector.TryProjectValidatedClaims(
                [new Claim(ClaimTypes.NameIdentifier, "user-123")], out var sessionClaims, "saml")
            .Should().BeTrue();

        var principal = AdminAuthClaimsProjector.CreatePrincipal(sessionClaims, "AdminSession");

        principal.Claims.Where(claim => claim.Type == "auth_type").Should().ContainSingle()
            .Which.Value.Should().Be("saml");
    }

    [UnitTest]
    public void CreatePrincipal_OperatorBearer_KeepsItsOwnAuthType()
    {
        var sessionClaims = new[]
        {
            new AdminAuthSessionClaim { Type = ClaimTypes.NameIdentifier, Value = "user-123" },
            new AdminAuthSessionClaim { Type = "roles", Value = "admin" },
        };

        var principal = AdminAuthClaimsProjector.CreatePrincipal(
            sessionClaims, "OperatorBearer", "operator-bearer");

        principal.FindFirst("auth_type")!.Value.Should().Be("operator-bearer");
        principal.IsInRole("admin").Should().BeTrue();
    }

    // ────────────────────────────────────────────────────────────────────────────

    private static AdminAuthSessionStore CreateStore()
        => new(new InMemorySecurityTokenStore(), NullLogger<AdminAuthSessionStore>.Instance);
}
