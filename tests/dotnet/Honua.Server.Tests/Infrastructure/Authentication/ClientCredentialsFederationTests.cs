// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http;
using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.AuditLog.Export;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Infrastructure.Authentication;

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

    [UnitTest]
    public void FederationEnabled_EndpointWithUserInfo_IsDisabled()
    {
        var service = CreateService(
            new PortalOAuthClientCredentialsFederationOptions
            {
                Enabled = true,
                TokenEndpoint = "https://user:secret@idp.example.com/oauth2/token",
                RequireHttps = true,
                GrantedRoles = GrantedRoles,
            },
            _ => new HttpResponseMessage(HttpStatusCode.OK));

        service.FederationEnabled.Should().BeFalse();
    }

    [UnitTest]
    public async Task PortalOAuthBroker_IsValidDiscoveryEndpoint_BlocksHttpUserInfoAndPrivateAddresses()
    {
        (await PortalOAuthBroker.IsValidDiscoveryEndpointAsync("http://idp.example.com/token", requireHttps: true, allowPrivateNetworks: false, CancellationToken.None))
            .Should().BeFalse();
        (await PortalOAuthBroker.IsValidDiscoveryEndpointAsync("https://user:pass@idp.example.com/token", requireHttps: true, allowPrivateNetworks: true, CancellationToken.None))
            .Should().BeFalse();
        (await PortalOAuthBroker.IsValidDiscoveryEndpointAsync("https://169.254.169.254/latest/meta-data", requireHttps: true, allowPrivateNetworks: false, CancellationToken.None))
            .Should().BeFalse();
        (await PortalOAuthBroker.IsValidDiscoveryEndpointAsync("https://127.0.0.1/token", requireHttps: true, allowPrivateNetworks: false, CancellationToken.None))
            .Should().BeFalse();
        (await PortalOAuthBroker.IsValidDiscoveryEndpointAsync("https://localhost/token", requireHttps: true, allowPrivateNetworks: false, CancellationToken.None))
            .Should().BeFalse();
        (await PortalOAuthBroker.IsValidDiscoveryEndpointAsync("https://localhost/token", requireHttps: true, allowPrivateNetworks: true, CancellationToken.None))
            .Should().BeTrue();
    }

    [UnitTest]
    public void CredentialBearingHttpClients_DisableAutoRedirectAndCookies()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AuditLog:Export:Enabled"] = "true",
                ["AuditLog:Export:Splunk:Enabled"] = "true",
                ["AuditLog:Export:Splunk:Endpoint"] = "https://splunk.example.com/services/collector",
                ["AuditLog:Export:Splunk:Token"] = "splunk-token",
                ["AuditLog:Export:Sentinel:Enabled"] = "true",
                ["AuditLog:Export:Sentinel:Endpoint"] = "https://sentinel.example.com/api/logs",
                ["AuditLog:Export:Sentinel:AuthHeaderValue"] = "SharedKey token",
                ["AuditLog:Export:S3:Enabled"] = "true",
                ["AuditLog:Export:S3:Endpoint"] = "https://s3.example.com",
                ["AuditLog:Export:S3:Bucket"] = "audit-bucket",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPortalTokenAuthentication(config);
        services.AddHonuaAuditExport(config);
        using var provider = services.BuildServiceProvider();
        var optionsMonitor = provider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>();

        string[] clientNames =
        [
            PortalOAuthBroker.HttpClientName,
            ClientCredentialsFederationService.HttpClientName,
            "honua-audit-splunk",
            "honua-audit-sentinel",
            "honua-audit-s3",
        ];

        foreach (var clientName in clientNames)
        {
            var factoryOptions = optionsMonitor.Get(clientName);
            var builder = new InspectingMessageHandlerBuilder(clientName, provider);
            foreach (var configure in factoryOptions.HttpMessageHandlerBuilderActions)
            {
                configure(builder);
            }

            var handler = builder.PrimaryHandler.Should().BeOfType<SocketsHttpHandler>(clientName).Subject;
            handler.AllowAutoRedirect.Should().BeFalse($"{clientName} must not auto-follow redirects with credentials");
            handler.UseCookies.Should().BeFalse($"{clientName} must not retain cookies across outbound requests");
            handler.Dispose();
        }
    }

    private sealed class InspectingMessageHandlerBuilder(string name, IServiceProvider services) : HttpMessageHandlerBuilder
    {
        public override string? Name { get; set; } = name;

        public override HttpMessageHandler PrimaryHandler { get; set; } = new HttpClientHandler();

        public override IList<DelegatingHandler> AdditionalHandlers { get; } = [];

        public override IServiceProvider Services { get; } = services;

        public override HttpMessageHandler Build() => PrimaryHandler;
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
