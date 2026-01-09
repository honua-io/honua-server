// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.OgcFeatures;
using Honua.Server.Features.OgcFeatures.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.OgcFeatures;

[Collection("Database")]
[Protocol(Protocols.OgcApiFeatures)]
[Operation(Operations.Query)]
public sealed class OgcFeaturesSpatialReferenceTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        _fixture.UseSeed(Path.Combine("tests", "seed", "spatial-reference.yaml"));
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items")]
    public async Task CreateFeature_WithNon4326Layer_TransformsToLayerSrid()
    {
        var feature = new GeoJsonFeature
        {
            Type = "Feature",
            Geometry = new SimpleGeoJsonGeometry
            {
                Type = "Point",
                CoordinatesJson = "[-122.4194, 37.7749]"
            },
            Properties = new Dictionary<string, object?>
            {
                ["name"] = "OGC SRID Feature"
            }
        };

        var json = JsonSerializer.Serialize(feature, OgcJsonContext.Default.GeoJsonFeature);
        var content = new StringContent(json, Encoding.UTF8, "application/geo+json");
        var response = await _fixture.Client.PostAsync(
            $"/ogc/features/collections/{SpatialReferenceTestLayerCatalog.PointLayerId}/items",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var responseContent = await response.Content.ReadAsStringAsync();
        var created = JsonSerializer.Deserialize(responseContent, OgcJsonContext.Default.GeoJsonFeature);
        created.Should().NotBeNull();
        created!.Id.Should().NotBeNull();

        var schema = _fixture.CurrentSchema ?? throw new InvalidOperationException("Schema was not initialized.");
        var srid = await SpatialReferenceTestData.GetGeometrySridAsync(
            _fixture.Postgres,
            schema,
            created.Id!.Value,
            SpatialReferenceTestLayerCatalog.PointLayerId);
        srid.Should().Be(SpatialReferenceTestLayerCatalog.LayerSrid);
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}")]
    public async Task GetCollection_WithNon4326Layer_IncludesStorageCrs()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{SpatialReferenceTestLayerCatalog.PointLayerId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var collection = JsonSerializer.Deserialize(content, OgcJsonContext.Default.CollectionInfo);

        collection.Should().NotBeNull();
        collection!.Crs.Should().Contain(OgcFeaturesUtilities.Crs84Uri);
        collection.Crs.Should().Contain("http://www.opengis.net/def/crs/EPSG/0/3857");
        collection.StorageCrs.Should().Be("http://www.opengis.net/def/crs/EPSG/0/3857");
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithProjectedBboxCrs_AllowsProjectedRange()
    {
        var bbox = "-20000000,-20000000,20000000,20000000";
        var bboxCrs = "http://www.opengis.net/def/crs/EPSG/0/3857";
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{SpatialReferenceTestLayerCatalog.PointLayerId}/items" +
            $"?bbox={Uri.EscapeDataString(bbox)}&bbox-crs={Uri.EscapeDataString(bboxCrs)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithCrsParameter_SetsContentCrsHeader()
    {
        var crs = "EPSG:3857";
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{SpatialReferenceTestLayerCatalog.PointLayerId}/items?crs={Uri.EscapeDataString(crs)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues("Content-Crs", out var contentCrsValues).Should().BeTrue();
        contentCrsValues!.Single().Should().Be("<http://www.opengis.net/def/crs/EPSG/0/3857>");
    }
}
