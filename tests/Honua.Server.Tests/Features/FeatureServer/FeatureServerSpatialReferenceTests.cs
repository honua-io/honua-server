// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.FeatureServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.FeatureServer;

[Collection("Database")]
[Protocol(Protocols.FeatureServer)]
public sealed class FeatureServerSpatialReferenceTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const double CoordinateTolerance = 0.0001;
    private long _pointObjectId;

    public async Task InitializeAsync()
    {
        _fixture.UseSeed(Path.Combine("tests", "seed", "spatial-reference.yaml"));
        await _fixture.InitializeAsync();

        var schema = _fixture.CurrentSchema ?? throw new InvalidOperationException("Schema was not initialized.");
        _pointObjectId = await SpatialReferenceTestData.InsertPointAsync(
            _fixture.Postgres,
            schema,
            SpatialReferenceTestLayerCatalog.PointLayerId,
            -122.4194,
            37.7749,
            "San Francisco");
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_WithInSrOutSr_TransformsSpatialFilterAndOutput()
    {
        var geometry = "-122.4194,37.7749";
        var requestUri = $"/rest/services/{SpatialReferenceTestLayerCatalog.ServiceId}/FeatureServer/{SpatialReferenceTestLayerCatalog.PointLayerId}/query" +
                         $"?geometry={Uri.EscapeDataString(geometry)}&geometryType=esriGeometryPoint&inSR=4326&outSR=4326&f=json";

        var response = await _fixture.Client.GetAsync(requestUri);

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);

        result.Should().NotBeNull();
        result!.SpatialReference.Should().NotBeNull();
        result.SpatialReference!.Wkid.Should().Be(4326);
        result.Features.Should().HaveCount(1);
        result.Features[0].Geometry.Should().NotBeNull();

        var geometryResult = result.Features[0].Geometry!;
        geometryResult.X.Should().BeApproximately(-122.4194, CoordinateTolerance);
        geometryResult.Y.Should().BeApproximately(37.7749, CoordinateTolerance);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_WithGeoJsonFormat_OnProjectedLayer_ReturnsWgs84Coordinates()
    {
        var requestUri = $"/rest/services/{SpatialReferenceTestLayerCatalog.ServiceId}/FeatureServer/{SpatialReferenceTestLayerCatalog.PointLayerId}/query" +
                         $"?where=objectid%20%3D%20{_pointObjectId}&f=geojson";

        var response = await _fixture.Client.GetAsync(requestUri);

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/geo+json");

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var feature = document.RootElement.GetProperty("features")[0];
        var coordinates = feature.GetProperty("geometry").GetProperty("coordinates");

        coordinates[0].GetDouble().Should().BeApproximately(-122.4194, CoordinateTolerance);
        coordinates[1].GetDouble().Should().BeApproximately(37.7749, CoordinateTolerance);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_WithGeoJsonFormat_LargeResultSet_UsesStreamingAndReturnsWgs84()
    {
        var requestUri = $"/rest/services/{SpatialReferenceTestLayerCatalog.ServiceId}/FeatureServer/{SpatialReferenceTestLayerCatalog.PointLayerId}/query" +
                         $"?where=objectid%20%3D%20{_pointObjectId}&resultRecordCount=1500&f=geojson";

        var response = await _fixture.Client.GetAsync(requestUri);

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/geo+json");
        Assert.True(response.Headers.TransferEncodingChunked ?? false, "Response should use chunked transfer encoding for streaming");

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var feature = document.RootElement.GetProperty("features")[0];
        var coordinates = feature.GetProperty("geometry").GetProperty("coordinates");

        coordinates[0].GetDouble().Should().BeApproximately(-122.4194, CoordinateTolerance);
        coordinates[1].GetDouble().Should().BeApproximately(37.7749, CoordinateTolerance);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_WithGeoJsonFormat_RejectsNonWgs84OutSr()
    {
        var requestUri = $"/rest/services/{SpatialReferenceTestLayerCatalog.ServiceId}/FeatureServer/{SpatialReferenceTestLayerCatalog.PointLayerId}/query" +
                         $"?where=objectid%20%3D%20{_pointObjectId}&f=geojson&outSR=3857";

        var response = await _fixture.Client.GetAsync(requestUri);

        response.Be400BadRequest();
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_WithLineGeometry_TransformsToLayerSrid()
    {
        var linePath = new[]
        {
            new[] { -122.5, 37.7 },
            new[] { -122.3, 37.8 }
        };

        var request = new ApplyEditsRequest
        {
            Adds =
            [
                new GeoServicesFeature
                {
                    Attributes = new Dictionary<string, object?>
                    {
                        ["name"] = "Line Feature"
                    },
                    Geometry = new GeoServicesGeometry
                    {
                        Paths = [linePath],
                        SpatialReference = new GeoServicesSpatialReference { Wkid = 4326 }
                    }
                }
            ]
        };

        var json = JsonSerializer.Serialize(request, FeatureServerJsonContext.Default.ApplyEditsRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{SpatialReferenceTestLayerCatalog.ServiceId}/FeatureServer/{SpatialReferenceTestLayerCatalog.LineLayerId}/applyEdits",
            content);

        response.Be200Ok();
        var responseContent = await response.Content.ReadAsStringAsync();
        var applyResponse = JsonSerializer.Deserialize(responseContent, FeatureServerJsonContext.Default.ApplyEditsResponse);

        applyResponse.Should().NotBeNull();
        applyResponse!.AddResults.Should().NotBeNull();
        applyResponse.AddResults![0].Success.Should().BeTrue(
            $"error: {applyResponse.AddResults[0].Error?.Description}");
        applyResponse.AddResults[0].ObjectId.Should().NotBeNull();

        var objectId = applyResponse.AddResults[0].ObjectId!.Value;
        var schema = _fixture.CurrentSchema ?? throw new InvalidOperationException("Schema was not initialized.");
        var srid = await SpatialReferenceTestData.GetGeometrySridAsync(
            _fixture.Postgres,
            schema,
            objectId,
            SpatialReferenceTestLayerCatalog.LineLayerId);
        srid.Should().Be(SpatialReferenceTestLayerCatalog.LayerSrid);

        var queryResponse = await _fixture.Client.GetAsync(
            $"/rest/services/{SpatialReferenceTestLayerCatalog.ServiceId}/FeatureServer/{SpatialReferenceTestLayerCatalog.LineLayerId}/query" +
            $"?where=objectid%20%3D%20{objectId}&outSR=4326&f=json");

        queryResponse.Be200Ok();
        var queryContent = await queryResponse.Content.ReadAsStringAsync();
        var queryResult = JsonSerializer.Deserialize(queryContent, FeatureServerJsonContext.Default.QueryResponse);

        queryResult.Should().NotBeNull();
        queryResult!.Features.Should().HaveCount(1);
        var geometryResult = queryResult.Features[0].Geometry;
        geometryResult.Should().NotBeNull();
        geometryResult!.Paths.Should().NotBeNull();
        geometryResult.Paths!.Length.Should().Be(1);
        geometryResult.Paths[0].Length.Should().BeGreaterThanOrEqualTo(2);

        geometryResult.Paths[0][0][0].Should().BeApproximately(-122.5, CoordinateTolerance);
        geometryResult.Paths[0][0][1].Should().BeApproximately(37.7, CoordinateTolerance);
        geometryResult.Paths[0][^1][0].Should().BeApproximately(-122.3, CoordinateTolerance);
        geometryResult.Paths[0][^1][1].Should().BeApproximately(37.8, CoordinateTolerance);
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_WithPolygonHole_PreservesRingsAndTransforms()
    {
        var outerRing = EnsureClockwise(new[]
        {
            new[] { -122.5, 37.7 },
            new[] { -122.3, 37.7 },
            new[] { -122.3, 37.9 },
            new[] { -122.5, 37.9 },
            new[] { -122.5, 37.7 }
        });

        var holeRing = EnsureCounterClockwise(new[]
        {
            new[] { -122.45, 37.75 },
            new[] { -122.35, 37.75 },
            new[] { -122.35, 37.85 },
            new[] { -122.45, 37.85 },
            new[] { -122.45, 37.75 }
        });

        var request = new ApplyEditsRequest
        {
            Adds =
            [
                new GeoServicesFeature
                {
                    Attributes = new Dictionary<string, object?>
                    {
                        ["name"] = "Polygon Feature"
                    },
                    Geometry = new GeoServicesGeometry
                    {
                        Rings = [outerRing, holeRing],
                        SpatialReference = new GeoServicesSpatialReference { Wkid = 4326 }
                    }
                }
            ]
        };

        var json = JsonSerializer.Serialize(request, FeatureServerJsonContext.Default.ApplyEditsRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{SpatialReferenceTestLayerCatalog.ServiceId}/FeatureServer/{SpatialReferenceTestLayerCatalog.PolygonLayerId}/applyEdits",
            content);

        response.Be200Ok();
        var responseContent = await response.Content.ReadAsStringAsync();
        var applyResponse = JsonSerializer.Deserialize(responseContent, FeatureServerJsonContext.Default.ApplyEditsResponse);

        applyResponse.Should().NotBeNull();
        applyResponse!.AddResults.Should().NotBeNull();
        applyResponse.AddResults![0].Success.Should().BeTrue(
            $"error: {applyResponse.AddResults[0].Error?.Description}");
        applyResponse.AddResults[0].ObjectId.Should().NotBeNull();

        var objectId = applyResponse.AddResults[0].ObjectId!.Value;
        var schema = _fixture.CurrentSchema ?? throw new InvalidOperationException("Schema was not initialized.");
        var srid = await SpatialReferenceTestData.GetGeometrySridAsync(
            _fixture.Postgres,
            schema,
            objectId,
            SpatialReferenceTestLayerCatalog.PolygonLayerId);
        srid.Should().Be(SpatialReferenceTestLayerCatalog.LayerSrid);

        var queryResponse = await _fixture.Client.GetAsync(
            $"/rest/services/{SpatialReferenceTestLayerCatalog.ServiceId}/FeatureServer/{SpatialReferenceTestLayerCatalog.PolygonLayerId}/query" +
            $"?where=objectid%20%3D%20{objectId}&outSR=4326&f=json");

        queryResponse.Be200Ok();
        var queryContent = await queryResponse.Content.ReadAsStringAsync();
        var queryResult = JsonSerializer.Deserialize(queryContent, FeatureServerJsonContext.Default.QueryResponse);

        queryResult.Should().NotBeNull();
        queryResult!.Features.Should().HaveCount(1);
        var geometryResult = queryResult.Features[0].Geometry;
        geometryResult.Should().NotBeNull();
        geometryResult!.Rings.Should().NotBeNull();
        geometryResult.Rings!.Length.Should().Be(2);

        var ringOrientations = geometryResult.Rings.Select(IsClockwise).ToArray();
        ringOrientations.Should().ContainSingle(isClockwise => isClockwise);
        ringOrientations.Should().ContainSingle(isClockwise => !isClockwise);
    }

    private static bool IsClockwise(double[][] ring)
    {
        var area = 0.0;
        for (var i = 0; i < ring.Length - 1; i++)
        {
            var x1 = ring[i][0];
            var y1 = ring[i][1];
            var x2 = ring[i + 1][0];
            var y2 = ring[i + 1][1];
            area += (x2 - x1) * (y2 + y1);
        }

        return area > 0;
    }

    private static double[][] EnsureClockwise(double[][] ring)
    {
        if (IsClockwise(ring))
        {
            return ring;
        }

        var reversed = ring.Reverse().ToArray();
        return reversed;
    }

    private static double[][] EnsureCounterClockwise(double[][] ring)
    {
        if (!IsClockwise(ring))
        {
            return ring;
        }

        var reversed = ring.Reverse().ToArray();
        return reversed;
    }
}
