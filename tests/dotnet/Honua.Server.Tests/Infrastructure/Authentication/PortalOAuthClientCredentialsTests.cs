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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// Unit tests for the OAuth2 <c>client_credentials</c> grant (ADR-0053, #1860) on
/// <see cref="PortalOAuthTokenService"/>. These prove, independent of the HTTP
/// pipeline: (1) with the grant disabled the request is rejected with
/// <c>unsupported_grant_type</c> exactly as before — the no-behaviour-change-by-default
/// guarantee; (2) with the grant enabled a full-admin API-key secret mints an opaque,
/// IP-bound portal token carrying the admin role, and no refresh token; (3) a
/// constrained key is refused with <c>unauthorized_client</c> rather than having its
/// permission labels turned into roles (#4577); (4) an unknown/missing secret is
/// rejected with <c>invalid_client</c>.
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
    public async Task Exchange_ClientCredentials_WhenEnabledWithFullAdminSecret_MintsIpBoundAdminTokenAndNoRefresh()
    {
        var (service, issuer, secret) = await CreateServiceAsync(
            enableClientCredentials: true,
            permissions: ["admin:*", "field-editor"]);

        var result = await service.ExchangeAsync(
            ClientCredentialsRequest(secret),
            requestBinding: "ignored-for-client-credentials",
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.AccessToken.Should().NotBeNullOrWhiteSpace();
        result.ExpiresInSeconds.Should().BeGreaterThan(0);
        // client_credentials never issues a refresh token (RFC 6749 §4.4.3).
        result.RefreshToken.Should().BeNull();

        // The token is IP-bound: it validates only from the issuing client IP. The
        // hydrated principal carries the admin role the key confers on X-API-Key, and
        // no permission label becomes a role (#4577).
        var validation = await issuer.ValidateAsync(
            result.AccessToken!,
            new PortalTokenBinding(Referer: null, ClientIp: ClientIp),
            CancellationToken.None);
        validation.Should().NotBeNull();
        validation!.Principal.IsInRole("admin").Should().BeTrue();
        validation.Principal.IsInRole("admin:*").Should().BeFalse();
        validation.Principal.IsInRole("field-editor").Should().BeFalse();
        validation.Principal.FindFirstValue("auth_type").Should().NotBeNull();

        var wrongIp = await issuer.ValidateAsync(
            result.AccessToken!,
            new PortalTokenBinding(Referer: null, ClientIp: "203.0.113.99"),
            CancellationToken.None);
        wrongIp.Should().BeNull("the token is bound to the issuing client IP");
    }

    [UnitTest]
    public async Task Exchange_ClientCredentials_TokenFollowsTheApiKeyItWasIssuedFrom()
    {
        var keyExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var (service, issuer, secret, keyStore, keyId) = await CreateKeyBoundServiceAsync(
            enableClientCredentials: true,
            permissions: ["admin:*"],
            keyExpiresAt: keyExpiresAt);

        var result = await service.ExchangeAsync(ClientCredentialsRequest(secret), requestBinding: "x", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        // The token lifetime is clamped to the key's own expiry.
        result.ExpiresInSeconds.Should().BeLessThanOrEqualTo(5 * 60);
        var binding = new PortalTokenBinding(Referer: null, ClientIp: ClientIp);
        (await issuer.ValidateAsync(result.AccessToken!, binding, CancellationToken.None)).Should().NotBeNull();

        await keyStore.RevokeAsync(keyId, CancellationToken.None);

        (await issuer.ValidateAsync(result.AccessToken!, binding, CancellationToken.None))
            .Should().BeNull("the API key the token was issued from was revoked");
    }

    [UnitTest]
    public async Task Exchange_ClientCredentials_ScopeNeverAddsUnheldRoles()
    {
        var (service, issuer, secret) = await CreateServiceAsync(
            enableClientCredentials: true,
            permissions: ["admin:*"]);

        var request = ClientCredentialsRequest(secret) with { Scope = "field-editor admin:everything" };
        var result = await service.ExchangeAsync(request, requestBinding: "x", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        var validation = await issuer.ValidateAsync(
            result.AccessToken!,
            new PortalTokenBinding(Referer: null, ClientIp: ClientIp),
            CancellationToken.None);
        validation.Should().NotBeNull();
        // Requested scopes the token would not hold are dropped, never escalated.
        validation!.Principal.IsInRole("admin").Should().BeTrue();
        validation.Principal.IsInRole("field-editor").Should().BeFalse();
        validation.Principal.IsInRole("admin:everything").Should().BeFalse();
    }

    [UnitTest]
    public async Task Exchange_ClientCredentials_ConstrainedKeys_AreRefusedNotProjectedAsRoles()
    {
        // #4577: X-API-Key authenticates these keys as non-admin principals with
        // permission claims. A token holds roles only, so the exchange is refused
        // instead of turning a label such as "field-editor" into a role.
        var operation = AdminApiKeyPermission.CreateApprovedOperationGrants(
            "PUT", "/api/v1/admin/metadata/layers/1/filter", "tenant-a");
        string[][] grants =
        [
            ["services:read", "services:write"],
            ["field-editor"],
            ["read:allowed-service"],
            ["write:allowed-service/1"],
            ["admin:read"],
            ["admin:read", "admin:approve"],
            ["ops:read"],
            operation.ToArray(),
            [.. operation, "admin:*"]
        ];

        foreach (var permissions in grants)
        {
            var (service, _, secret) = await CreateServiceAsync(
                enableClientCredentials: true,
                permissions: permissions);

            var result = await service.ExchangeAsync(
                ClientCredentialsRequest(secret),
                requestBinding: "x",
                CancellationToken.None);

            result.Succeeded.Should().BeFalse(string.Join(',', permissions));
            result.Error.Should().Be("unauthorized_client");
            result.AccessToken.Should().BeNull();
        }
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
        var (service, issuer, secret, _, _) = await CreateKeyBoundServiceAsync(
            enableClientCredentials, permissions);
        return (service, issuer, secret);
    }

    /// <summary>
    /// Builds the service over an issuer wired to the real source validator, so a token
    /// minted from the API key is re-checked against that key on every restore.
    /// </summary>
    private static async Task<(PortalOAuthTokenService Service, IPortalTokenIssuer Issuer, string Secret, InMemoryAdminApiKeyStore KeyStore, Guid KeyId)>
        CreateKeyBoundServiceAsync(
            bool enableClientCredentials,
            IReadOnlyList<string>? permissions = null,
            DateTimeOffset? keyExpiresAt = null)
    {
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var apiKeyStore = new InMemoryAdminApiKeyStore();
        var created = await apiKeyStore.CreateAsync(
            name: "etl-worker",
            permissions: permissions ?? [],
            expiresAt: keyExpiresAt,
            createdBy: "test",
            CancellationToken.None);

        var options = Options.Create(new PortalTokenAuthenticationOptions
        {
            OAuth2 = new PortalOAuth2Options { EnableClientCredentials = enableClientCredentials },
            SourceRevalidationSeconds = 0,
        });

        var services = new ServiceCollection()
            .AddSingleton<IAdminApiKeyStore>(apiKeyStore)
            .AddSingleton<IPortalTokenSourceValidator>(sp => new PortalTokenSourceValidator(memoryCache, options, sp))
            .BuildServiceProvider();
        var issuer = new PortalTokenIssuer(memoryCache, NullLogger<PortalTokenIssuer>.Instance, serviceProvider: services);
        var store = new PortalOAuthStore(memoryCache, NullLogger<PortalOAuthStore>.Instance);

        var clientStore = new InMemoryOAuthClientStore();
        var scopeCatalogue = new InMemoryOAuthScopeCatalogue();
        var jwtService = new PortalJwtAccessTokenService(issuer, options);
        var federation = new ClientCredentialsFederationService(new NullHttpClientFactory(), options);
        var service = new PortalOAuthTokenService(issuer, store, apiKeyStore, clientStore, scopeCatalogue, jwtService, federation, options);
        return (service, issuer, created.Key, apiKeyStore, created.Record.Id);
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
