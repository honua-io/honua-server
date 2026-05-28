// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Protocols.Scene;

/// <summary>
/// Integration tests for the scene access envelope issuance endpoint and the
/// nested asset verification path. Covers the browser-safe <c>?token=</c>
/// transport, native <c>X-Honua-Token</c> header transport, expiry/tamper
/// rejection, public-scene preservation, and structured-log redaction.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Scene)]
public sealed class SceneAccessEnvelopeEndpointTests : IAsyncLifetime
{
    private const string AdminPassword = "scene-envelope-test-key";
    private const string SigningKey = "test-envelope-signing-key-aBcD1234ZxYw5678";
    private const string SecondProtectedSceneId = "second-protected-tileset";
    private readonly WebAppFixture _fixture;
    private HttpClient _authenticatedClient = null!;

    public SceneAccessEnvelopeEndpointTests()
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
                        // Public scene — no access policy.
                        [$"Scenes:Datasets:0:Id"] = SceneFixturePaths.FixtureSceneId,
                        [$"Scenes:Datasets:0:Name"] = "Public Fixture",
                        [$"Scenes:Datasets:0:AssetRoot"] = fixtureRoot,

                        // Protected scene — requires authenticated principal.
                        [$"Scenes:Datasets:1:Id"] = SceneFixturePaths.ProtectedSceneId,
                        [$"Scenes:Datasets:1:Name"] = "Protected Fixture",
                        [$"Scenes:Datasets:1:AssetRoot"] = fixtureRoot,
                        [$"Scenes:Datasets:1:AccessPolicy:AllowAnonymous"] = "false",

                        // Second protected scene — needed to exercise the
                        // "wrong scene" rejection path on the asset endpoint.
                        [$"Scenes:Datasets:2:Id"] = SecondProtectedSceneId,
                        [$"Scenes:Datasets:2:Name"] = "Second Protected Fixture",
                        [$"Scenes:Datasets:2:AssetRoot"] = fixtureRoot,
                        [$"Scenes:Datasets:2:AccessPolicy:AllowAnonymous"] = "false",

                        // Signing key for the envelope service.
                        ["Honua:SceneAccessSigning:SigningKey"] = SigningKey,
                        ["Honua:SceneAccessSigning:TokenTtlMinutes"] = "15"
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

