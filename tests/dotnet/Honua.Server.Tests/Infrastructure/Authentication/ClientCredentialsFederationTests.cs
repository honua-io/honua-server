// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http;
using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// Unit tests for the pluggable IdP/OIDC federation of the OAuth2
/// <c>client_credentials</c> grant (ADR-0053 Increment 3, #1889). These prove the
/// delegation logic without a live IdP via a stubbed token endpoint: a successful
/// federated exchange yields the operator-configured roles; an auth failure or a
/// disabled configuration yields a federation miss (null) so the caller falls back
/// to the in-tree credential path.
/// </summary>
[SecurityTest]
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.Security)]
public sealed class ClientCredentialsFederationTests
{
    private const string TokenEndpoint = "https://idp.example.com/oauth2/token";

    private static readonly string[] GrantedRoles = ["services:read", "services:write"];

    [UnitTest]
    public async Task TryAuthenticate_WhenDisabled_ReturnsNull()
    {
        var service = CreateService(
            new PortalOAuthClientCredentialsFederationOptions { Enabled = false },
            _ => new HttpResponseMessage(HttpStatusCode.OK));

        var roles = await service.TryAuthenticateAsync("client", "secret", requestedScope: null, CancellationToken.None);

        roles.Should().BeNull("federation is off, so the in-tree path must handle the credential");
    }

    [UnitTest]
    public async Task TryAuthenticate_PlaintextEndpointWhenHttpsRequired_ReturnsNull()
    {
        var service = CreateService(
            new PortalOAuthClientCredentialsFederationOptions
            {
                Enabled = true,
                TokenEndpoint = "http://idp.example.com/oauth2/token",
                RequireHttps = true,
                GrantedRoles = GrantedRoles,
            },
            _ => new HttpResponseMessage(HttpStatusCode.OK));

        var roles = await service.TryAuthenticateAsync("client", "secret", requestedScope: null, CancellationToken.None);

        roles.Should().BeNull("a plaintext IdP endpoint must be refused when HTTPS is required");
    }

    [UnitTest]
    public async Task TryAuthenticate_WhenIdpAcceptsCredentials_ReturnsConfiguredRoles()
    {
        var service = CreateService(
            new PortalOAuthClientCredentialsFederationOptions
            {
                Enabled = true,
                TokenEndpoint = TokenEndpoint,
                GrantedRoles = GrantedRoles,
            },
            _ => JsonResponse(HttpStatusCode.OK, """{"access_token":"idp-token","token_type":"Bearer","expires_in":3600}"""));

        var roles = await service.TryAuthenticateAsync("client", "secret", requestedScope: "services:read", CancellationToken.None);

        roles.Should().NotBeNull();
        roles.Should().BeEquivalentTo("services:read", "services:write");
    }

    [UnitTest]
    public async Task TryAuthenticate_WhenIdpRejectsCredentials_ReturnsNull()
    {
        var service = CreateService(
            new PortalOAuthClientCredentialsFederationOptions
            {
                Enabled = true,
                TokenEndpoint = TokenEndpoint,
                GrantedRoles = GrantedRoles,
            },
            _ => JsonResponse(HttpStatusCode.Unauthorized, """{"error":"invalid_client"}"""));

        var roles = await service.TryAuthenticateAsync("client", "bad-secret", requestedScope: null, CancellationToken.None);

        roles.Should().BeNull("a rejected credential is a federation miss, not a granted token");
    }

    [UnitTest]
    public async Task TryAuthenticate_WhenIdpReturnsNoAccessToken_ReturnsNull()
    {
        var service = CreateService(
            new PortalOAuthClientCredentialsFederationOptions
            {
                Enabled = true,
                TokenEndpoint = TokenEndpoint,
                GrantedRoles = GrantedRoles,
            },
            _ => JsonResponse(HttpStatusCode.OK, """{"token_type":"Bearer"}"""));

        var roles = await service.TryAuthenticateAsync("client", "secret", requestedScope: null, CancellationToken.None);

        roles.Should().BeNull("a 200 without an access_token is not a successful exchange");
    }

    [UnitTest]
    public async Task TryAuthenticate_WhenIdpAcceptsButNoRolesConfigured_ReturnsEmpty()
    {
        var service = CreateService(
            new PortalOAuthClientCredentialsFederationOptions
            {
                Enabled = true,
                TokenEndpoint = TokenEndpoint,
                GrantedRoles = [],
            },
            _ => JsonResponse(HttpStatusCode.OK, """{"access_token":"idp-token"}"""));

        var roles = await service.TryAuthenticateAsync("client", "secret", requestedScope: null, CancellationToken.None);

        // The IdP authenticated the client but the operator granted no local roles:
        // the federated client gets a token with no privileges — never an escalation.
        roles.Should().NotBeNull();
        roles.Should().BeEmpty();
    }

    private static ClientCredentialsFederationService CreateService(
        PortalOAuthClientCredentialsFederationOptions federationOptions,
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var options = Options.Create(new PortalTokenAuthenticationOptions
        {
            OAuth2 = new PortalOAuth2Options { ClientCredentialsFederation = federationOptions },
        });

        return new ClientCredentialsFederationService(new StubHttpClientFactory(responder), options);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private sealed class StubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> responder) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(responder));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
