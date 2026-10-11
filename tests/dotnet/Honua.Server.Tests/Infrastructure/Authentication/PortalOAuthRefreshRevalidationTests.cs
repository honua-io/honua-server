// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.Identity.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// Refresh tokens stay bound to the original expiry and the live identity. Rotation does
/// not open a new 90-day window, and a deprovisioned principal cannot mint another token.
/// </summary>
[SecurityTest]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class PortalOAuthRefreshRevalidationTests
{
    private const string ClientId = "arcgis-client";

    [UnitTest]
    public async Task Rotation_DoesNotExtendTheOriginalRefreshLifetime()
    {
        var harness = CreateHarness();
        var originalExpiry = DateTimeOffset.UtcNow.AddDays(2);
        var refresh = await SeedRefreshAsync(harness.Store, originalExpiry, roles: ["admin"]);

        var result = await harness.Service.ExchangeAsync(Refresh(refresh), "binding", CancellationToken.None);
        var rotated = await harness.Store.GetRefreshTokenAsync(result.RefreshToken, CancellationToken.None);

        Assert.True(result.Succeeded, result.ErrorDescription);
        Assert.NotNull(rotated);
        Assert.Equal(originalExpiry, rotated!.ExpiresAt);
        Assert.True(rotated.ExpiresAt < DateTimeOffset.UtcNow.AddDays(30));
    }

    [UnitTest]
    public async Task Refresh_ReplacesRolesAndRejectsInactiveOrFailingMembership()
    {
        var inactive = new ScriptedMembership(new PrincipalMembership(false, ["admin"]));
        var inactiveHarness = CreateHarness(inactive);
        var inactiveToken = await SeedRefreshAsync(inactiveHarness.Store, DateTimeOffset.UtcNow.AddDays(2), ["admin"]);
        var inactiveResult = await inactiveHarness.Service.ExchangeAsync(Refresh(inactiveToken), "binding", CancellationToken.None);
        Assert.False(inactiveResult.Succeeded);
        Assert.Equal("invalid_grant", inactiveResult.Error);
        Assert.NotNull(await inactiveHarness.Store.GetRefreshTokenAsync(inactiveToken, CancellationToken.None));

        var demoted = new ScriptedMembership(new PrincipalMembership(true, ["viewer"]));
        var demotedHarness = CreateHarness(demoted);
        var demotedToken = await SeedRefreshAsync(demotedHarness.Store, DateTimeOffset.UtcNow.AddDays(2), ["admin"]);
        var demotedResult = await demotedHarness.Service.ExchangeAsync(Refresh(demotedToken), "binding", CancellationToken.None);
        Assert.True(demotedResult.Succeeded, demotedResult.ErrorDescription);
        var validation = await demotedHarness.Issuer.ValidateAsync(
            demotedResult.AccessToken!,
            new PortalTokenBinding(Referer: null, ClientIp: null),
            CancellationToken.None);
        Assert.NotNull(validation);
        Assert.True(validation!.Principal.IsInRole("viewer"));
        Assert.False(validation.Principal.IsInRole("admin"));

        var failing = new ScriptedMembership(exception: new InvalidOperationException("store down"));
        var failingHarness = CreateHarness(failing);
        var failingToken = await SeedRefreshAsync(failingHarness.Store, DateTimeOffset.UtcNow.AddDays(2), ["admin"]);
        var failingResult = await failingHarness.Service.ExchangeAsync(Refresh(failingToken), "binding", CancellationToken.None);
        Assert.False(failingResult.Succeeded);
        Assert.Equal("invalid_grant", failingResult.Error);
    }

    [UnitTest]
    public async Task Refresh_ClampsAccessTokenToTheSourceExpiry()
    {
        var harness = CreateHarness();
        var sourceExpiry = DateTimeOffset.UtcNow.AddSeconds(45);
        var refresh = await SeedRefreshAsync(
            harness.Store,
            DateTimeOffset.UtcNow.AddDays(2),
            ["viewer"],
            sourceExpiry);

        var result = await harness.Service.ExchangeAsync(Refresh(refresh), "binding", CancellationToken.None);

        Assert.True(result.Succeeded, result.ErrorDescription);
        Assert.InRange(result.ExpiresInSeconds, 1, 120);
    }

    [UnitTest]
    public async Task Revocation_RejectsTokensIssuedBeforeTheMarker()
    {
        var harness = CreateHarness();
        var refresh = await SeedRefreshAsync(harness.Store, DateTimeOffset.UtcNow.AddDays(2), ["admin"]);
        await PortalOAuthRefreshRevoker.RevokeManagedUserAsync(
            harness.Store,
            new ManagedUser
            {
                UserId = "user@example.com",
                DisplayName = "Test User",
                ProvisioningSource = "oidc",
                ExternalId = "subject-1",
                ExternalIssuer = "https://idp.example",
            },
            CancellationToken.None);

        var revoked = await harness.Service.ExchangeAsync(Refresh(refresh), "binding", CancellationToken.None);
        Assert.False(revoked.Succeeded);
        Assert.Equal("invalid_grant", revoked.Error);

        var fresh = await SeedRefreshAsync(
            harness.Store,
            DateTimeOffset.UtcNow.AddDays(2),
            ["viewer"],
            issuedAt: DateTimeOffset.UtcNow.AddSeconds(5));
        var accepted = await harness.Service.ExchangeAsync(Refresh(fresh), "binding", CancellationToken.None);
        Assert.True(accepted.Succeeded, accepted.ErrorDescription);
    }

    private static PortalOAuthTokenRequest Refresh(string refreshToken) => new(
        GrantType: "refresh_token",
        Code: null,
        CodeVerifier: null,
        RedirectUri: null,
        ClientId: ClientId,
        RefreshToken: refreshToken,
        IncludeRefreshToken: true);

    private static async Task<string> SeedRefreshAsync(
        PortalOAuthStore store,
        DateTimeOffset expiresAt,
        string[] roles,
        DateTimeOffset? sourceExpiresAt = null,
        DateTimeOffset? issuedAt = null)
    {
        return await store.CreateRefreshTokenAsync(
            new PortalOAuthRefreshToken
            {
                ClientId = ClientId,
                ClientType = PortalTokenClientType.Bearer,
                BindingValue = string.Empty,
                IssuedAt = issuedAt ?? DateTimeOffset.UtcNow,
                ExpiresAt = expiresAt,
                Principal = new PortalOAuthPrincipal
                {
                    PrincipalId = "user@example.com",
                    DisplayName = "Test User",
                    Roles = roles,
                    RolesRequireClaimsMappingEntitlement = false,
                    RolesWithoutClaimsMapping = roles,
                    Source = new PortalCredentialSource(
                        PortalCredentialSourceKind.FederatedToken,
                        Reference: "https://idp.example|subject-1",
                        ExpiresAt: sourceExpiresAt),
                },
            },
            CancellationToken.None);
    }

    private static Harness CreateHarness(IPrincipalMembershipSource? membership = null)
    {
        var memory = new MemoryCache(new MemoryCacheOptions());
        var store = new PortalOAuthStore(new MemoryCache(new MemoryCacheOptions()), NullLogger<PortalOAuthStore>.Instance);
        var options = Options.Create(new PortalTokenAuthenticationOptions
        {
            OAuth2 = new PortalOAuth2Options { RotateRefreshTokens = true },
            RequireHttps = false,
        });
        var services = new ServiceCollection();
        services.AddSingleton<IPortalTokenSourceValidator>(new PortalTokenSourceValidator(memory, options, services.BuildServiceProvider()));
        var issuer = new PortalTokenIssuer(
            memory,
            NullLogger<PortalTokenIssuer>.Instance,
            serviceProvider: services.BuildServiceProvider());
        var service = new PortalOAuthTokenService(
            issuer,
            store,
            new InMemoryAdminApiKeyStore(),
            new InMemoryOAuthClientStore(),
            new InMemoryOAuthScopeCatalogue(),
            new PortalJwtAccessTokenService(issuer, options),
            new ClientCredentialsFederationService(new NullHttpClientFactory(), options),
            options,
            membership);
        return new Harness(service, store, issuer);
    }

    private sealed record Harness(PortalOAuthTokenService Service, PortalOAuthStore Store, PortalTokenIssuer Issuer);

    private sealed class ScriptedMembership(PrincipalMembership? membership = null, Exception? exception = null)
        : IPrincipalMembershipSource
    {
        public Task<PrincipalMembership?> ResolveMembershipAsync(string principalId, CancellationToken cancellationToken = default)
            => ResolveMembershipAsync(principalId, issuer: null, cancellationToken);

        public Task<PrincipalMembership?> ResolveMembershipAsync(
            string principalId,
            string? issuer,
            CancellationToken cancellationToken = default)
        {
            if (exception is not null)
            {
                return Task.FromException<PrincipalMembership?>(exception);
            }

            return Task.FromResult(membership);
        }
    }

    private sealed class NullHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
