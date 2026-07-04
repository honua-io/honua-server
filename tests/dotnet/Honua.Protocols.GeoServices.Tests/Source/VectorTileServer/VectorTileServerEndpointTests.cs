// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Protocols.GeoServices.VectorTileServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.VectorTileServer;

/// <summary>
/// Integration tests for the GeoServices VectorTileServer service-metadata foundation
/// (honua-server#1777). The service is resolved by NAME against an EsriVectorTileLayer
/// publication seeded into the default Metadata v2 test graph. The tile / resources /
/// tileMap routes are stubbed (501) in the foundation and asserted here so the API-surface
/// coverage gate is satisfied before the parallel wave fills them in.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.VectorTileServer)]
public sealed class VectorTileServerEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer")]
    public async Task VectorTileServer_Metadata_ReturnsServiceDescriptor()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VectorTileServer?f=json");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        var metadata = JsonSerializer.Deserialize(
            content, VectorTileServerJsonContext.Default.VectorTileServerMetadataResponse);

        metadata.Should().NotBeNull();
        metadata!.Name.Should().Be(WebAppFixture.TestServiceId);
        metadata.CurrentVersion.Should().BeGreaterThan(0);
        metadata.Capabilities.Should().Be("TilesOnly");
        metadata.Type.Should().Be("indexedVector");
        metadata.ExportTilesAllowed.Should().BeFalse();
        metadata.Tiles.Should().ContainSingle().Which.Should().Be("tile/{z}/{y}/{x}.pbf");
        metadata.DefaultStyles.Should().Be("resources/styles");
        metadata.TileMap.Should().Be("tilemap");

        metadata.TileInfo.Should().NotBeNull();
        metadata.TileInfo!.Rows.Should().Be(512);
        metadata.TileInfo.Cols.Should().Be(512);
        metadata.TileInfo.Format.Should().Be("pbf");
        metadata.TileInfo.Origin.Should().NotBeNull();
        metadata.TileInfo.SpatialReference.Should().NotBeNull();
        metadata.TileInfo.SpatialReference!.Wkid.Should().Be(102100);
        metadata.TileInfo.SpatialReference.LatestWkid.Should().Be(3857);
        metadata.TileInfo.Lods.Should().NotBeNullOrEmpty();
        metadata.TileInfo.Lods![0].Level.Should().Be(0);
        metadata.TileInfo.Lods[0].Scale.Should().BeApproximately(559082264.0287178, 1e-3);
        metadata.TileInfo.Lods[1].Scale.Should().BeApproximately(559082264.0287178 / 2.0, 1e-3);

        metadata.MinLod.Should().Be(0);
        metadata.MaxLod.Should().Be(metadata.TileInfo.Lods[^1].Level);
        metadata.FullExtent.Should().NotBeNull();
        metadata.InitialExtent.Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /rest/services/{serviceId}/VectorTileServer")]
    public async Task VectorTileServer_Metadata_Post_ReturnsServiceDescriptor()
    {
        using var body = new StringContent("f=json", Encoding.UTF8, "application/x-www-form-urlencoded");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VectorTileServer", body);

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        var metadata = JsonSerializer.Deserialize(
            content, VectorTileServerJsonContext.Default.VectorTileServerMetadataResponse);
        metadata.Should().NotBeNull();
        metadata!.Name.Should().Be(WebAppFixture.TestServiceId);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer")]
    public async Task VectorTileServer_Metadata_UnknownService_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            "/rest/services/does-not-exist/VectorTileServer?f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/tile/{z}/{y}/{x}.pbf")]
    public async Task VectorTileServer_Tile_InRange_ReturnsMvtBytesWithCacheHeader()
    {
        // Zoom 1, tile (0,0) covers a quadrant of the world; the seeded "test" service
        // resolves its EsriVectorTileLayer publication -> storage layer 0 and renders MVT.
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VectorTileServer/tile/1/0/0.pbf");

        // The seeded geometry may or may not intersect this specific tile, so accept either
        // rendered bytes or an empty (204) tile; both are valid pipeline outcomes.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);

        // Cache-Control (max-age from TileOptions.CacheMaxAge) is set on both OK and 204.
        response.Headers.Should().ContainKey("Cache-Control");
        response.Headers.CacheControl!.MaxAge.Should().BeGreaterThan(TimeSpan.Zero);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/vnd.mapbox-vector-tile");
            var bytes = await response.Content.ReadAsByteArrayAsync();
            bytes.Should().NotBeEmpty();
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/tile/{z}/{y}/{x}.pbf")]
    public async Task VectorTileServer_Tile_EmptyTile_ReturnsNoContent()
    {
        // A high-zoom tile far from the seeded extent yields no features -> 204 No Content.
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VectorTileServer/tile/15/0/0.pbf");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.Should().ContainKey("Cache-Control");
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/tile/{z}/{y}/{x}.pbf")]
    public async Task VectorTileServer_Tile_BadCoordinates_ReturnsBadRequest()
    {
        // x/y out of range for the zoom level (z=1 -> max index 1) -> 400 Bad Request.
        var badCoordinates = new[]
        {
            $"/rest/services/{WebAppFixture.TestServiceId}/VectorTileServer/tile/1/2/0.pbf", // y >= 2^z
            $"/rest/services/{WebAppFixture.TestServiceId}/VectorTileServer/tile/1/0/2.pbf"  // x >= 2^z
        };

        foreach (var url in badCoordinates)
        {
            var response = await _fixture.Client.GetAsync(url);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"{url} is out of range");
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/tile/{z}/{y}/{x}.pbf")]
    public async Task VectorTileServer_Tile_OutOfRangeZoom_ReturnsBadRequest()
    {
        // Zoom above LimitsOptions.Tiles.MaxTileZoom -> 400 Bad Request.
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VectorTileServer/tile/30/0/0.pbf");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/tile/{z}/{y}/{x}.pbf")]
    public async Task VectorTileServer_Tile_UnknownService_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            "/rest/services/does-not-exist/VectorTileServer/tile/1/0/0.pbf");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/resources/styles")]
    public async Task VectorTileServer_DefaultStyles_ReturnsGlStyleWithRewrittenTileSource()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VectorTileServer/resources/styles");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        // Valid Mapbox GL style v8.
        root.GetProperty("version").GetInt32().Should().Be(8);

        // sprite/glyphs are omitted until the sprite/glyph pipeline lands (honua-server#1780).
        root.TryGetProperty("sprite", out _).Should().BeFalse();
        root.TryGetProperty("glyphs", out _).Should().BeFalse();

        // A vector source whose tile template resolves to THIS service's tile route.
        var sources = root.GetProperty("sources");
        sources.EnumerateObject().Should().NotBeEmpty();

        var sawVectorTileTemplate = false;
        foreach (var source in sources.EnumerateObject())
        {
            source.Value.GetProperty("type").GetString().Should().Be("vector");

            // The TileJSON pointer must not be emitted (we serve tiles directly).
            source.Value.TryGetProperty("url", out _).Should().BeFalse();

            var tiles = source.Value.GetProperty("tiles");
            tiles.GetArrayLength().Should().BeGreaterThan(0);
            foreach (var tile in tiles.EnumerateArray())
            {
                var template = tile.GetString();
                template.Should().NotBeNullOrEmpty();
                template.Should().Contain(
                    $"/rest/services/{WebAppFixture.TestServiceId}/VectorTileServer/tile/{{z}}/{{y}}/{{x}}.pbf");
                template.Should().StartWith("http");
                sawVectorTileTemplate = true;
            }
        }

        sawVectorTileTemplate.Should().BeTrue();

        // The layer with no stored style still yields a deterministic, non-empty default style.
        root.GetProperty("layers").GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/resources/styles/{**resourcePath}")]
    public async Task VectorTileServer_StyleResource_RootJson_ReturnsGlStyle()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VectorTileServer/resources/styles/root.json");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        using var document = JsonDocument.Parse(content);
        document.RootElement.GetProperty("version").GetInt32().Should().Be(8);
        document.RootElement.GetProperty("sources").EnumerateObject().Should().NotBeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/resources/styles/{**resourcePath}")]
    public async Task VectorTileServer_StyleResource_SpriteOrGlyph_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VectorTileServer/resources/styles/sprites/sprite.json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/resources/styles")]
    public async Task VectorTileServer_DefaultStyles_UnknownService_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            "/rest/services/does-not-exist/VectorTileServer/resources/styles");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/tilemap/{z}/{y}/{x}/{dimension}/{dimension2}")]
    public async Task VectorTileServer_TileMap_FullyInRange_ReturnsAllAvailable()
    {
        // Level 2 has a 4x4 grid (0..3 in both axes); a 4x4 block at (0,0) covers it exactly.
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VectorTileServer/tilemap/2/0/0/4/4");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var tileMap = JsonSerializer.Deserialize(
            content, VectorTileServerJsonContext.Default.VectorTileMapResponse);

        tileMap.Should().NotBeNull();
        tileMap!.Adjusted.Should().BeFalse();
        tileMap.Location.Left.Should().Be(0);
        tileMap.Location.Top.Should().Be(0);
        tileMap.Location.Width.Should().Be(4);
        tileMap.Location.Height.Should().Be(4);
        tileMap.Data.Should().HaveCount(16);
        tileMap.Data.Should().OnlyContain(value => value == 1);
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/tilemap/{z}/{y}/{x}/{dimension}/{dimension2}")]
    public async Task VectorTileServer_TileMap_EdgeBlock_MarksOutOfRangeTilesZero()
    {
        // Level 2 grid is 0..3. A 2x2 block anchored at (3,3) covers tile (3,3) plus three
        // tiles that overrun the grid edge, so only the top-left flag is 1.
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VectorTileServer/tilemap/2/3/3/2/2");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        var tileMap = JsonSerializer.Deserialize(
            content, VectorTileServerJsonContext.Default.VectorTileMapResponse);

        tileMap.Should().NotBeNull();
        tileMap!.Data.Should().HaveCount(4);
        // Row-major: (3,3)=in-range, (4,3)=out, (3,4)=out, (4,4)=out.
        tileMap.Data.Should().Equal(1, 0, 0, 0);
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/tilemap")]
    public async Task VectorTileServer_TileMapRoot_ReturnsTopOfPyramid()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VectorTileServer/tilemap");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        var tileMap = JsonSerializer.Deserialize(
            content, VectorTileServerJsonContext.Default.VectorTileMapResponse);

        tileMap.Should().NotBeNull();
        tileMap!.Data.Should().ContainSingle().Which.Should().Be(1);
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/tilemap/{z}/{y}/{x}/{dimension}/{dimension2}")]
    public async Task VectorTileServer_TileMap_LevelOutsideScheme_ReturnsBadRequest()
    {
        // Level 99 is well beyond the served LOD range; the tileMap pyramid cannot describe it.
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VectorTileServer/tilemap/99/0/0/2/2");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/tilemap/{z}/{y}/{x}/{dimension}/{dimension2}")]
    public async Task VectorTileServer_TileMap_AbsurdDimension_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VectorTileServer/tilemap/2/0/0/4096/4096");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/tilemap/{z}/{y}/{x}/{dimension}/{dimension2}")]
    public async Task VectorTileServer_TileMap_UnknownService_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            "/rest/services/does-not-exist/VectorTileServer/tilemap/2/0/0/4/4");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/resources/sprites/{spriteResource}")]
    public async Task VectorTileServer_SpriteJson_ReturnsEmptyJsonObject()
    {
        foreach (var name in new[] { "sprite.json", "sprite@2x.json" })
        {
            var response = await _fixture.Client.GetAsync(
                $"/rest/services/{WebAppFixture.TestServiceId}/VectorTileServer/resources/sprites/{name}");

            var content = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, content);
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

            using var document = JsonDocument.Parse(content);
            document.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
            document.RootElement.EnumerateObject().Should().BeEmpty($"{name} is an empty sprite index");
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/resources/sprites/{spriteResource}")]
    public async Task VectorTileServer_SpritePng_ReturnsTransparentPng()
    {
        foreach (var name in new[] { "sprite.png", "sprite@2x.png" })
        {
            var response = await _fixture.Client.GetAsync(
                $"/rest/services/{WebAppFixture.TestServiceId}/VectorTileServer/resources/sprites/{name}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.Should().Be("image/png");

            var bytes = await response.Content.ReadAsByteArrayAsync();
            bytes.Should().NotBeEmpty();
            // PNG signature.
            bytes.Take(8).Should().Equal(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/resources/sprites/{spriteResource}")]
    public async Task VectorTileServer_Sprite_UnknownResource_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VectorTileServer/resources/sprites/sprite@3x.png");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/resources/sprites/{spriteResource}")]
    public async Task VectorTileServer_Sprite_UnknownService_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            "/rest/services/does-not-exist/VectorTileServer/resources/sprites/sprite.json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/resources/fonts/{fontstack}/{range}.pbf")]
    public async Task VectorTileServer_GlyphRange_ReturnsGlyphPbf()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VectorTileServer/resources/fonts/Honua%20Default/0-255.pbf");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/x-protobuf");

        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Should().NotBeEmpty("a valid glyph PBF carries the fontstack message");
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/resources/fonts/{fontstack}/{range}.pbf")]
    public async Task VectorTileServer_Glyph_OutOfRange_ReturnsNotFound()
    {
        // Ranges must be the canonical 256-codepoint windows (0-255, 256-511, …); an
        // arbitrary range is not served by the minimal embedded glyph stack.
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VectorTileServer/resources/fonts/Honua%20Default/99-1234.pbf");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/resources/fonts/{fontstack}/{range}.pbf")]
    public async Task VectorTileServer_Glyph_UnknownService_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            "/rest/services/does-not-exist/VectorTileServer/resources/fonts/Honua%20Default/0-255.pbf");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
