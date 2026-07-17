// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using FluentAssertions.Execution;
using Honua.Ai.Protocols.Mcp;
using Honua.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Integration tests for the RFC 9728 OAuth 2.0 Protected Resource Metadata document
/// published for the <c>/mcp</c> resource (honua-server#2849). Asserts the document shape
/// against the RFC, the well-known URI construction rule, and the capability-honesty
/// posture that the surface is absent — not empty — with no authorization server
/// configured.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Mcp)]
[Operation(Operations.GetMetadata)]
public sealed class McpProtectedResourceMetadataTests
{
    // The metadata path is spelled as a literal at each request site below rather than
    // routed through a constant. EndpointRegistryDriftTests proves every EndpointRegistry
    // entry is backed by a same-method HTTP request by scanning this source for the route
    // path, and it cannot see through a const reference. This route is registry-tracked
    // but only conditionally deployed (no OIDC authority => absent), so that source-level
    // proof is its only drift gate — keep the literal at the call site.
    private const string GenericAuthority = "https://auth.example.com";
    private const string PublicBaseUrl = "https://mcp.example.com";
    private const string TestTenantId = "11111111-1111-1111-1111-111111111111";
    private const string TestClientId = "22222222-2222-2222-2222-222222222222";

    [IntegrationTest]
    [Endpoint("GET /.well-known/oauth-protected-resource/mcp")]
    public async Task ProtectedResourceMetadata_WithOidcConfigured_ReturnsRfc9728Document()
    {
        var fixture = CreateOidcFixture();

        try
        {
            await fixture.InitializeAsync();
            var client = fixture.CreateClient();

            var response = await client.GetAsync("/.well-known/oauth-protected-resource/mcp");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.Should().Be(
                "application/json",
                because: "RFC 9728 section 3.2 requires the application/json content type");

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;

            using (new AssertionScope())
            {
                root.ValueKind.Should().Be(JsonValueKind.Object);

                root.TryGetProperty("resource", out var resource).Should().BeTrue(
                    "resource is REQUIRED by RFC 9728 section 2");
                resource.GetString().Should().Be(
                    PublicBaseUrl + "/mcp",
                    because: "the resource identifier is the /mcp resource this document describes");

                root.TryGetProperty("authorization_servers", out var servers).Should().BeTrue();
                servers.ValueKind.Should().Be(JsonValueKind.Array);
                servers.EnumerateArray().Select(element => element.GetString())
                    .Should().Contain(GenericAuthority);

                root.TryGetProperty("resource_name", out var name).Should().BeTrue();
                name.GetString().Should().NotBeNullOrWhiteSpace();

                root.TryGetProperty("scopes_supported", out _).Should().BeFalse(
                    "the surface authenticates but does not enforce OAuth scopes yet (#2851), so advertising them would be dishonest");
                root.TryGetProperty("bearer_methods_supported", out var bearerMethods).Should().BeTrue(
                    "the surface now accepts Authorization: Bearer tokens as a resource server (#2850)");
                bearerMethods.ValueKind.Should().Be(JsonValueKind.Array);
                bearerMethods.EnumerateArray().Select(element => element.GetString())
                    .Should().Equal("header", because: "the /mcp resource reads the token only from the Authorization header (RFC 6750)");
            }
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /.well-known/oauth-protected-resource/mcp")]
    public async Task ProtectedResourceMetadata_DerivesAuthorizationServersFromEveryConfiguredAuthority()
    {
        var fixture = CreateOidcFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("Oidc:AzureAd:Enabled", "true");
                builder.UseSetting("Oidc:AzureAd:TenantId", TestTenantId);
                builder.UseSetting("Oidc:AzureAd:ClientId", TestClientId);
                builder.UseSetting("Oidc:AzureAd:ClientSecret", "azure-secret-value-minimum-length");
            });

        try
        {
            await fixture.InitializeAsync();
            var client = fixture.CreateClient();

            var response = await client.GetAsync("/.well-known/oauth-protected-resource/mcp");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var servers = document.RootElement.GetProperty("authorization_servers")
                .EnumerateArray()
                .Select(element => element.GetString())
                .ToArray();

            servers.Should().BeEquivalentTo(
                [$"https://login.microsoftonline.com/{TestTenantId}/v2.0", GenericAuthority],
                because: "the document mirrors the OIDC authority configuration rather than a parallel config surface");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /.well-known/oauth-protected-resource/mcp")]
    public async Task ProtectedResourceMetadata_WithNoAuthorizationServerConfigured_IsAbsent()
    {
        var fixture = CreateBaseFixture();

        try
        {
            await fixture.InitializeAsync();
            var client = fixture.CreateClient();

            var response = await client.GetAsync("/.well-known/oauth-protected-resource/mcp");

            response.StatusCode.Should().Be(
                HttpStatusCode.NotFound,
                because: "with no authorization server the endpoint must be absent, not serve an empty document (#2803)");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void BuildMetadataUrl_ForResourceWithPath_InsertsWellKnownBetweenHostAndPath()
    {
        var url = McpProtectedResourceMetadata.BuildMetadataUrl(new Uri("https://resource.example.com/mcp"));

        url.Should().Be(
            "https://resource.example.com/.well-known/oauth-protected-resource/mcp",
            because: "RFC 9728 section 3 inserts the well-known path between the host and the resource path");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void BuildMetadataUrl_ForRootResource_OmitsTerminatingSlash()
    {
        var url = McpProtectedResourceMetadata.BuildMetadataUrl(new Uri("https://resource.example.com/"));

        url.Should().Be(
            "https://resource.example.com/.well-known/oauth-protected-resource",
            because: "RFC 9728 section 3 removes a terminating slash following the host component");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TryBuildChallenge_WithAuthorizationServerConfigured_CarriesResourceMetadataParameter()
    {
        var context = CreateHttpContext(oidcEnabled: true);

        McpProtectedResourceMetadataEndpointExtensions.TryBuildChallenge(context, out var challenge)
            .Should().BeTrue();

        challenge.Should().Be(
            $"Bearer resource_metadata=\"{PublicBaseUrl}/.well-known/oauth-protected-resource/mcp\"",
            because: "RFC 9728 section 5.1 defines a quoted resource_metadata parameter on the Bearer challenge");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TryBuildChallenge_WithNoAuthorizationServerConfigured_EmitsNoChallenge()
    {
        var context = CreateHttpContext(oidcEnabled: false);

        McpProtectedResourceMetadataEndpointExtensions.TryBuildChallenge(context, out _)
            .Should().BeFalse(
                "there is no metadata document to point a resource_metadata parameter at");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ResolveAuthorizationServers_WhenOidcDisabled_IsEmpty()
    {
        var options = new OidcAuthenticationOptions
        {
            Enabled = false,
            Generic = new GenericOidcProviderOptions
            {
                Enabled = true,
                Authority = GenericAuthority,
                ClientId = "client"
            }
        };

        McpProtectedResourceMetadata.ResolveAuthorizationServers(options).Should().BeEmpty(
            "a provider configured under a disabled OIDC section is not an authorization server for this resource");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ResolveAuthorizationServers_WhenProviderIncomplete_IsEmpty()
    {
        var options = new OidcAuthenticationOptions
        {
            Enabled = true,
            Generic = new GenericOidcProviderOptions
            {
                Enabled = true,
                Authority = GenericAuthority
            }
        };

        McpProtectedResourceMetadata.ResolveAuthorizationServers(options).Should().BeEmpty(
            "an authority without a client id is not a valid provider");
    }

    private static DefaultHttpContext CreateHttpContext(bool oidcEnabled)
    {
        var oidcOptions = new OidcAuthenticationOptions
        {
            Enabled = oidcEnabled,
            Generic = new GenericOidcProviderOptions
            {
                Enabled = true,
                Authority = GenericAuthority,
                ClientId = "generic-client-id"
            }
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Public:BaseUrl"] = PublicBaseUrl })
            .Build();

        return new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<IConfiguration>(configuration)
                .AddSingleton<IOptions<OidcAuthenticationOptions>>(Options.Create(oidcOptions))
                .BuildServiceProvider()
        };
    }

    private static WebAppFixture CreateBaseFixture()
    {
        return new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", "test-admin-key");
                builder.UseSetting("Public:BaseUrl", PublicBaseUrl);
            });
    }

    private static WebAppFixture CreateOidcFixture()
    {
        return CreateBaseFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("Oidc:Enabled", "true");
                builder.UseSetting("Oidc:RequireHttps", "true");
                builder.UseSetting("Oidc:TokenValidation:ValidateIssuer", "false");
                builder.UseSetting("Oidc:TokenValidation:ValidateAudience", "false");
                builder.UseSetting("Oidc:TokenValidation:ValidateIssuerSigningKey", "false");
                builder.UseSetting("Oidc:TokenValidation:SymmetricSigningKey", "test-key-at-least-32-characters-long-for-testing");
                builder.UseSetting("Oidc:Generic:Enabled", "true");
                builder.UseSetting("Oidc:Generic:Authority", GenericAuthority);
                builder.UseSetting("Oidc:Generic:ClientId", "generic-client-id");
                builder.UseSetting("Oidc:Generic:DisplayName", "External Provider");
            });
    }
}
