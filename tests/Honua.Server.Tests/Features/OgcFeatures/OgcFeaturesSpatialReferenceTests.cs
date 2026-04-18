// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
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
    public async Task CreateFeature_WithNon4326Layer_MismatchedContentCrs_ReturnsBadRequest()
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

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var responseContent = await response.Content.ReadAsStringAsync();
        var problem = JsonSerializer.Deserialize<JsonElement>(responseContent);
        problem.GetProperty("status").GetInt32().Should().Be(400);
        problem.GetProperty("detail").GetString().Should().Contain("does not match layer SRID");
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
    public async Task GetItems_WithFilterCrs_UsesFilterSrid()
    {
        var feature = new GeoJsonFeature
        {
            Type = "Feature",
            Geometry = new SimpleGeoJsonGeometry
            {
                Type = "Point",
                CoordinatesJson = "[0, 0]"
            },
            Properties = new Dictionary<string, object?>
            {
                ["name"] = "Filter CRS Feature"
            }
        };

        var json = JsonSerializer.Serialize(feature, OgcJsonContext.Default.GeoJsonFeature);
        var content = new StringContent(json, Encoding.UTF8, "application/geo+json");
        var createResponse = await _fixture.Client.PostAsync(
            $"/ogc/features/collections/{SpatialReferenceTestLayerCatalog.PointLayerId}/items",
            content);

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var createdContent = await createResponse.Content.ReadAsStringAsync();
        var created = JsonSerializer.Deserialize(createdContent, OgcJsonContext.Default.GeoJsonFeature);
        created.Should().NotBeNull();
        created!.Id.Should().NotBeNull();
        var createdFeatureId = NormalizeFeatureId(created.Id);
        createdFeatureId.Should().NotBeNull();

        var schema = _fixture.CurrentSchema ?? throw new InvalidOperationException("Schema was not initialized.");
        var projected = await SpatialReferenceTestData.GetGeometryCoordinatesAsync(
            _fixture.Postgres,
            schema,
            createdFeatureId!.Value,
            SpatialReferenceTestLayerCatalog.PointLayerId,
            3395);
        projected.Should().NotBeNull();

        var filter = FormattableString.Invariant(
            $"INTERSECTS(geometry, POINT({projected!.Value.X} {projected.Value.Y}))");
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{SpatialReferenceTestLayerCatalog.PointLayerId}/items" +
            $"?filter={Uri.EscapeDataString(filter)}&filter-crs=EPSG:3395");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        var collection = JsonSerializer.Deserialize(responseContent, OgcJsonContext.Default.FeatureCollection);
        collection.Should().NotBeNull();
        collection!.Features.Should().Contain(item => NormalizeFeatureId(item.Id) == createdFeatureId);
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
    public async Task GetItems_WithRegistryBackedExplicitGeometryCrs_UsesNonAdvertisedCrs()
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
                ["name"] = "Registry CRS Filter Feature"
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

        var schema = _fixture.CurrentSchema ?? throw new InvalidOperationException("Schema was not initialized.");
        var projected = await SpatialReferenceTestData.GetGeometryCoordinatesAsync(
            _fixture.Postgres,
            schema,
            createdFeatureId!.Value,
            SpatialReferenceTestLayerCatalog.PointLayerId,
            3395);
        projected.Should().NotBeNull();

        var ring = new[]
        {
            new[] { projected!.Value.X - 1d, projected.Value.Y - 1d },
            new[] { projected.Value.X + 1d, projected.Value.Y - 1d },
            new[] { projected.Value.X + 1d, projected.Value.Y + 1d },
            new[] { projected.Value.X - 1d, projected.Value.Y + 1d },
            new[] { projected.Value.X - 1d, projected.Value.Y - 1d }
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

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        var collection = JsonSerializer.Deserialize(responseContent, OgcJsonContext.Default.FeatureCollection);
        collection.Should().NotBeNull();
        collection!.Features.Should().Contain(f => NormalizeFeatureId(f.Id) == createdFeatureId);
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
    public async Task GetItems_WithCrsParameter_SetsContentCrsHeader()
    {
        var crs = "EPSG:3395";
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{SpatialReferenceTestLayerCatalog.PointLayerId}/items?crs={Uri.EscapeDataString(crs)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues("Content-Crs", out var contentCrsValues).Should().BeTrue();
        contentCrsValues!.Single().Should().Be("<http://www.opengis.net/def/crs/EPSG/0/3395>");
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithRegistryBackedBboxCrs_UsesNonAdvertisedCrs()
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
                ["name"] = "BBox Registry CRS Feature"
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

        var schema = _fixture.CurrentSchema ?? throw new InvalidOperationException("Schema was not initialized.");
        var projected = await SpatialReferenceTestData.GetGeometryCoordinatesAsync(
            _fixture.Postgres,
            schema,
            createdFeatureId!.Value,
            SpatialReferenceTestLayerCatalog.PointLayerId,
            3395);
        projected.Should().NotBeNull();

        var bbox = FormattableString.Invariant(
            $"{projected!.Value.X - 1},{projected.Value.Y - 1},{projected.Value.X + 1},{projected.Value.Y + 1}");
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{SpatialReferenceTestLayerCatalog.PointLayerId}/items" +
            $"?bbox={Uri.EscapeDataString(bbox)}&bbox-crs=EPSG:3395");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        var collection = JsonSerializer.Deserialize(responseContent, OgcJsonContext.Default.FeatureCollection);
        collection.Should().NotBeNull();
        collection!.Features.Should().Contain(item => NormalizeFeatureId(item.Id) == createdFeatureId);
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
