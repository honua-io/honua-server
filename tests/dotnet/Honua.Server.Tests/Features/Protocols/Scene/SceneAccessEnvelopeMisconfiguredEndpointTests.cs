// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Protocols.Scene;

/// <summary>
/// Integration coverage for the misconfigured-signing-key path. A deployment
/// that registers a protected scene without setting
/// <c>Honua:SceneAccessSigning:SigningKey</c> must surface a structured 500
/// from both the issue endpoint and the token-verification path on asset
/// requests, rather than an unhandled exception during DI parameter binding.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Scene)]
public sealed class SceneAccessEnvelopeMisconfiguredEndpointTests : IAsyncLifetime
{
    private const string AdminPassword = "scene-envelope-misconfig-test-key";
    private readonly WebAppFixture _fixture;
    private HttpClient _authenticatedClient = null!;

    public SceneAccessEnvelopeMisconfiguredEndpointTests()
    {
        var fixtureRoot = SceneFixturePaths.ResolveFixtureRoot();

        _fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        // Protected scene — requires authenticated principal.
                        [$"Scenes:Datasets:0:Id"] = SceneFixturePaths.ProtectedSceneId,
                        [$"Scenes:Datasets:0:Name"] = "Protected Fixture",
                        [$"Scenes:Datasets:0:AssetRoot"] = fixtureRoot,
                        [$"Scenes:Datasets:0:AccessPolicy:AllowAnonymous"] = "false",

                        // Intentionally NO Honua:SceneAccessSigning:SigningKey.
                        // The signing service constructor will throw
                        // InvalidOperationException on first resolve and the
                        // endpoints must catch and surface 500 + log.
                    });
                });
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _authenticatedClient = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("POST /scenes/{sceneId}/access-envelope")]
    public async Task IssueEnvelope_MissingSigningKey_Returns500()
    {
        // Authenticated principal passes the access-policy gate and reaches
        // the signing-service resolve, which throws because SigningKey is
        // unset. Expect a structured 500, not an unhandled exception.
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/scenes/{SceneFixturePaths.ProtectedSceneId}/access-envelope");
        var response = await _authenticatedClient.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        // The internal exception message must not surface to the client.
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("Honua:SceneAccessSigning:SigningKey");
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /scenes/{sceneId}/{*assetPath}")]
    public async Task GetSceneAsset_MissingSigningKey_WithToken_Returns500()
    {
        // Anonymous request carrying a stray token: the bearer-auth gate
        // fails, the asset handler attempts to verify the token and tries
        // to resolve the envelope service, which throws because SigningKey
        // is unset. Expect a structured 500.
        var response = await _fixture.Client.GetAsync(
            $"/scenes/{SceneFixturePaths.ProtectedSceneId}/tiles/0.b3dm?token=any-stray-token-value");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /scenes/{sceneId}/{*assetPath}")]
    public async Task GetSceneAsset_MissingSigningKey_NoToken_Returns401()
    {
        // No token transport means the verification path is never entered,
        // so the misconfiguration is invisible — anonymous requests fall
        // through to the standard 401 access-denied result.
        var response = await _fixture.Client.GetAsync(
            $"/scenes/{SceneFixturePaths.ProtectedSceneId}/tiles/0.b3dm");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /scenes/{sceneId}/{*assetPath}")]
    public async Task GetSceneAsset_MissingSigningKey_BearerAuthBypassesVerification()
    {
        // Bearer/API-key clients never reach the token-verification branch
        // because the access policy already allows them. The envelope
        // service is therefore never resolved, so the misconfiguration is
        // not user-visible for native clients with valid credentials.
        var response = await _authenticatedClient.GetAsync(
            $"/scenes/{SceneFixturePaths.ProtectedSceneId}/tiles/0.b3dm");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
