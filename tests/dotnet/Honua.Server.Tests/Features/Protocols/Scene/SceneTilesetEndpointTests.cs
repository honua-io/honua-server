// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Protocols.Scene;

/// <summary>
/// Integration tests for the hosted 3D Tiles serving endpoints.
/// Verifies that <c>tileset.json</c>, binary tile content, and nested
/// tilesets are served with correct content types, ETags, and cache headers
/// against a public fixture tileset.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Scene)]
public sealed class SceneTilesetEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture;
    private readonly string _fixtureRoot;

    public SceneTilesetEndpointTests()
    {
        _fixtureRoot = SceneFixturePaths.ResolveFixtureRoot();

        _fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                // Public scene tests exercise anonymous behavior end-to-end:
                // output cache eligibility, browser/CDN cache headers, and the
                // Range bypass regression all depend on requests reaching the
                // server unauthenticated. WebAppFixture defaults to
                // HONUA_DEV_AUTH=true, which would otherwise short-circuit
                // AnonymousOnlyOutputCachePolicy and disable cache storage.
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        [$"Scenes:Datasets:0:Id"] = SceneFixturePaths.FixtureSceneId,
                        [$"Scenes:Datasets:0:Name"] = "Honua Fixture Tileset",
                        [$"Scenes:Datasets:0:Description"] = "Static 3D Tiles fixture used by tests",
                        [$"Scenes:Datasets:0:AssetRoot"] = _fixtureRoot
                    });
                });
            });
    }

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /scenes/{sceneId}/tileset.json")]
    public async Task GetTileset_ForPublicFixtureScene_ReturnsJsonWithETagAndCache()
    {
        var response = await _fixture.Client.GetAsync($"/scenes/{SceneFixturePaths.FixtureSceneId}/tileset.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        response.Headers.ETag.Should().NotBeNull();
        response.Headers.ETag!.Tag.Should().StartWith("\"").And.EndWith("\"");
        response.Headers.CacheControl?.Public.Should().BeTrue();
        response.Headers.CacheControl?.MaxAge.Should().BeGreaterThan(TimeSpan.Zero);
        response.Content.Headers.LastModified.Should().NotBeNull();

        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);
        var root = json.RootElement;
        root.GetProperty("asset").GetProperty("version").GetString().Should().Be("1.1");
        root.GetProperty("root").GetProperty("content").GetProperty("uri").GetString().Should().Be("tiles/0.b3dm");
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /scenes/{sceneId}/tileset.json")]
    public async Task GetTileset_RepeatedRequests_ReturnDeterministicETag()
    {
        var first = await _fixture.Client.GetAsync($"/scenes/{SceneFixturePaths.FixtureSceneId}/tileset.json");
        var second = await _fixture.Client.GetAsync($"/scenes/{SceneFixturePaths.FixtureSceneId}/tileset.json");

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        first.Headers.ETag.Should().NotBeNull();
        second.Headers.ETag.Should().NotBeNull();
        second.Headers.ETag!.Tag.Should().Be(first.Headers.ETag!.Tag);
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /scenes/{sceneId}/tileset.json")]
    public async Task GetTileset_WithMatchingIfNoneMatch_ReturnsNotModified()
    {
        var first = await _fixture.Client.GetAsync($"/scenes/{SceneFixturePaths.FixtureSceneId}/tileset.json");
        first.Headers.ETag.Should().NotBeNull();
        var etag = first.Headers.ETag!.Tag;

        using var conditional = new HttpRequestMessage(HttpMethod.Get, $"/scenes/{SceneFixturePaths.FixtureSceneId}/tileset.json");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var second = await _fixture.Client.SendAsync(conditional);

        second.StatusCode.Should().Be(HttpStatusCode.NotModified);
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("HEAD /scenes/{sceneId}/tileset.json")]
    public async Task HeadTileset_ReturnsHeadersWithoutBody()
    {
        using var headRequest = new HttpRequestMessage(HttpMethod.Head, $"/scenes/{SceneFixturePaths.FixtureSceneId}/tileset.json");
        var response = await _fixture.Client.SendAsync(headRequest);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.ETag.Should().NotBeNull();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var bodyBytes = await response.Content.ReadAsByteArrayAsync();
        bodyBytes.Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("HEAD /scenes/{sceneId}/{*assetPath}")]
    public async Task HeadSceneAsset_ReturnsHeadersWithoutBody()
    {
        using var headRequest = new HttpRequestMessage(HttpMethod.Head, $"/scenes/{SceneFixturePaths.FixtureSceneId}/tiles/0.b3dm");
        var response = await _fixture.Client.SendAsync(headRequest);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.ETag.Should().NotBeNull();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/octet-stream");
        response.Content.Headers.ContentLength.Should().BeGreaterThan(0);

        var bodyBytes = await response.Content.ReadAsByteArrayAsync();
        bodyBytes.Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /scenes/{sceneId}/{*assetPath}")]
    public async Task GetSceneAsset_BinaryB3dm_ReturnsOctetStreamWithMagicHeader()
    {
        var response = await _fixture.Client.GetAsync($"/scenes/{SceneFixturePaths.FixtureSceneId}/tiles/0.b3dm");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/octet-stream");
        response.Headers.ETag.Should().NotBeNull();
        response.Headers.AcceptRanges.Should().Contain("bytes");

        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Length.Should().BeGreaterThan(4);
        System.Text.Encoding.ASCII.GetString(bytes, 0, 4).Should().Be("b3dm");
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /scenes/{sceneId}/{*assetPath}")]
    public async Task GetSceneAsset_NestedTilesetJson_ReturnsApplicationJsonContent()
    {
        var response = await _fixture.Client.GetAsync($"/scenes/{SceneFixturePaths.FixtureSceneId}/nested/sub-tileset.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);
        json.RootElement.GetProperty("asset").GetProperty("version").GetString().Should().Be("1.1");
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /scenes/{sceneId}/{*assetPath}")]
    public async Task GetSceneAsset_PngTexture_ReturnsImagePngContent()
    {
        var response = await _fixture.Client.GetAsync($"/scenes/{SceneFixturePaths.FixtureSceneId}/tiles/texture.png");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /scenes/{sceneId}/{*assetPath}")]
    public async Task GetSceneAsset_TraversalAttempt_Returns400AndDoesNotEscapeAssetRoot()
    {
        // ASP.NET routing collapses literal `..` segments before the handler runs;
        // exercising encoded `..` confirms our resolver still rejects after URL decoding.
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/scenes/{SceneFixturePaths.FixtureSceneId}/..%2F..%2Fetc/passwd");
        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /scenes/{sceneId}/{*assetPath}")]
    public async Task GetSceneAsset_AbsolutePath_Returns400()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/scenes/{SceneFixturePaths.FixtureSceneId}/%2Fabsolute%2Ftiles%2F0.b3dm");
        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /scenes/{sceneId}/{*assetPath}")]
    public async Task GetSceneAsset_BackslashSeparator_Returns400()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/scenes/{SceneFixturePaths.FixtureSceneId}/tiles%5C0.b3dm");
        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /scenes/{sceneId}/{*assetPath}")]
    public async Task GetSceneAsset_UnknownAsset_Returns404()
    {
        var response = await _fixture.Client.GetAsync($"/scenes/{SceneFixturePaths.FixtureSceneId}/tiles/missing.b3dm");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /scenes/{sceneId}/tileset.json")]
    public async Task GetTileset_UnknownScene_Returns404()
    {
        var response = await _fixture.Client.GetAsync("/scenes/no-such-scene/tileset.json");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /scenes/{sceneId}/{*assetPath}")]
    public async Task GetSceneAsset_RangeRequestAfterFullGet_Returns206WithContentRange()
    {
        // Warm the output cache with a full anonymous GET (the fixture sets
        // HONUA_DEV_AUTH=false so AnonymousOnlyOutputCachePolicy lets the body
        // populate cache). Then issue a Range GET: BypassOutputCacheOnRangeRequestPolicy
        // must skip cache lookup so the static-file pipeline can return
        // 206 Partial Content with Content-Range instead of replaying the
        // previously cached 200 body.
        var assetUrl = $"/scenes/{SceneFixturePaths.FixtureSceneId}/tiles/0.b3dm";
        var fullResponse = await _fixture.Client.GetAsync(assetUrl);
        fullResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        // The warming GET must be cacheable end-to-end: public Cache-Control
        // is the only configuration under which AnonymousOnlyOutputCachePolicy
        // and the scene cache policy both allow storage.
        fullResponse.Headers.CacheControl?.Public.Should().BeTrue();
        fullResponse.Content.Headers.ContentLength.Should().NotBeNull();
        var totalLength = fullResponse.Content.Headers.ContentLength!.Value;
        totalLength.Should().BeGreaterThan(4);

        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, assetUrl);
        rangeRequest.Headers.Range = new RangeHeaderValue(0, 3);
        var rangeResponse = await _fixture.Client.SendAsync(rangeRequest);

        rangeResponse.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        rangeResponse.Content.Headers.ContentRange.Should().NotBeNull();
        rangeResponse.Content.Headers.ContentRange!.From.Should().Be(0);
        rangeResponse.Content.Headers.ContentRange.To.Should().Be(3);
        rangeResponse.Content.Headers.ContentRange.Length.Should().Be(totalLength);

        var rangeBytes = await rangeResponse.Content.ReadAsByteArrayAsync();
        rangeBytes.Length.Should().Be(4);
        System.Text.Encoding.ASCII.GetString(rangeBytes).Should().Be("b3dm");
    }

    [IntegrationTest]
    [Operation(Operations.ContentNegotiation)]
    [Endpoint("GET /scenes/{sceneId}/tileset.json")]
    public async Task GetTileset_FromBrowserOrigin_ExposesETagAndAcceptRanges()
    {
        // Mirrors the CORS preflight a browser CesiumJS instance would issue
        // for nested asset GETs. The shared CORS policy must keep ETag and
        // Accept-Ranges in the exposed-headers list so the tile cache and
        // range processing work in browsers.
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/scenes/{SceneFixturePaths.FixtureSceneId}/tileset.json");
        request.Headers.TryAddWithoutValidation("Origin", "http://localhost:3000");

        var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        if (response.Headers.TryGetValues("Access-Control-Expose-Headers", out var exposedValues))
        {
            var combined = string.Join(',', exposedValues);
            combined.Should().Contain("ETag");
            combined.Should().Contain("Accept-Ranges");
            combined.Should().Contain("Content-Length");
        }
    }
}
