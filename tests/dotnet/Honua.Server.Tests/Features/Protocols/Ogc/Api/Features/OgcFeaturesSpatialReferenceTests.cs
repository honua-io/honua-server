// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Protocols.Ogc.Api.Features;
using Honua.Server.Features.Protocols.Ogc.Api.Features.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

[Collection("Database")]
[Protocol(TestProtocols.OgcApiFeatures)]
[Operation(Operations.Query)]
public sealed class OgcFeaturesSpatialReferenceTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        _fixture.UseSeed(Path.Combine("tests", "seed", "spatial-reference.yaml"));
        await _fixture.InitializeAsync();

        // V2 query processor rejects bbox queries on resources whose Spatial slot is
        // unset; the default test V2 graph (WebAppFixture.BuildDefaultTestGraph) does
        // not seed Spatial on layers 101..103. Mirror the v1 SpatialReferenceTestLayerCatalog
        // (EPSG:3857 / Web Mercator) so the V2 OGC API Features paths exercised here
        // can route bbox-crs requests against these layers.
        var sridSpatial = new Honua.Core.Features.Metadata.Domain.V2.MetadataV2ResourceSpatial
        {
            SpatialReference = new Honua.Core.Features.Metadata.Domain.V2.MetadataV2SpatialReference
            {
                Srid = SpatialReferenceTestLayerCatalog.LayerSrid,
                Crs = $"EPSG:{SpatialReferenceTestLayerCatalog.LayerSrid}",
                IsGeographic = false,
            },
        };
        _fixture.UpdateV2ResourceMetadata(
            SpatialReferenceTestLayerCatalog.PointLayerId,
            spatial: sridSpatial with { GeometryType = Honua.Core.Features.Metadata.Domain.V2.MetadataV2GeometryType.Point });
        _fixture.UpdateV2ResourceMetadata(
            SpatialReferenceTestLayerCatalog.LineLayerId,
            spatial: sridSpatial with { GeometryType = Honua.Core.Features.Metadata.Domain.V2.MetadataV2GeometryType.LineString });
        _fixture.UpdateV2ResourceMetadata(
            SpatialReferenceTestLayerCatalog.PolygonLayerId,
            spatial: sridSpatial with { GeometryType = Honua.Core.Features.Metadata.Domain.V2.MetadataV2GeometryType.Polygon });
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items")]
    public async Task CreateFeature_WithAdvertisedAlternateContentCrs_TransformsIntoLayerSrid()
    {
        var feature = new GeoJsonFeature
        {
            Type = "Feature",
            Geometry = new SimpleGeoJsonGeometry
            {
                Type = "Point",
                CoordinatesJson = "[37.7749, -122.4194]"
            },
            Properties = new Dictionary<string, object?>
            {
                ["name"] = "OGC SRID Feature"
            }
        };

        var json = JsonSerializer.Serialize(feature, OgcJsonContext.Default.GeoJsonFeature);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/ogc/features/collections/{SpatialReferenceTestLayerCatalog.PointLayerId}/items")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/geo+json")
        };
        request.Headers.TryAddWithoutValidation("Content-Crs", "<http://www.opengis.net/def/crs/EPSG/0/4326>");
        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var responseContent = await response.Content.ReadAsStringAsync();
        var created = JsonSerializer.Deserialize(responseContent, OgcJsonContext.Default.GeoJsonFeature);
        created.Should().NotBeNull();
        created!.Id.Should().NotBeNull();
        response.Headers.TryGetValues("Content-Crs", out var contentCrsValues).Should().BeTrue();
        contentCrsValues!.Single().Should().Be("<http://www.opengis.net/def/crs/EPSG/0/4326>");

        var featureId = NormalizeFeatureId(created.Id);
        featureId.Should().NotBeNull();

        var schema = _fixture.CurrentSchema ?? throw new InvalidOperationException("Schema was not initialized.");
        var srid = await SpatialReferenceTestData.GetGeometrySridAsync(
            _fixture.Postgres,
            schema,
            featureId!.Value,
            SpatialReferenceTestLayerCatalog.PointLayerId);
        srid.Should().Be(SpatialReferenceTestLayerCatalog.LayerSrid);

        var projectedCoordinates = await SpatialReferenceTestData.GetGeometryCoordinatesAsync(
            _fixture.Postgres,
            schema,
            featureId.Value,
            SpatialReferenceTestLayerCatalog.PointLayerId);
        projectedCoordinates.Should().NotBeNull();
        projectedCoordinates!.Value.X.Should().BeApproximately(-13627665.27, 2d);
        projectedCoordinates.Value.Y.Should().BeApproximately(4547675.35, 2d);

        created.Geometry.Should().NotBeNull();
        var createdCoordinates = JsonDocument.Parse(created.Geometry!.CoordinatesJson!).RootElement.EnumerateArray().ToArray();
        createdCoordinates[0].GetDouble().Should().BeApproximately(37.7749, 1e-4);
        createdCoordinates[1].GetDouble().Should().BeApproximately(-122.4194, 1e-4);

        var responseJson = JsonDocument.Parse(responseContent);
        var representationLinks = responseJson.RootElement.GetProperty("links")
            .EnumerateArray()
            .Where(link =>
                link.TryGetProperty("rel", out var rel) &&
                (rel.GetString() == "self" || rel.GetString() == "alternate"))
            .ToArray();

        representationLinks.Should().NotBeEmpty();
        representationLinks.Should().OnlyContain(link =>
            link.GetProperty("href").GetString()!.Contains(
                "crs=http%3A%2F%2Fwww.opengis.net%2Fdef%2Fcrs%2FEPSG%2F0%2F4326",
                StringComparison.Ordinal));
        representationLinks.Should().Contain(link =>
            link.GetProperty("rel").GetString() == "self" &&
            link.GetProperty("href").GetString()!.Contains("f=geojson", StringComparison.Ordinal));
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items")]
    public async Task CreateFeature_WithNon4326Layer_MatchingContentCrs_CreatesFeature()
    {
        var feature = new GeoJsonFeature
        {
            Type = "Feature",
            Geometry = new SimpleGeoJsonGeometry
            {
                Type = "Point",
                CoordinatesJson = "[1000, 2000]"
            },
            Properties = new Dictionary<string, object?>
            {
                ["name"] = "OGC SRID Feature"
            }
        };

        var json = JsonSerializer.Serialize(feature, OgcJsonContext.Default.GeoJsonFeature);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/ogc/features/collections/{SpatialReferenceTestLayerCatalog.PointLayerId}/items")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/geo+json")
        };
        request.Headers.TryAddWithoutValidation("Content-Crs", "<http://www.opengis.net/def/crs/EPSG/0/3857>");
        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var responseContent = await response.Content.ReadAsStringAsync();
        var created = JsonSerializer.Deserialize(responseContent, OgcJsonContext.Default.GeoJsonFeature);
        created.Should().NotBeNull();
        created!.Id.Should().NotBeNull();
        var featureId = NormalizeFeatureId(created.Id);
        featureId.Should().NotBeNull();

        var schema = _fixture.CurrentSchema ?? throw new InvalidOperationException("Schema was not initialized.");
        var srid = await SpatialReferenceTestData.GetGeometrySridAsync(
            _fixture.Postgres,
            schema,
            featureId!.Value,
            SpatialReferenceTestLayerCatalog.PointLayerId);
        srid.Should().Be(SpatialReferenceTestLayerCatalog.LayerSrid);

        var coordinates = await SpatialReferenceTestData.GetGeometryCoordinatesAsync(
            _fixture.Postgres,
            schema,
            featureId.Value,
            SpatialReferenceTestLayerCatalog.PointLayerId);
        coordinates.Should().NotBeNull();
        coordinates!.Value.X.Should().BeApproximately(1000, 1e-6);
        coordinates.Value.Y.Should().BeApproximately(2000, 1e-6);
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
    public async Task GetItems_WithNonAdvertisedFilterCrs_ReturnsBadRequest()
    {
        var filter = "name = 'Filter CRS Feature'";
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{SpatialReferenceTestLayerCatalog.PointLayerId}/items" +
            $"?filter={Uri.EscapeDataString(filter)}&filter-crs=EPSG:3395");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var responseContent = await response.Content.ReadAsStringAsync();
        JsonDocument.Parse(responseContent).RootElement.GetProperty("detail").GetString().Should().Contain("Unsupported CRS");
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithExplicitGeometryCrsAndFilterCrs_DoesNotSwapExplicitLiteralAxisOrder()
    {
        var feature = new GeoJsonFeature
        {
            Type = "Feature",
            Geometry = new SimpleGeoJsonGeometry
            {
                Type = "Point",
                CoordinatesJson = "[1000, 2000]"
            },
            Properties = new Dictionary<string, object?>
            {
                ["name"] = "Explicit CRS Filter Feature"
            }
        };

        var createJson = JsonSerializer.Serialize(feature, OgcJsonContext.Default.GeoJsonFeature);
        using var createRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/ogc/features/collections/{SpatialReferenceTestLayerCatalog.PointLayerId}/items")
        {
            Content = new StringContent(createJson, Encoding.UTF8, "application/geo+json")
        };
        createRequest.Headers.TryAddWithoutValidation("Content-Crs", "<http://www.opengis.net/def/crs/EPSG/0/3857>");

        var createResponse = await _fixture.Client.SendAsync(createRequest);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var createdContent = await createResponse.Content.ReadAsStringAsync();
        var created = JsonSerializer.Deserialize(createdContent, OgcJsonContext.Default.GeoJsonFeature);
        created.Should().NotBeNull();
        created!.Id.Should().NotBeNull();
        var createdFeatureId = NormalizeFeatureId(created.Id);
        createdFeatureId.Should().NotBeNull();

        var filterJson =
            """{"op":"s_intersects","args":[{"property":"geometry"},{"type":"Point","coordinates":[1000,2000],"crs":{"type":"name","properties":{"name":"EPSG:3857"}}}]}""";
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{SpatialReferenceTestLayerCatalog.PointLayerId}/items" +
            $"?filter-lang=cql2-json&filter={Uri.EscapeDataString(filterJson)}&filter-crs=EPSG:4326");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        var collection = JsonSerializer.Deserialize(responseContent, OgcJsonContext.Default.FeatureCollection);
        collection.Should().NotBeNull();
        collection!.Features.Should().Contain(f => NormalizeFeatureId(f.Id) == createdFeatureId);
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithNonAdvertisedExplicitGeometryCrs_ReturnsBadRequest()
    {
        var ring = new[]
        {
            new[] { -1d, -1d },
            new[] { 1d, -1d },
            new[] { 1d, 1d },
            new[] { -1d, 1d },
            new[] { -1d, -1d }
        };
        var filterJson = JsonSerializer.Serialize(new
        {
            op = "s_intersects",
            args = new object[]
            {
                new { property = "geometry" },
                new
                {
                    type = "Polygon",
                    coordinates = new[] { ring },
                    crs = new
                    {
                        type = "name",
                        properties = new { name = "EPSG:3395" }
                    }
                }
            }
        });
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{SpatialReferenceTestLayerCatalog.PointLayerId}/items" +
            $"?filter-lang=cql2-json&filter={Uri.EscapeDataString(filterJson)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var responseContent = await response.Content.ReadAsStringAsync();
        JsonDocument.Parse(responseContent).RootElement.GetProperty("detail").GetString().Should().Contain("Unsupported explicit geometry CRS 'EPSG:3395'");
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithUnsupportedExplicitGeometryCrs_ReturnsBadRequest()
    {
        var filterJson =
            """{"op":"s_intersects","args":[{"property":"geometry"},{"type":"Point","coordinates":[0,0],"crs":{"type":"name","properties":{"name":"EPSG:999999"}}}]}""";
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{SpatialReferenceTestLayerCatalog.PointLayerId}/items" +
            $"?filter-lang=cql2-json&filter={Uri.EscapeDataString(filterJson)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var responseContent = await response.Content.ReadAsStringAsync();
        var problem = JsonSerializer.Deserialize<JsonElement>(responseContent);
        problem.GetProperty("detail").GetString().Should().Contain("Unsupported explicit geometry CRS 'EPSG:999999'");
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithNonAdvertisedOutputCrs_ReturnsBadRequest()
    {
        var crs = "EPSG:3395";
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{SpatialReferenceTestLayerCatalog.PointLayerId}/items?crs={Uri.EscapeDataString(crs)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var responseContent = await response.Content.ReadAsStringAsync();
        JsonDocument.Parse(responseContent).RootElement.GetProperty("detail").GetString().Should().Contain("Unsupported CRS");
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithNonAdvertisedBboxCrs_ReturnsBadRequest()
    {
        var bbox = "-1,-1,1,1";
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{SpatialReferenceTestLayerCatalog.PointLayerId}/items" +
            $"?bbox={Uri.EscapeDataString(bbox)}&bbox-crs=EPSG:3395");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var responseContent = await response.Content.ReadAsStringAsync();
        JsonDocument.Parse(responseContent).RootElement.GetProperty("detail").GetString().Should().Contain("Unsupported CRS");
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithOutputCrsAndNoBboxCrs_DefaultsBboxToCrs84()
    {
        var feature = new GeoJsonFeature
        {
            Type = "Feature",
            Geometry = new SimpleGeoJsonGeometry
            {
                Type = "Point",
                CoordinatesJson = "[1000, 2000]"
            },
            Properties = new Dictionary<string, object?>
            {
                ["name"] = "BBox Default CRS Feature"
            }
        };

        var createJson = JsonSerializer.Serialize(feature, OgcJsonContext.Default.GeoJsonFeature);
        using var createRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/ogc/features/collections/{SpatialReferenceTestLayerCatalog.PointLayerId}/items")
        {
            Content = new StringContent(createJson, Encoding.UTF8, "application/geo+json")
        };
        createRequest.Headers.TryAddWithoutValidation("Content-Crs", "<http://www.opengis.net/def/crs/EPSG/0/3857>");

        var createResponse = await _fixture.Client.SendAsync(createRequest);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var createdContent = await createResponse.Content.ReadAsStringAsync();
        var created = JsonSerializer.Deserialize(createdContent, OgcJsonContext.Default.GeoJsonFeature);
        created.Should().NotBeNull();
        created!.Id.Should().NotBeNull();
        var createdFeatureId = NormalizeFeatureId(created.Id);
        createdFeatureId.Should().NotBeNull();

        var outputCrs = Uri.EscapeDataString("EPSG:3857");
        var bbox = Uri.EscapeDataString("-1,-1,1,1");
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{SpatialReferenceTestLayerCatalog.PointLayerId}/items?crs={outputCrs}&bbox={bbox}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var responseContent = await response.Content.ReadAsStringAsync();
        var collection = JsonSerializer.Deserialize(responseContent, OgcJsonContext.Default.FeatureCollection);
        collection.Should().NotBeNull();
        collection!.Features.Should().Contain(item => NormalizeFeatureId(item.Id) == createdFeatureId);
    }

    private static long? NormalizeFeatureId(object? id)
    {
        return id switch
        {
            null => null,
            long longId => longId,
            int intId => intId,
            string strId when long.TryParse(strId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedStringId) => parsedStringId,
            JsonElement { ValueKind: JsonValueKind.Number } jsonNumberId when jsonNumberId.TryGetInt64(out var parsedNumberId) => parsedNumberId,
            JsonElement { ValueKind: JsonValueKind.String } jsonStringId
                when long.TryParse(jsonStringId.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedJsonStringId)
                    => parsedJsonStringId,
            _ => null
        };
    }
}
