// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// Regression tests for three S1 OAuth2 race conditions found in the bughunt-2 audit:
/// <list type="bullet">
/// <item><description>
/// BH2-022 — TOCTOU: <c>ConsumeAuthorizationCodeAsync</c> is non-atomic; two concurrent
/// requests with the same code can both acquire the record and obtain access tokens.
/// </description></item>
/// <item><description>
/// BH2-023 — Refresh-token rotation race: <c>ExchangeRefreshTokenAsync</c> reads the
/// refresh-token record with <c>GetRefreshTokenAsync</c> and removes it separately; two
/// concurrent callers can both pass the read before either removes, both obtaining tokens.
/// </description></item>
/// <item><description>
/// BH2-024 — Refresh token permanently destroyed: <c>RemoveRefreshTokenAsync</c> was
/// called unconditionally before <c>IssueAsync</c>; a transient <c>IssueAsync</c> failure
/// permanently deletes the refresh token and forces interactive re-authentication.
/// </description></item>
/// </list>
/// </summary>
[SecurityTest]
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.Security)]
public sealed class PortalOAuthRaceConditionTests
{
    // ─── BH2-022: authorization code TOCTOU ─────────────────────────────────────

    /// <summary>
    /// Two concurrent requests presenting the same authorization code must result in
    /// exactly one successful acquisition.  Before the fix both callers completed
    /// <c>GetAsync</c> before either called <c>RemoveAsync</c>, so both obtained a valid
    /// record and could independently mint an access token (RFC 6749 §4.1.2 violation).
    /// </summary>
    [UnitTest]
    public async Task ConsumeAuthorizationCodeAsync_ConcurrentConsumers_OnlyOneAcquires_Bh2022()
    {
        var store = CreateStore();
        var code = await store.CreateAuthorizationCodeAsync(
            new PortalOAuthAuthorizationCode
            {
                ClientId = "arcgispro",
                RedirectUri = "https://app.example.com/redirect",
                Principal = TestPrincipal(),
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            },
            CancellationToken.None);

        // Fire both consume requests concurrently with the same code value.
        var t1 = store.ConsumeAuthorizationCodeAsync(code, CancellationToken.None);
        var t2 = store.ConsumeAuthorizationCodeAsync(code, CancellationToken.None);
        var results = await Task.WhenAll(t1, t2);

        var successes = results.Count(r => r is not null);
        successes.Should().Be(1,
            "RFC 6749 §4.1.2: an authorization code is single-use — exactly one concurrent " +
            "caller must acquire the record (BH2-022)");
    }

    /// <summary>
    /// After a successful consume the code must be irrevocably gone.  Retrying the
    /// same code later must return <see langword="null"/>.
    /// </summary>
    [UnitTest]
    public async Task ConsumeAuthorizationCodeAsync_SecondCallAfterSuccess_ReturnsNull_Bh2022()
    {
        var store = CreateStore();
        var code = await store.CreateAuthorizationCodeAsync(
            new PortalOAuthAuthorizationCode
            {
                ClientId = "arcgispro",
                RedirectUri = "https://app.example.com/redirect",
                Principal = TestPrincipal(),
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            },
            CancellationToken.None);

        var first = await store.ConsumeAuthorizationCodeAsync(code, CancellationToken.None);
        var second = await store.ConsumeAuthorizationCodeAsync(code, CancellationToken.None);

        first.Should().NotBeNull("the first consumer must succeed");
        second.Should().BeNull("the second consumer must receive null — the code was already consumed");
    }

    // ─── BH2-023: refresh-token rotation race ───────────────────────────────────