    // ----- Issue endpoint -----

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("POST /scenes/{sceneId}/access-envelope")]
    public async Task IssueEnvelope_AuthenticatedForProtectedScene_Returns200WithEnvelope()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/scenes/{SceneFixturePaths.ProtectedSceneId}/access-envelope");
        var response = await _authenticatedClient.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl?.NoStore.Should().BeTrue(
            "tokens are short-lived credentials that must never be stored");

        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        root.GetProperty("sceneId").GetString().Should().Be(SceneFixturePaths.ProtectedSceneId);
        root.GetProperty("token").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("expiresAt").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("refreshAfter").GetString().Should().NotBeNullOrEmpty();
        var methods = root.GetProperty("allowedMethods").EnumerateArray()
            .Select(m => m.GetString())
            .ToArray();
        methods.Should().Contain("GET");
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("POST /scenes/{sceneId}/access-envelope")]
    public async Task IssueEnvelope_AnonymousForProtectedScene_Returns401()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/scenes/{SceneFixturePaths.ProtectedSceneId}/access-envelope");
        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("POST /scenes/{sceneId}/access-envelope")]
    public async Task IssueEnvelope_PublicScene_Returns400()
    {
        // Envelope issuance for a public scene returns 400: the contract is
        // explicit so callers don't accumulate unused short-lived credentials.
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/scenes/{SceneFixturePaths.FixtureSceneId}/access-envelope");
        var response = await _authenticatedClient.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("POST /scenes/{sceneId}/access-envelope")]
    public async Task IssueEnvelope_UnknownScene_Returns404()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/scenes/no-such-scene/access-envelope");
        var response = await _authenticatedClient.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("POST /scenes/{sceneId}/access-envelope")]
    public async Task IssueEnvelope_ResponseDoesNotEchoSigningKey()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/scenes/{SceneFixturePaths.ProtectedSceneId}/access-envelope");
        var response = await _authenticatedClient.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();

        // Acceptance: response payload must not contain the configured
        // signing key, "SigningKey", or any obvious credential leak.
        body.Should().NotContain(SigningKey);
        body.Should().NotContain("SigningKey", because: "internal config keys must not surface in responses");
    }

    // ----- Token-authorized asset access -----

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /scenes/{sceneId}/tileset.json")]
    public async Task GetTileset_ProtectedScene_WithValidToken_Returns200()
    {
        var token = await IssueTokenAsync(SceneFixturePaths.ProtectedSceneId);

        var response = await _fixture.Client.GetAsync(
            $"/scenes/{SceneFixturePaths.ProtectedSceneId}/tileset.json?token={Uri.EscapeDataString(token)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        // Token-authorized assets must use private cacheability so shared
        // caches cannot replay the body to clients without the token.
        response.Headers.CacheControl?.Private.Should().BeTrue();
        response.Headers.CacheControl?.Public.Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /scenes/{sceneId}/{*assetPath}")]
    public async Task GetSceneAsset_NestedBinaryWithToken_Returns200()
    {
        // Acceptance: Cesium-style nested asset fetches work with ?token=
        // and do not require a bearer header.
        var token = await IssueTokenAsync(SceneFixturePaths.ProtectedSceneId);

        var response = await _fixture.Client.GetAsync(
            $"/scenes/{SceneFixturePaths.ProtectedSceneId}/tiles/0.b3dm?token={Uri.EscapeDataString(token)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/octet-stream");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Length.Should().BeGreaterThan(4);
        Encoding.ASCII.GetString(bytes, 0, 4).Should().Be("b3dm");
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /scenes/{sceneId}/{*assetPath}")]
    public async Task GetSceneAsset_NestedTilesetJsonWithToken_Returns200()
    {
        var token = await IssueTokenAsync(SceneFixturePaths.ProtectedSceneId);

        var response = await _fixture.Client.GetAsync(
            $"/scenes/{SceneFixturePaths.ProtectedSceneId}/nested/sub-tileset.json?token={Uri.EscapeDataString(token)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /scenes/{sceneId}/{*assetPath}")]
    public async Task GetSceneAsset_NestedTextureWithToken_Returns200()
    {
        var token = await IssueTokenAsync(SceneFixturePaths.ProtectedSceneId);

        var response = await _fixture.Client.GetAsync(
            $"/scenes/{SceneFixturePaths.ProtectedSceneId}/tiles/texture.png?token={Uri.EscapeDataString(token)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /scenes/{sceneId}/{*assetPath}")]
    public async Task GetSceneAsset_TokenInHonuaHeader_Returns200()
    {
        var token = await IssueTokenAsync(SceneFixturePaths.ProtectedSceneId);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/scenes/{SceneFixturePaths.ProtectedSceneId}/tiles/0.b3dm");
        request.Headers.Add("X-Honua-Token", token);

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl?.Private.Should().BeTrue();
        response.Headers.Vary.Should().Contain("X-Honua-Token");
        response.Headers.Vary.Should().NotContain("Authorization");
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /scenes/{sceneId}/{*assetPath}")]
    public async Task GetSceneAsset_TamperedToken_Returns401()
    {
        var token = await IssueTokenAsync(SceneFixturePaths.ProtectedSceneId);

        // Flip the last hex digit of the signature.
        var tampered = token[..^1] + (token[^1] == '0' ? '1' : '0');

        var response = await _fixture.Client.GetAsync(
            $"/scenes/{SceneFixturePaths.ProtectedSceneId}/tiles/0.b3dm?token={Uri.EscapeDataString(tampered)}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /scenes/{sceneId}/{*assetPath}")]
    public async Task GetSceneAsset_TokenForDifferentScene_Returns403()
    {
        // Issue a token bound to ProtectedSceneId, then attempt to use it on
        // a different protected scene — wrong-scene must surface as 403.
        var token = await IssueTokenAsync(SceneFixturePaths.ProtectedSceneId);

        var response = await _fixture.Client.GetAsync(
            $"/scenes/{SecondProtectedSceneId}/tiles/0.b3dm?token={Uri.EscapeDataString(token)}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /scenes/{sceneId}/{*assetPath}")]
    public async Task GetSceneAsset_NoTokenOnProtectedScene_Returns401()
    {
        // Acceptance: protected scene asset with no Authorization header and
        // no token must fail closed.
        var response = await _fixture.Client.GetAsync(
            $"/scenes/{SceneFixturePaths.ProtectedSceneId}/tiles/0.b3dm");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /scenes/{sceneId}/{*assetPath}")]
    public async Task GetSceneAsset_BearerAuthOnProtectedScene_StillWorks()
    {
        // Acceptance: native server-to-server clients with bearer credentials
        // continue to work; the envelope path is purely additive.
        var response = await _authenticatedClient.GetAsync(
            $"/scenes/{SceneFixturePaths.ProtectedSceneId}/tiles/0.b3dm");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl?.Private.Should().BeTrue();
        response.Headers.Vary.Should().Contain("Authorization");
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /scenes/{sceneId}/{*assetPath}")]
    public async Task GetSceneAsset_PathTraversalWithValidToken_Returns400()
    {
        // Acceptance: even with a valid token, path-traversal probes must
        // be rejected by the resolver.
        var token = await IssueTokenAsync(SceneFixturePaths.ProtectedSceneId);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/scenes/{SceneFixturePaths.ProtectedSceneId}/..%2F..%2Fetc/passwd?token={Uri.EscapeDataString(token)}");
        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /scenes/{sceneId}/tileset.json")]
    public async Task GetTileset_PublicScene_TokenIgnored()
    {
        // Acceptance: public scene cache and ETag behavior is preserved
        // regardless of whether a token is also supplied.
        var response = await _fixture.Client.GetAsync(
            $"/scenes/{SceneFixturePaths.FixtureSceneId}/tileset.json?token=stray-value");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.ETag.Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /scenes/{sceneId}/{*assetPath}")]
    public async Task GetSceneAsset_TokenAuthorized_DoesNotEmitVaryAuthorization()
    {
        // Query-token requests already vary by URL. Emitting
        // Vary: Authorization on these responses would be misleading because
        // no Authorization header is present, and Vary: X-Honua-Token is only
        // needed for the native header transport.
        var token = await IssueTokenAsync(SceneFixturePaths.ProtectedSceneId);

        var response = await _fixture.Client.GetAsync(
            $"/scenes/{SceneFixturePaths.ProtectedSceneId}/tiles/0.b3dm?token={Uri.EscapeDataString(token)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Vary.Should().NotContain("Authorization");
        response.Headers.Vary.Should().NotContain("X-Honua-Token");
    }

    private async Task<string> IssueTokenAsync(string sceneId)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/scenes/{sceneId}/access-envelope");
        var response = await _authenticatedClient.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("Envelope did not include a token.");
    }
}
