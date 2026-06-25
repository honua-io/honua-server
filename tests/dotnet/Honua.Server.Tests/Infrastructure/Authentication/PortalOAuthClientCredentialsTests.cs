// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http;
using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// Unit tests for the OAuth2 <c>client_credentials</c> grant (ADR-0053, #1860) on
/// <see cref="PortalOAuthTokenService"/>. These prove, independent of the HTTP
/// pipeline: (1) with the grant disabled the request is rejected with
/// <c>unsupported_grant_type</c> exactly as before — the no-behaviour-change-by-default
/// guarantee; (2) with the grant enabled a valid API-key secret mints an opaque,
/// IP-bound portal token carrying the key's permissions as roles, and no refresh
/// token; (3) an unknown/missing secret is rejected with <c>invalid_client</c>.
/// </summary>
[SecurityTest]
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.Security)]
public sealed class PortalOAuthClientCredentialsTests
{
    private const string ClientIp = "203.0.113.10";

    private static readonly string[] ClientCredentialsGrantTypes = ["client_credentials"];
    private static readonly string[] AuthorizationCodeGrantTypes = ["authorization_code"];
    private static readonly string[] ReadOnlyScopes = ["features:read"];
    private static readonly string[] ReadWriteScopes = ["features:read", "features:write"];
    private static readonly string[] ServicesRead = ["services:read"];
    private static readonly string[] ServicesWrite = ["services:write"];
    private static readonly string[] AdminAll = ["admin:*"];

