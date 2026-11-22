// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using SkiaSharp;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wmts;

[Collection("Database")]
[Protocol(TestProtocols.Wmts10)]
public sealed class WmtsRasterLayerTests : IAsyncLifetime
{
    private const int RasterStorageLayerId = 9201;
    private const int NonSpatialStorageLayerId = 9202;
    private const int RasterLayerIndex = 201;
    private const int NonSpatialLayerIndex = 202;
    private const string RasterLayerName = "wmts_ortho";
    private const string NonSpatialTableName = "wmts_lookup";

    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        await SeedPostGisRasterAsync(RasterStorageLayerId);
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Wmts)]
    [InterfaceOperation(TestProtocols.Wmts10, "GetCapabilities")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    public async Task GetCapabilities_ListsRasterLayer_AndOmitsNonSpatialTable()
    {
        var original = _fixture.GetCurrentV2GraphSnapshot().Graph;
        var provider = (TestMetadataV2GraphProvider)_fixture.GetService<IMetadataV2GraphProvider>();
        provider.SetGraph(WithRasterAndNonSpatialPublications(original, spatial: null), schema: _fixture.CurrentSchema);
        try
        {
            using var response = await _fixture.Client.GetAsync(
                $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetCapabilities");
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, body);

            var doc = XDocument.Parse(body);
            XNamespace wmts = "http://www.opengis.net/wmts/1.0";
            XNamespace ows = "http://www.opengis.net/ows/1.1";

            var layers = doc.Descendants(wmts + "Contents").Elements(wmts + "Layer")
                .ToDictionary(el => el.Element(ows + "Identifier")!.Value, el => el, StringComparer.Ordinal);

            var expectedId = RasterLayerIndex.ToString(CultureInfo.InvariantCulture);
            var nonSpatialId = NonSpatialLayerIndex.ToString(CultureInfo.InvariantCulture);

            layers.Should().ContainKey(expectedId);
            layers.Should().NotContainKey(nonSpatialId);

            var rasterLayer = layers[expectedId];
            rasterLayer.Element(ows + "WGS84BoundingBox").Should().NotBeNull();
            rasterLayer.Elements(wmts + "InfoFormat").Should().BeEmpty();
            rasterLayer.Descendants(wmts + "LegendURL").Should().BeEmpty();
            rasterLayer.Elements(wmts + "ResourceURL")
                .Where(el => el.Attribute("resourceType")?.Value == "tile")
                .Should().ContainSingle();
        }
        finally
        {
            provider.SetGraph(original, schema: _fixture.CurrentSchema);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Wmts)]
    [InterfaceOperation(TestProtocols.Wmts10, "GetTile")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    public async Task GetTile_RasterLayer_ReturnsDecodablePngForBothBuiltInTileMatrixSets()
    {
        var original = _fixture.GetCurrentV2GraphSnapshot().Graph;
        var provider = (TestMetadataV2GraphProvider)_fixture.GetService<IMetadataV2GraphProvider>();
        provider.SetGraph(WithRasterAndNonSpatialPublications(original, DefaultRasterSpatial()), schema: _fixture.CurrentSchema);
        try
        {
            using var mercatorResp = await _fixture.Client.GetAsync(
                $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0" +
                $"&LAYER={RasterLayerIndex}&STYLE=default&FORMAT=image/png&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=0&TILEROW=0&TILECOL=0");
            mercatorResp.StatusCode.Should().Be(HttpStatusCode.OK, await mercatorResp.Content.ReadAsStringAsync());
            mercatorResp.Content.Headers.ContentType?.MediaType.Should().Be("image/png");

            using var mercatorBitmap = SKBitmap.Decode(await mercatorResp.Content.ReadAsByteArrayAsync());
            mercatorBitmap.Should().NotBeNull();
            mercatorBitmap!.Width.Should().Be(256);
            mercatorBitmap.Height.Should().Be(256);
            HasOpaquePixels(mercatorBitmap).Should().BeTrue();

            using var crs84Resp = await _fixture.Client.GetAsync(
                $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0" +
                $"&LAYER={RasterLayerIndex}&STYLE=default&FORMAT=image/png&TILEMATRIXSET=WorldCRS84Quad&TILEMATRIX=0&TILEROW=0&TILECOL=0");
            crs84Resp.StatusCode.Should().Be(HttpStatusCode.OK, await crs84Resp.Content.ReadAsStringAsync());
            crs84Resp.Content.Headers.ContentType?.MediaType.Should().Be("image/png");

            using var crs84Bitmap = SKBitmap.Decode(await crs84Resp.Content.ReadAsByteArrayAsync());
            crs84Bitmap.Should().NotBeNull();
            crs84Bitmap!.Width.Should().Be(256);
            crs84Bitmap.Height.Should().Be(256);
            HasOpaquePixels(crs84Bitmap).Should().BeTrue();
        }
        finally
        {
            provider.SetGraph(original, schema: _fixture.CurrentSchema);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Wmts)]
    [InterfaceOperation(TestProtocols.Wmts10, "GetTile")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    public async Task GetTile_RejectsNonSpatialTable_AndReturnsTransparentPngOutsideRasterExtent()
    {
        var original = _fixture.GetCurrentV2GraphSnapshot().Graph;
        var provider = (TestMetadataV2GraphProvider)_fixture.GetService<IMetadataV2GraphProvider>();
        provider.SetGraph(WithRasterAndNonSpatialPublications(original, DefaultRasterSpatial()), schema: _fixture.CurrentSchema);
        try
        {
            using var nonSpatialResp = await _fixture.Client.GetAsync(
                $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0" +
                $"&LAYER={NonSpatialLayerIndex}&STYLE=default&FORMAT=image/png&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=0&TILEROW=0&TILECOL=0");
            nonSpatialResp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await nonSpatialResp.Content.ReadAsStringAsync()).Should().Contain("InvalidParameterValue");

            // Eastern hemisphere tile (col=1 at z=0 in WorldCRS84Quad) has no raster coverage.
            using var emptyTileResp = await _fixture.Client.GetAsync(
                $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0" +
                $"&LAYER={RasterLayerIndex}&STYLE=default&FORMAT=image/png&TILEMATRIXSET=WorldCRS84Quad&TILEMATRIX=0&TILEROW=0&TILECOL=1");
            emptyTileResp.StatusCode.Should().Be(HttpStatusCode.OK, await emptyTileResp.Content.ReadAsStringAsync());
            using var emptyBitmap = SKBitmap.Decode(await emptyTileResp.Content.ReadAsByteArrayAsync());
            emptyBitmap.Should().NotBeNull();
            HasOpaquePixels(emptyBitmap!).Should().BeFalse();

            using var featureInfoResp = await _fixture.Client.GetAsync(
                $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetFeatureInfo&VERSION=1.0.0" +
                $"&LAYER={RasterLayerIndex}&STYLE=default&FORMAT=image/png&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=0&TILEROW=0&TILECOL=0&I=10&J=10&INFOFORMAT=application/json");
            featureInfoResp.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
        }
        finally
        {
            provider.SetGraph(original, schema: _fixture.CurrentSchema);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Wmts)]
    [InterfaceOperation(TestProtocols.Wmts10, "GetTile")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    public async Task GetTile_CogRasterLayer_FallsBackToCogTileResolver()
    {
        var cogPng = CreateSolidPng(256, 256, new SKColor(30, 180, 90, 255));
        var cogResolver = CreateCogResolverStub(cogPng);
        var fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<ICogTileResolver>();
                services.AddSingleton(cogResolver);
            });

        try
        {
            await fixture.InitializeAsync();
            var original = fixture.GetCurrentV2GraphSnapshot().Graph;
            var provider = (TestMetadataV2GraphProvider)fixture.GetService<IMetadataV2GraphProvider>();
            provider.SetGraph(WithRasterAndNonSpatialPublications(original, DefaultRasterSpatial(), rasterStorageLayerId: 9299), schema: fixture.CurrentSchema);

            using var response = await fixture.Client.GetAsync(
                $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0" +
                $"&LAYER={RasterLayerIndex}&STYLE=default&FORMAT=image/png&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=0&TILEROW=0&TILECOL=0");
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            (await response.Content.ReadAsByteArrayAsync()).Should().Equal(cogPng);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static ICogTileResolver CreateCogResolverStub(byte[] cogPng)
    {
        var cogResolver = Substitute.For<ICogTileResolver>();
        cogResolver.GetTileForLayerAsync(RasterLayerIndex, 0, 0, 0, RasterFormat.PNG, Arg.Any<CancellationToken>())
            .Returns(new CogTileLookup(
                new RasterResult { Data = cogPng, ContentType = "image/png", Width = 256, Height = 256, Srid = 3857 },
                EditionGateHit: false));
        return cogResolver;
    }

    private async Task SeedPostGisRasterAsync(int layerId)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO honua.layers (layer_id, layer_name, table_name, geometry_type)
            VALUES (@layerId, 'wmts_raster_' || @layerId, 'raster_data', 'NONE')
            ON CONFLICT (layer_id) DO NOTHING;
            DELETE FROM honua.raster_data WHERE layer_id = @layerId;
            INSERT INTO honua.raster_data (layer_id, name, raster, acquisition_date, created_at)
            SELECT @layerId, 'wmts-raster-test',
                ST_AddBand(
                    ST_AddBand(
                        ST_AddBand(
                            ST_MakeEmptyRaster(8, 8, -130, 50, 2.0, -2.0, 0, 0, 4326),
                            '8BUI'::text, 40, 0),
                        '8BUI'::text, 190, 0),
                    '8BUI'::text, 220, 0),
                NOW(), NOW();
            """;
        command.Parameters.AddWithValue("layerId", layerId);
        await command.ExecuteNonQueryAsync();
    }

    private static bool HasOpaquePixels(SKBitmap bitmap)
    {
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha > 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static byte[] CreateSolidPng(int width, int height, SKColor color)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface!.Canvas.Clear(color);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static MetadataV2ObjectSpatial DefaultRasterSpatial() => new()
    {
        SpatialReference = new MetadataV2SpatialReference { Srid = 4326, Crs = "EPSG:4326", IsGeographic = true },
        Bbox = new MetadataV2Bbox { West = -130.0, South = 34.0, East = -114.0, North = 50.0 }
    };

    private static MetadataV2Graph WithRasterAndNonSpatialPublications(
        MetadataV2Graph original,
        MetadataV2ObjectSpatial? spatial,
        int rasterStorageLayerId = RasterStorageLayerId)
    {
        var service = original.Services.First(s => s.Metadata.Name == WebAppFixture.TestServiceId);
        return original with
        {
            Revision = original.Revision + 1,
            Resources = [.. original.Resources, .. BuildTestResources(spatial)],
            StorageBindings = [.. original.StorageBindings, .. BuildTestBindings(rasterStorageLayerId)],
            Publications = [.. original.Publications, .. BuildTestPublications(service.Metadata.Id)]
        };
    }

    private static MetadataV2Object[] BuildTestResources(MetadataV2ObjectSpatial? spatial) =>
    [
        new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-wmts-raster", Name = RasterLayerName, Title = "WMTS Ortho" },
            Type = MetadataV2ObjectType.RasterDataset,
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
            Spatial = spatial,
            StorageBindingIds = ["bind-wmts-raster"]
        },
        new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-wmts-nonspatial", Name = NonSpatialTableName, Title = "WMTS Lookup" },
            Type = MetadataV2ObjectType.Table,
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
            SchemaFields = [new MetadataV2Field { Name = "code", Type = MetadataV2FieldType.String }],
            StorageBindingIds = ["bind-wmts-nonspatial"]
        }
    ];

    private static MetadataV2StorageBinding[] BuildTestBindings(int rasterStorageLayerId) =>
    [
        new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "bind-wmts-raster", Name = "bind-wmts-raster" },
            ResourceId = "res-wmts-raster",
            ConnectionId = "conn-default",
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
            StorageType = MetadataV2StorageType.RelationalTable,
            StorageLayerId = rasterStorageLayerId
        },
        new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "bind-wmts-nonspatial", Name = "bind-wmts-nonspatial" },
            ResourceId = "res-wmts-nonspatial",
            ConnectionId = "conn-default",
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
            StorageType = MetadataV2StorageType.RelationalTable,
            StorageLayerId = NonSpatialStorageLayerId
        }
    ];

    private static MetadataV2Layer[] BuildTestPublications(string serviceId) =>
    [
        new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "pub-wmts-raster", Name = RasterLayerName },
            ServiceId = serviceId,
            ResourceId = "res-wmts-raster",
            ServiceLocalId = RasterLayerName,
            LayerIndex = RasterLayerIndex,
            IsPrimary = true
        },
        new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "pub-wmts-nonspatial", Name = NonSpatialTableName },
            ServiceId = serviceId,
            ResourceId = "res-wmts-nonspatial",
            ServiceLocalId = NonSpatialTableName,
            LayerIndex = NonSpatialLayerIndex,
            IsPrimary = true
        }
    ];
}
