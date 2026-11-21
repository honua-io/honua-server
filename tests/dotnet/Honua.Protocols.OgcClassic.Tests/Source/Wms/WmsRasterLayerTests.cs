// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using SkiaSharp;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wms;

[Collection("Database")]
[Protocol(TestProtocols.Wms13)]
public sealed class WmsRasterLayerTests : IAsyncLifetime
{
    private const int RasterStorageLayerId = 9101;
    private const int NonSpatialStorageLayerId = 9102;
    private const int ProtectedRasterStorageLayerId = 9103;
    private const string RasterLayerName = "ortho_imagery";
    private const string NonSpatialTableName = "lookup_table";
    private const string ProtectedRasterLayerName = "private_imagery";

    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        await SeedPostGisRasterAsync(RasterStorageLayerId);
        await SeedPostGisRasterAsync(ProtectedRasterStorageLayerId);
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetCapabilities")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task GetCapabilities_ListsRasterLayer_AndOmitsNonSpatialTable()
    {
        var original = _fixture.GetCurrentV2GraphSnapshot().Graph;
        var provider = (TestMetadataV2GraphProvider)_fixture.GetService<IMetadataV2GraphProvider>();
        provider.SetGraph(WithRasterAndNonSpatialPublications(original, spatial: null), schema: _fixture.CurrentSchema);
        try
        {
            using var response = await _fixture.Client.GetAsync(
                $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetCapabilities&VERSION=1.3.0");
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, body);

            var doc = XDocument.Parse(body);
            XNamespace wms = "http://www.opengis.net/wms";
            var namedLayers = doc.Descendants(wms + "Layer")
                .Where(el => el.Element(wms + "Name") is not null)
                .ToDictionary(el => el.Element(wms + "Name")!.Value, el => el, StringComparer.Ordinal);

            namedLayers.Should().ContainKey(RasterLayerName);
            namedLayers.Should().NotContainKey(NonSpatialTableName);

            var rasterLayer = namedLayers[RasterLayerName];
            rasterLayer.Attribute("queryable")?.Value.Should().Be("0");
            rasterLayer.Descendants(wms + "LegendURL").Should().BeEmpty();

            var geoBox = rasterLayer.Element(wms + "EX_GeographicBoundingBox");
            geoBox.Should().NotBeNull();
            double.Parse(geoBox!.Element(wms + "westBoundLongitude")!.Value, CultureInfo.InvariantCulture)
                .Should().BeApproximately(-122.5, 0.01);
            double.Parse(geoBox.Element(wms + "eastBoundLongitude")!.Value, CultureInfo.InvariantCulture)
                .Should().BeApproximately(-122.3, 0.01);
        }
        finally
        {
            provider.SetGraph(original, schema: _fixture.CurrentSchema);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetCapabilities")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task GetCapabilities_WithAuthEnforced_HidesProtectedRasterLayer()
    {
        var authFixture = new WebAppFixture()
            .ConfigureWebHost(builder => builder
                .UseSetting("HONUA_DEV_AUTH", "false")
                .UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false"));
        try
        {
            await authFixture.InitializeAsync();
            var original = authFixture.GetCurrentV2GraphSnapshot().Graph;
            var provider = (TestMetadataV2GraphProvider)authFixture.GetService<IMetadataV2GraphProvider>();
            provider.SetGraph(WithRasterAndNonSpatialPublications(original, DefaultRasterSpatial()), schema: authFixture.CurrentSchema);

            using var response = await authFixture.Client.GetAsync(
                $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetCapabilities&VERSION=1.3.0");
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, body);

            var doc = XDocument.Parse(body);
            XNamespace wms = "http://www.opengis.net/wms";
            var layerNames = doc.Descendants(wms + "Layer")
                .Select(el => el.Element(wms + "Name")?.Value)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToArray();

            layerNames.Should().Contain(RasterLayerName);
            layerNames.Should().NotContain(ProtectedRasterLayerName);
        }
        finally
        {
            await authFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task GetMap_RasterLayer_ReturnsDecodableImageWithRasterPixels()
    {
        var original = _fixture.GetCurrentV2GraphSnapshot().Graph;
        var provider = (TestMetadataV2GraphProvider)_fixture.GetService<IMetadataV2GraphProvider>();
        provider.SetGraph(WithRasterAndNonSpatialPublications(original, DefaultRasterSpatial()), schema: _fixture.CurrentSchema);
        try
        {
            using var response = await _fixture.Client.GetAsync(
                $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap" +
                $"&LAYERS={RasterLayerName}&STYLES=&CRS=EPSG:4326&BBOX=37.7,-122.5,37.9,-122.3&WIDTH=64&HEIGHT=64&FORMAT=image/png&TRANSPARENT=TRUE");
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");

            using var bitmap = SKBitmap.Decode(await response.Content.ReadAsByteArrayAsync());
            bitmap.Should().NotBeNull();
            bitmap!.Width.Should().Be(64);
            bitmap.Height.Should().Be(64);

            var centerPixel = bitmap.GetPixel(32, 32);
            centerPixel.Alpha.Should().BeGreaterThan(200);
            centerPixel.Red.Should().BeGreaterThan(180);
            centerPixel.Green.Should().BeLessThan(80);
            centerPixel.Blue.Should().BeGreaterThan(100);
        }
        finally
        {
            provider.SetGraph(original, schema: _fixture.CurrentSchema);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task GetMap_VectorLayer_IsUnchangedWhenRasterLayerExistsInService()
    {
        var query = $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap" +
            "&LAYERS=0&STYLES=&CRS=EPSG:4326&BBOX=-90,-180,90,180&WIDTH=64&HEIGHT=64&FORMAT=image/png&TRANSPARENT=TRUE";

        using var beforeResponse = await _fixture.Client.GetAsync(query);
        beforeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var beforeBytes = await beforeResponse.Content.ReadAsByteArrayAsync();

        var original = _fixture.GetCurrentV2GraphSnapshot().Graph;
        var provider = (TestMetadataV2GraphProvider)_fixture.GetService<IMetadataV2GraphProvider>();
        provider.SetGraph(WithRasterAndNonSpatialPublications(original, DefaultRasterSpatial()), schema: _fixture.CurrentSchema);
        try
        {
            using var afterResponse = await _fixture.Client.GetAsync(query);
            afterResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            (await afterResponse.Content.ReadAsByteArrayAsync()).Should().Equal(beforeBytes);
        }
        finally
        {
            provider.SetGraph(original, schema: _fixture.CurrentSchema);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task GetMap_AndAuxiliaryOps_RejectNonSpatialRasterFilterAndNonQueryableRequests()
    {
        var original = _fixture.GetCurrentV2GraphSnapshot().Graph;
        var provider = (TestMetadataV2GraphProvider)_fixture.GetService<IMetadataV2GraphProvider>();
        provider.SetGraph(WithRasterAndNonSpatialPublications(original, DefaultRasterSpatial()), schema: _fixture.CurrentSchema);
        try
        {
            var baseUrl = $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&VERSION=1.3.0";
            await AssertWmsBadRequestAsync($"{baseUrl}&REQUEST=GetMap&LAYERS={NonSpatialTableName}&STYLES=&CRS=EPSG:4326&BBOX=37.7,-122.5,37.9,-122.3&WIDTH=32&HEIGHT=32&FORMAT=image/png", "LayerNotDefined");
            await AssertWmsBadRequestAsync($"{baseUrl}&REQUEST=GetMap&LAYERS={RasterLayerName}&STYLES=&CRS=EPSG:4326&BBOX=37.7,-122.5,37.9,-122.3&WIDTH=32&HEIGHT=32&FORMAT=image/png&FILTER={Uri.EscapeDataString("<Filter><PropertyIsEqualTo><ValueReference>code</ValueReference><Literal>1</Literal></PropertyIsEqualTo></Filter>")}", "InvalidParameterValue");
            await AssertWmsBadRequestAsync($"{baseUrl}&REQUEST=GetFeatureInfo&LAYERS={RasterLayerName}&QUERY_LAYERS={RasterLayerName}&STYLES=&CRS=EPSG:4326&BBOX=37.7,-122.5,37.9,-122.3&WIDTH=32&HEIGHT=32&I=16&J=16&INFO_FORMAT=application/json", "LayerNotQueryable");
            await AssertWmsBadRequestAsync($"{baseUrl}&REQUEST=GetLegendGraphic&LAYER={RasterLayerName}&FORMAT=image/png", "OperationNotSupported");
        }
        finally
        {
            provider.SetGraph(original, schema: _fixture.CurrentSchema);
        }
    }

    private async Task AssertWmsBadRequestAsync(string url, string expectedCode)
    {
        using var response = await _fixture.Client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain(expectedCode);
    }

    private async Task SeedPostGisRasterAsync(int layerId)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO honua.layers (layer_id, layer_name, table_name, geometry_type)
            VALUES (@layerId, 'wms_raster_' || @layerId, 'raster_data', 'NONE')
            ON CONFLICT (layer_id) DO NOTHING;
            DELETE FROM honua.raster_data WHERE layer_id = @layerId;
            INSERT INTO honua.raster_data (layer_id, name, raster, acquisition_date, created_at)
            SELECT @layerId, 'wms-raster-test',
                ST_AddBand(
                    ST_AddBand(
                        ST_AddBand(
                            ST_MakeEmptyRaster(8, 8, -122.5, 37.9, 0.025, -0.025, 0, 0, 4326),
                            '8BUI'::text, 220, 0),
                        '8BUI'::text, 40, 0),
                    '8BUI'::text, 160, 0),
                NOW(), NOW();
            """;
        command.Parameters.AddWithValue("layerId", layerId);
        await command.ExecuteNonQueryAsync();
    }

    private static MetadataV2ObjectSpatial DefaultRasterSpatial() => new()
    {
        SpatialReference = new MetadataV2SpatialReference { Srid = 4326, Crs = "EPSG:4326", IsGeographic = true },
        Bbox = new MetadataV2Bbox { West = -122.5, South = 37.7, East = -122.3, North = 37.9 }
    };

    private static MetadataV2Graph WithRasterAndNonSpatialPublications(
        MetadataV2Graph original,
        MetadataV2ObjectSpatial? spatial)
    {
        var service = original.Services.First(s => s.Metadata.Name == WebAppFixture.TestServiceId);
        var resources = BuildTestResources(spatial);
        var bindings = BuildTestBindings();
        var publications = BuildTestPublications(service.Metadata.Id);

        return original with
        {
            Revision = original.Revision + 1,
            Resources = [.. original.Resources, .. resources],
            StorageBindings = [.. original.StorageBindings, .. bindings],
            Publications = [.. original.Publications, .. publications]
        };
    }

    private static MetadataV2Object[] BuildTestResources(MetadataV2ObjectSpatial? spatial) =>
    [
        new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-wms-raster", Name = RasterLayerName, Title = "Ortho Imagery" },
            Type = MetadataV2ObjectType.RasterDataset,
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
            Spatial = spatial,
            AccessPolicy = new AccessPolicy { AllowAnonymous = true, AllowAnonymousWrite = false },
            StorageBindingIds = ["bind-wms-raster"]
        },
        new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-wms-nonspatial", Name = NonSpatialTableName, Title = "Lookup Table" },
            Type = MetadataV2ObjectType.Table,
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
            SchemaFields = [new MetadataV2Field { Name = "code", Type = MetadataV2FieldType.String }],
            StorageBindingIds = ["bind-wms-nonspatial"]
        },
        new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-wms-protected-raster", Name = ProtectedRasterLayerName, Title = "Private Imagery" },
            Type = MetadataV2ObjectType.RasterDataset,
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
            Spatial = spatial,
            AccessPolicy = new AccessPolicy { AllowAnonymous = false, AllowAnonymousWrite = false },
            StorageBindingIds = ["bind-wms-protected-raster"]
        }
    ];

    private static MetadataV2StorageBinding[] BuildTestBindings() =>
    [
        CreateBinding("bind-wms-raster", "res-wms-raster", RasterStorageLayerId),
        CreateBinding("bind-wms-nonspatial", "res-wms-nonspatial", NonSpatialStorageLayerId),
        CreateBinding("bind-wms-protected-raster", "res-wms-protected-raster", ProtectedRasterStorageLayerId)
    ];

    private static MetadataV2StorageBinding CreateBinding(string id, string resourceId, int storageLayerId) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = id, Name = id },
        ResourceId = resourceId,
        ConnectionId = "conn-default",
        Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
        StorageType = MetadataV2StorageType.RelationalTable,
        StorageLayerId = storageLayerId
    };

    private static MetadataV2Layer[] BuildTestPublications(string serviceId) =>
    [
        CreatePublication(serviceId, ("pub-wms-raster", "res-wms-raster", RasterLayerName, 101)),
        CreatePublication(serviceId, ("pub-wms-nonspatial", "res-wms-nonspatial", NonSpatialTableName, 102)),
        CreatePublication(serviceId, ("pub-wms-protected-raster", "res-wms-protected-raster", ProtectedRasterLayerName, 103))
    ];

    private static MetadataV2Layer CreatePublication(
        string serviceId,
        (string Id, string ResourceId, string Name, int LayerIndex) spec) => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = spec.Id, Name = spec.Name },
            ServiceId = serviceId,
            ResourceId = spec.ResourceId,
            ServiceLocalId = spec.Name,
            LayerIndex = spec.LayerIndex,
            IsPrimary = true
        };
}