    [UnitTest]
    public async Task Exchange_ClientCredentials_WhenDisabled_ReturnsUnsupportedGrantType()
    {
        var (service, _, secret) = await CreateServiceAsync(enableClientCredentials: false);

        var result = await service.ExchangeAsync(
            ClientCredentialsRequest(secret),
            requestBinding: "ignored",
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("unsupported_grant_type");
    }

    [UnitTest]
    public async Task Exchange_ClientCredentials_WhenEnabledWithValidSecret_MintsIpBoundTokenWithRolesAndNoRefresh()
    {
        var (service, issuer, secret) = await CreateServiceAsync(
            enableClientCredentials: true,
            permissions: ["services:read", "services:write"]);

        var result = await service.ExchangeAsync(
            ClientCredentialsRequest(secret),
            requestBinding: "ignored-for-client-credentials",
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.AccessToken.Should().NotBeNullOrWhiteSpace();
        result.ExpiresInSeconds.Should().BeGreaterThan(0);
        // client_credentials never issues a refresh token (RFC 6749 §4.4.3).
        result.RefreshToken.Should().BeNull();

        // The token is IP-bound: it validates only from the issuing client IP and the
        // hydrated principal carries the API key's permissions as roles, so the RBAC
        // resolver decides per-operation access exactly as for any other principal.
        var validation = await issuer.ValidateAsync(
            result.AccessToken!,
            new PortalTokenBinding(Referer: null, ClientIp: ClientIp),
            CancellationToken.None);
        validation.Should().NotBeNull();
        validation!.Principal.IsInRole("services:read").Should().BeTrue();
        validation.Principal.IsInRole("services:write").Should().BeTrue();
        validation.Principal.FindFirstValue("auth_type").Should().NotBeNull();

        var wrongIp = await issuer.ValidateAsync(
            result.AccessToken!,
            new PortalTokenBinding(Referer: null, ClientIp: "203.0.113.99"),
            CancellationToken.None);
        wrongIp.Should().BeNull("the token is bound to the issuing client IP");
    }

    [UnitTest]
    public async Task Exchange_ClientCredentials_ScopeNarrowsToHeldPermissionsOnly()
    {
        var (service, issuer, secret) = await CreateServiceAsync(
            enableClientCredentials: true,
            permissions: ["services:read", "services:write"]);

        var request = ClientCredentialsRequest(secret) with { Scope = "services:read admin:everything" };
        var result = await service.ExchangeAsync(request, requestBinding: "x", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        var validation = await issuer.ValidateAsync(
            result.AccessToken!,
            new PortalTokenBinding(Referer: null, ClientIp: ClientIp),
            CancellationToken.None);
        validation.Should().NotBeNull();
        // The requested scope narrows to the held permission; the unheld scope is
        // dropped, never escalated.
        validation!.Principal.IsInRole("services:read").Should().BeTrue();
        validation.Principal.IsInRole("services:write").Should().BeFalse();
        validation.Principal.IsInRole("admin:everything").Should().BeFalse();
    }

    [UnitTest]
    public async Task Exchange_ClientCredentials_UnknownSecret_ReturnsInvalidClient()
    {
        var (service, _, _) = await CreateServiceAsync(enableClientCredentials: true);

        var result = await service.ExchangeAsync(
            ClientCredentialsRequest("hnua_not-a-real-key"),
            requestBinding: "x",
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("invalid_client");
    }

    [UnitTest]
    public async Task Exchange_ClientCredentials_MissingSecret_ReturnsInvalidClient()
    {
        var (service, _, _) = await CreateServiceAsync(enableClientCredentials: true);

        var request = ClientCredentialsRequest(clientSecret: null);
        var result = await service.ExchangeAsync(request, requestBinding: "x", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("invalid_client");
    }

    [UnitTest]
    public async Task Exchange_ClientCredentials_MissingClientIp_ReturnsInvalidRequest()
    {
        var (service, _, secret) = await CreateServiceAsync(enableClientCredentials: true);

        var request = ClientCredentialsRequest(secret) with { ClientIp = null };
        var result = await service.ExchangeAsync(request, requestBinding: "x", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("invalid_request");
    }

    [UnitTest]
    public async Task Exchange_FirstClassClient_MintsTokenCarryingGrantedScopesMappedToPermissions()
    {
        var (service, issuer, clientId, secret) = await CreateFirstClassServiceAsync(
            allowedGrantTypes: ClientCredentialsGrantTypes,
            allowedScopes: ReadWriteScopes,
            scopeDefinitions:
            [
                ("features:read", ServicesRead),
                ("features:write", ServicesWrite),
            ]);

        var request = FirstClassRequest(clientId, secret) with { Scope = "features:read" };
        var result = await service.ExchangeAsync(request, requestBinding: "x", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        // The token response carries exactly the granted scope (RFC 6749 §5.1).
        result.Scope.Should().Be("features:read");
        result.RefreshToken.Should().BeNull();

        var validation = await issuer.ValidateAsync(
            result.AccessToken!,
            new PortalTokenBinding(Referer: null, ClientIp: ClientIp),
            CancellationToken.None);
        validation.Should().NotBeNull();
        // The granted scope maps to its catalogue permission; the unrequested scope's
        // permission is absent (scope narrowing through the catalogue).
        validation!.Principal.IsInRole("services:read").Should().BeTrue();
        validation.Principal.IsInRole("services:write").Should().BeFalse();
    }

    [UnitTest]
    public async Task Exchange_FirstClassClient_RequestedScopeOutsideAllowList_IsDropped()
    {
        var (service, issuer, clientId, secret) = await CreateFirstClassServiceAsync(
            allowedGrantTypes: ClientCredentialsGrantTypes,
            allowedScopes: ReadOnlyScopes,
            scopeDefinitions:
            [
                ("features:read", ServicesRead),
                ("admin:all", AdminAll),
            ]);

        var request = FirstClassRequest(clientId, secret) with { Scope = "features:read admin:all" };
        var result = await service.ExchangeAsync(request, requestBinding: "x", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Scope.Should().Be("features:read");

        var validation = await issuer.ValidateAsync(
            result.AccessToken!,
            new PortalTokenBinding(Referer: null, ClientIp: ClientIp),
            CancellationToken.None);
        validation!.Principal.IsInRole("services:read").Should().BeTrue();
        validation.Principal.IsInRole("admin:*").Should().BeFalse("a scope outside the client allow-list is never escalated");
    }

    [UnitTest]
    public async Task Exchange_FirstClassClient_NotAuthorizedForGrant_ReturnsUnauthorizedClient()
    {
        var (service, _, clientId, secret) = await CreateFirstClassServiceAsync(
            allowedGrantTypes: AuthorizationCodeGrantTypes,
            allowedScopes: ReadOnlyScopes,
            scopeDefinitions: Array.Empty<(string, string[])>());

        var result = await service.ExchangeAsync(
            FirstClassRequest(clientId, secret),
            requestBinding: "x",
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("unauthorized_client");
    }

    [UnitTest]
    public async Task Exchange_FirstClassClient_WrongSecret_FallsBackAndFailsInvalidClient()
    {
        var (service, _, clientId, _) = await CreateFirstClassServiceAsync(
            allowedGrantTypes: ClientCredentialsGrantTypes,
            allowedScopes: ReadOnlyScopes,
            scopeDefinitions: Array.Empty<(string, string[])>());

        var result = await service.ExchangeAsync(
            FirstClassRequest(clientId, "secret_wrong"),
            requestBinding: "x",
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("invalid_client");
    }

    private static PortalOAuthTokenRequest FirstClassRequest(string clientId, string? clientSecret)
        => new(
            GrantType: "client_credentials",
            Code: null,
            CodeVerifier: null,
            RedirectUri: null,
            ClientId: clientId,
            RefreshToken: null,
            IncludeRefreshToken: false,
            ClientSecret: clientSecret,
            Scope: null,
            ClientIp: ClientIp);

    private static async Task<(PortalOAuthTokenService Service, IPortalTokenIssuer Issuer, string ClientId, string Secret)>
        CreateFirstClassServiceAsync(
            IReadOnlyList<string> allowedGrantTypes,
            IReadOnlyList<string> allowedScopes,
            IReadOnlyList<(string Scope, string[] Permissions)> scopeDefinitions)
    {
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var issuer = new PortalTokenIssuer(memoryCache, NullLogger<PortalTokenIssuer>.Instance);
        var store = new PortalOAuthStore(memoryCache, NullLogger<PortalOAuthStore>.Instance);
        var apiKeyStore = new InMemoryAdminApiKeyStore();

        var clientStore = new InMemoryOAuthClientStore();
        var created = await clientStore.CreateAsync(
            new OAuthClientRegistration(
                Name: "etl-worker",
                ClientType: OAuthClientType.Confidential,
                AllowedGrantTypes: allowedGrantTypes,
                RedirectUris: [],
                AllowedScopes: allowedScopes,
                ExpiresAt: null,
                CreatedBy: "test"),
            CancellationToken.None);

        var scopeCatalogue = new InMemoryOAuthScopeCatalogue();
        foreach (var (scope, permissions) in scopeDefinitions)
        {
            await scopeCatalogue.DefineAsync(
                new OAuthScopeDefinition(scope, scope, permissions),
                CancellationToken.None);
        }

        var options = Options.Create(new PortalTokenAuthenticationOptions
        {
            OAuth2 = new PortalOAuth2Options { EnableClientCredentials = true },
        });

        var jwtService = new PortalJwtAccessTokenService(issuer, options);
        var federation = new ClientCredentialsFederationService(new NullHttpClientFactory(), options);
        var service = new PortalOAuthTokenService(issuer, store, apiKeyStore, clientStore, scopeCatalogue, jwtService, federation, options);
        return (service, issuer, created.Record.ClientId, created.Secret!);
    }

    private static PortalOAuthTokenRequest ClientCredentialsRequest(string? clientSecret)
        => new(
            GrantType: "client_credentials",
            Code: null,
            CodeVerifier: null,
            RedirectUri: null,
            ClientId: "etl-worker",
            RefreshToken: null,
            IncludeRefreshToken: false,
            ClientSecret: clientSecret,
            Scope: null,
            ClientIp: ClientIp);

    private static async Task<(PortalOAuthTokenService Service, IPortalTokenIssuer Issuer, string Secret)> CreateServiceAsync(
        bool enableClientCredentials,
        IReadOnlyList<string>? permissions = null)
    {
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var issuer = new PortalTokenIssuer(memoryCache, NullLogger<PortalTokenIssuer>.Instance);
        var store = new PortalOAuthStore(memoryCache, NullLogger<PortalOAuthStore>.Instance);
        var apiKeyStore = new InMemoryAdminApiKeyStore();
        var created = await apiKeyStore.CreateAsync(
            name: "etl-worker",
            permissions: permissions ?? [],
            expiresAt: null,
            createdBy: "test",
            CancellationToken.None);

        var options = Options.Create(new PortalTokenAuthenticationOptions
        {
            OAuth2 = new PortalOAuth2Options { EnableClientCredentials = enableClientCredentials },
        });

        var clientStore = new InMemoryOAuthClientStore();
        var scopeCatalogue = new InMemoryOAuthScopeCatalogue();
        var jwtService = new PortalJwtAccessTokenService(issuer, options);
        var federation = new ClientCredentialsFederationService(new NullHttpClientFactory(), options);
        var service = new PortalOAuthTokenService(issuer, store, apiKeyStore, clientStore, scopeCatalogue, jwtService, federation, options);
        return (service, issuer, created.Key);
    }

    /// <summary>
    /// An <see cref="IHttpClientFactory"/> that hands out a plain client. Federation
    /// is disabled in these tests (no TokenEndpoint configured), so the factory is
    /// never asked to reach an external IdP.
    /// </summary>
    private sealed class NullHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