    /// <summary>
    /// Two concurrent refresh-token exchanges (rotation enabled) must result in exactly
    /// one successful token acquisition.  Before the fix both callers completed
    /// <c>GetRefreshTokenAsync</c> before either called <c>RemoveRefreshTokenAsync</c>,
    /// so both minted independent access tokens from the same credential.
    /// </summary>
    [UnitTest]
    public async Task ConsumeRefreshTokenAsync_ConcurrentConsumers_OnlyOneAcquires_Bh2023()
    {
        var store = CreateStore();
        var tokenValue = await store.CreateRefreshTokenAsync(
            new PortalOAuthRefreshToken
            {
                ClientId = "arcgispro",
                Principal = TestPrincipal(),
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(90),
            },
            CancellationToken.None);

        var t1 = store.ConsumeRefreshTokenAsync(tokenValue, CancellationToken.None);
        var t2 = store.ConsumeRefreshTokenAsync(tokenValue, CancellationToken.None);
        var results = await Task.WhenAll(t1, t2);

        var successes = results.Count(r => r is not null);
        successes.Should().Be(1,
            "refresh-token rotation: only one concurrent redemption must succeed; " +
            "the second must receive null and return invalid_grant (BH2-023)");
    }

    /// <summary>
    /// When a concurrent caller steals the consume slot the service must respond with
    /// <c>invalid_grant</c> rather than silently issuing a duplicate token or throwing.
    /// </summary>
    [UnitTest]
    public async Task ExchangeRefreshTokenAsync_ConcurrentSecondConsumer_ReturnsInvalidGrant_Bh2023()
    {
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var store = new PortalOAuthStore(memoryCache, NullLogger<PortalOAuthStore>.Instance);
        var realIssuer = new PortalTokenIssuer(memoryCache, NullLogger<PortalTokenIssuer>.Instance);
        var service = BuildService(store, realIssuer, rotateRefreshTokens: true);

        // Seed a refresh token directly in the store.
        var refreshValue = await store.CreateRefreshTokenAsync(
            new PortalOAuthRefreshToken
            {
                ClientId = "arcgispro",
                Principal = TestPrincipal(),
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(90),
            },
            CancellationToken.None);

        // Simulate the concurrent-consumer scenario: consume the token out from under
        // the service before the service's own ConsumeRefreshTokenAsync can act.
        var preConsumed = await store.ConsumeRefreshTokenAsync(refreshValue, CancellationToken.None);
        preConsumed.Should().NotBeNull("sanity: direct consume must succeed");

        // The service now tries to exchange the same (already consumed) token.
        var result = await service.ExchangeAsync(
            RefreshTokenRequest(refreshValue, "arcgispro"),
            requestBinding: "https://app.example.com",
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("invalid_grant",
            "after the atomic consume slot is taken the service must return invalid_grant (BH2-023)");
    }

    // ─── BH2-024: refresh token destroyed when IssueAsync throws ────────────────

    /// <summary>
    /// When <see cref="IPortalTokenIssuer.IssueAsync"/> throws after the refresh token
    /// has been atomically consumed, the store must restore the original token so the
    /// client can retry the same request.  Before the fix the token was irrecoverably
    /// deleted and every retry returned <c>invalid_grant</c>, forcing re-authentication.
    /// </summary>
    [UnitTest]
    public async Task ExchangeRefreshTokenAsync_WhenIssueAsyncThrows_RefreshTokenIsRestored_Bh2024()
    {
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var store = new PortalOAuthStore(memoryCache, NullLogger<PortalOAuthStore>.Instance);

        // Seed a refresh token directly in the store (bypass the full auth-code flow).
        var refreshValue = await store.CreateRefreshTokenAsync(
            new PortalOAuthRefreshToken
            {
                ClientId = "arcgispro",
                Principal = TestPrincipal(),
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(90),
            },
            CancellationToken.None);

        // Build a service whose token issuer throws on every IssueAsync call.
        var failingIssuer = Substitute.For<IPortalTokenIssuer>();
        failingIssuer
            .IssueAsync(Arg.Any<PortalTokenIssueRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<PortalTokenIssuance>(
                new InvalidOperationException("Simulated transient infrastructure failure")));

        var service = BuildService(store, failingIssuer, rotateRefreshTokens: true);

        // The service atomically consumes the token and then calls IssueAsync which throws.
        var act = () => service.ExchangeAsync(
            RefreshTokenRequest(refreshValue, "arcgispro"),
            requestBinding: "https://app.example.com",
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "the transient IssueAsync failure must propagate to the caller");

        // After the failure the original refresh token must be restored in the store.
        var restored = await store.GetRefreshTokenAsync(refreshValue, CancellationToken.None);
        restored.Should().NotBeNull(
            "the refresh token must be restored after a transient IssueAsync failure " +
            "so the client can retry without re-authenticating (BH2-024)");
        restored!.Principal.PrincipalId.Should().Be("user@example.com");
        restored.ClientId.Should().Be("arcgispro");
    }

    /// <summary>
    /// After restoration the client must be able to exchange the original refresh token
    /// successfully with a healthy issuer — the retry path is fully functional.
    /// </summary>
    [UnitTest]
    public async Task ExchangeRefreshTokenAsync_RetryAfterRestoration_Succeeds_Bh2024()
    {
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var store = new PortalOAuthStore(memoryCache, NullLogger<PortalOAuthStore>.Instance);

        var refreshValue = await store.CreateRefreshTokenAsync(
            new PortalOAuthRefreshToken
            {
                ClientId = "arcgispro",
                Principal = TestPrincipal(),
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(90),
            },
            CancellationToken.None);

        // First attempt — IssueAsync throws.
        var failingIssuer = Substitute.For<IPortalTokenIssuer>();
        failingIssuer
            .IssueAsync(Arg.Any<PortalTokenIssueRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<PortalTokenIssuance>(
                new InvalidOperationException("Transient failure")));

        var failingService = BuildService(store, failingIssuer, rotateRefreshTokens: true);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => failingService.ExchangeAsync(
                RefreshTokenRequest(refreshValue, "arcgispro"),
                "https://app.example.com", CancellationToken.None));

        // Second attempt — healthy issuer.
        var healthyIssuer = new PortalTokenIssuer(memoryCache, NullLogger<PortalTokenIssuer>.Instance);
        var healthyService = BuildService(store, healthyIssuer, rotateRefreshTokens: true);

        var retryResult = await healthyService.ExchangeAsync(
            RefreshTokenRequest(refreshValue, "arcgispro"),
            requestBinding: "https://app.example.com",
            CancellationToken.None);

        retryResult.Succeeded.Should().BeTrue(
            "a retry after transient IssueAsync failure must succeed when the issuer recovers (BH2-024)");
        retryResult.AccessToken.Should().NotBeNullOrWhiteSpace();
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static PortalOAuthStore CreateStore()
    {
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        return new PortalOAuthStore(memoryCache, NullLogger<PortalOAuthStore>.Instance);
    }

    private static PortalOAuthTokenService BuildService(
        PortalOAuthStore store,
        IPortalTokenIssuer issuer,
        bool rotateRefreshTokens = true)
    {
        var options = Options.Create(new PortalTokenAuthenticationOptions
        {
            OAuth2 = new PortalOAuth2Options { RotateRefreshTokens = rotateRefreshTokens },
            RequireHttps = false,
        });
        var jwtService = new PortalJwtAccessTokenService(issuer, options);
        var federation = new ClientCredentialsFederationService(new NullHttpClientFactory(), options);
        return new PortalOAuthTokenService(
            issuer, store,
            new InMemoryAdminApiKeyStore(),
            new InMemoryOAuthClientStore(),
            new InMemoryOAuthScopeCatalogue(),
            jwtService, federation, options);
    }

    private static PortalOAuthTokenRequest RefreshTokenRequest(string refreshToken, string clientId)
        => new(
            GrantType: "refresh_token",
            Code: null,
            CodeVerifier: null,
            RedirectUri: null,
            ClientId: clientId,
            RefreshToken: refreshToken,
            IncludeRefreshToken: true);

    private static PortalOAuthPrincipal TestPrincipal()
        => new()
        {
            PrincipalId = "user@example.com",
            DisplayName = "Test User",
            TenantId = null,
            Roles = ["org_user"],
        };

    /// <summary>
    /// No-op HTTP client factory used when federation is disabled in tests.
    /// </summary>
    private sealed class NullHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
