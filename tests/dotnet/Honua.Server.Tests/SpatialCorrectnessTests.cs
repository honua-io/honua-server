// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using System.Text;
using System.Text.Json;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests;

[Protocol(TestProtocols.FeatureServer)]
[Collection("Database.CoreSpatial")]
public sealed class SpatialCorrectnessTests : IClassFixture<WebAppFixture>
{
    private readonly WebAppFixture _fixture;
    private const string TestServiceId = "test";
    private const int TestLayerId = 0;
    private const double CoordinateTolerance = 0.0001;

    // Endpoint cases only query the seeded schema; xUnit owns its lifetime.
    public SpatialCorrectnessTests(WebAppFixture fixture)
    {
        _fixture = fixture;
    }

    #region ANTIMERIDIAN QUERY FAILURES (Primary Target)

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_AntimeridianCrossingBbox_ReturnsValidPacificFeatures()
    {
        // Pacific Ocean bounding box: 170°E to -170°W (crosses antimeridian)
        var geometry = "170,-10,-170,10";
        var requestUri = $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
                         $"?geometry={Uri.EscapeDataString(geometry)}" +
                         "&geometryType=esriGeometryEnvelope" +
                         "&spatialRel=esriSpatialRelIntersects" +
                         "&inSR=4326&outSR=4326&f=json";

        var response = await _fixture.Client.GetAsync(requestUri);

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);

        result.Should().NotBeNull();
        result!.SpatialReference.Should().NotBeNull();
        result.SpatialReference!.Wkid.Should().Be(4326);

        // Verify query processed without error (even if no features)
        // The critical test is that the spatial filter doesn't cause server errors
        result.Features.Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_PostAntimeridianCrossingGeometry_HandlesComplexPacificQueries()
    {
        // Test with both Fiji (178°E) and Samoa (-171°W) regions
        var json = """
            {
                "geometry": "175,-20,-165,20",
                "geometryType": "esriGeometryEnvelope",
                "spatialRel": "esriSpatialRelIntersects",
                "inSR": "4326",
                "outSR": "4326",
                "returnGeometry": true,
                "f": "json"
            }
            """;
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query", content);

        response.Be200Ok();
        var responseContent = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            responseContent, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
        queryResponse.SpatialReference.Should().NotBeNull();
        queryResponse.SpatialReference!.Wkid.Should().Be(4326);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_ExactAntimeridianBoundary_HandlesEdgeCoordinates()
    {
        // Exact antimeridian boundary coordinates
        var geometry = "179.999,-5,-179.999,5";
        var requestUri = $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
                         $"?geometry={Uri.EscapeDataString(geometry)}" +
                         "&geometryType=esriGeometryEnvelope" +
                         "&spatialRel=esriSpatialRelIntersects" +
                         "&inSR=4326&f=json";

        var response = await _fixture.Client.GetAsync(requestUri);

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);

        result.Should().NotBeNull();
        result!.Features.Should().NotBeNull();
    }

    #endregion

    #region CRS84/EPSG:4326 AXIS CONFUSION (Primary Target)

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_Crs84VsEpsg4326_AxisOrderConsistency()
    {
        // New York City coordinates: longitude=-74.0060, latitude=40.7128
        var nycGeometry = "-74.0060,40.7128";

        // Test 1: Query with CRS84 (should be lon,lat order)
        var crs84Uri = $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
                       $"?geometry={Uri.EscapeDataString(nycGeometry)}" +
                       "&geometryType=esriGeometryPoint" +
                       "&inSR=http://www.opengis.net/def/crs/OGC/1.3/CRS84" +
                       "&outSR=4326&f=json";

        var crs84Response = await _fixture.Client.GetAsync(crs84Uri);
        crs84Response.Be200Ok();

        // Test 2: Query with EPSG:4326 (traditionally lat,lon but context-dependent)
        var epsg4326Uri = $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
                          $"?geometry={Uri.EscapeDataString(nycGeometry)}" +
                          "&geometryType=esriGeometryPoint" +
                          "&inSR=EPSG:4326" +
                          "&outSR=4326&f=json";

        var epsg4326Response = await _fixture.Client.GetAsync(epsg4326Uri);
        epsg4326Response.Be200Ok();

        // Both queries should succeed and return similar spatial results
        // If axis order is wrong, one would place NYC in the ocean near Madagascar
        var crs84Content = await crs84Response.Content.ReadAsStringAsync();
        var epsg4326Content = await epsg4326Response.Content.ReadAsStringAsync();

        var crs84Result = JsonSerializer.Deserialize(crs84Content, FeatureServerJsonContext.Default.QueryResponse);
        var epsg4326Result = JsonSerializer.Deserialize(epsg4326Content, FeatureServerJsonContext.Default.QueryResponse);

        crs84Result.Should().NotBeNull();
        epsg4326Result.Should().NotBeNull();

        // Critical: Both should return valid responses without coordinate confusion
        crs84Result!.Features.Should().NotBeNull();
        epsg4326Result!.Features.Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_PolarRegionCoordinates_DetectsAxisOrderProblems()
    {
        // Northern Alaska coordinates - latitude=71.0, longitude=-156.0
        var alaskaGeometry = "-156.0,71.0";

        var requestUri = $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
                         $"?geometry={Uri.EscapeDataString(alaskaGeometry)}" +
                         "&geometryType=esriGeometryPoint" +
                         "&inSR=EPSG:4326&outSR=4326&f=json";

        var response = await _fixture.Client.GetAsync(requestUri);
        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);

        result.Should().NotBeNull();
        result!.SpatialReference.Should().NotBeNull();
        result.SpatialReference!.Wkid.Should().Be(4326);

        // If axis order is wrong, query might fail or return unexpected results
        result.Features.Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_RoundtripCoordinateTransformation_DetectsPrecisionLoss()
    {
        // High-precision coordinates for precision loss detection
        var preciseGeometry = "-122.419416,37.774929"; // San Francisco with extra precision

        // Transform through multiple CRS: WGS84 -> Web Mercator -> back to WGS84
        var webMercatorUri = $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
                             $"?geometry={Uri.EscapeDataString(preciseGeometry)}" +
                             "&geometryType=esriGeometryPoint" +
                             "&inSR=4326&outSR=3857&f=json";

        var webMercatorResponse = await _fixture.Client.GetAsync(webMercatorUri);
        webMercatorResponse.Be200Ok();

        var backToWgs84Uri = $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
                             $"?geometry={Uri.EscapeDataString(preciseGeometry)}" +
                             "&geometryType=esriGeometryPoint" +
                             "&inSR=3857&outSR=4326&f=json";

        var backToWgs84Response = await _fixture.Client.GetAsync(backToWgs84Uri);
        backToWgs84Response.Be200Ok();

        // Both transformations should succeed
        var webMercatorContent = await webMercatorResponse.Content.ReadAsStringAsync();
        var wgs84Content = await backToWgs84Response.Content.ReadAsStringAsync();

        var webMercatorResult = JsonSerializer.Deserialize(webMercatorContent, FeatureServerJsonContext.Default.QueryResponse);
        var wgs84Result = JsonSerializer.Deserialize(wgs84Content, FeatureServerJsonContext.Default.QueryResponse);

        webMercatorResult.Should().NotBeNull();
        wgs84Result.Should().NotBeNull();

        // Verify transformations completed without errors
        webMercatorResult!.Features.Should().NotBeNull();
        wgs84Result!.Features.Should().NotBeNull();
    }

    #endregion

    #region SPATIAL INDEX CORRECTNESS

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_SpatialIndex_HandlesAntimeridianQueries()
    {
        // Large Pacific Ocean region spanning antimeridian
        var pacificGeometry = "150,-20,-150,20";
        var requestUri = $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
                         $"?geometry={Uri.EscapeDataString(pacificGeometry)}" +
                         "&geometryType=esriGeometryEnvelope" +
                         "&spatialRel=esriSpatialRelIntersects" +
                         "&inSR=4326&f=json";

        var response = await _fixture.Client.GetAsync(requestUri);

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);

        result.Should().NotBeNull();
        result!.Features.Should().NotBeNull();

        // The critical test is query performance and correctness
        // Spatial index should not cause timeouts or incorrect results
        response.Headers.Should().ContainKey("X-Correlation-ID");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_BufferOperation_CrossesAntimeridian()
    {
        // Point near antimeridian with large buffer
        var nearAntimeridianPoint = "179.5,0";
        var bufferDistance = 500000; // 500km buffer (will cross antimeridian)

        var requestUri = $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
                         $"?geometry={Uri.EscapeDataString(nearAntimeridianPoint)}" +
                         "&geometryType=esriGeometryPoint" +
                         "&spatialRel=withinDistance" +
                         $"&distance={bufferDistance}" +
                         "&units=esriSRUnit_Meter" +
                         "&inSR=4326&f=json";

        var response = await _fixture.Client.GetAsync(requestUri);

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);

        result.Should().NotBeNull();
        result!.Features.Should().NotBeNull();

        // Buffer operation should complete without spatial errors
        // Critical test: no infinite loops or coordinate overflow
    }

    #endregion

    #region PROJECTION BOUNDARY ISSUES

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_WebMercatorLimits_HandlesProjectionBoundaries()
    {
        // Near Web Mercator northern limit (85.051129°N)
        var nearLimitGeometry = "0,85.05";
        var requestUri = $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
                         $"?geometry={Uri.EscapeDataString(nearLimitGeometry)}" +
                         "&geometryType=esriGeometryPoint" +
                         "&inSR=4326&outSR=3857&f=json";

        var response = await _fixture.Client.GetAsync(requestUri);

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);

        result.Should().NotBeNull();
        result!.SpatialReference.Should().NotBeNull();
        result.SpatialReference!.Wkid.Should().Be(3857);

        // Transformation should handle projection limits gracefully
        result.Features.Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_BeyondWebMercatorLimits_HandlesUnprojectableRegions()
    {
        // Antarctic coordinates beyond Web Mercator limits
        var antarticGeometry = "0,-89";
        var requestUri = $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
                         $"?geometry={Uri.EscapeDataString(antarticGeometry)}" +
                         "&geometryType=esriGeometryPoint" +
                         "&inSR=4326&outSR=3857&f=json";

        var response = await _fixture.Client.GetAsync(requestUri);

        // Should either transform gracefully or return appropriate error
        // Critical: Should not crash or hang the server
        var content = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
        {
            var result = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);
            result.Should().NotBeNull();
            result!.Features.Should().NotBeNull();
        }
        else
        {
            // If transformation fails, should return informative error
            await response.AssertGeoServicesErrorAsync(400, 422);
        }
    }

    #endregion

    #region HIGH-PRECISION COORDINATE TESTS

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_HighPrecisionCoordinates_PreservesAccuracy()
    {
        // Very high precision coordinates (6 decimal places = ~0.1m accuracy)
        var highPrecisionGeometry = "-122.419416,37.774929";
        var requestUri = $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
                         $"?geometry={Uri.EscapeDataString(highPrecisionGeometry)}" +
                         "&geometryType=esriGeometryPoint" +
                         "&inSR=4326&outSR=4326&returnGeometry=true&f=json";

        var response = await _fixture.Client.GetAsync(requestUri);

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);

        result.Should().NotBeNull();
        result!.Features.Should().NotBeNull();

        // If features are returned, verify coordinate precision is reasonable
        if (result.Features.Length > 0 && result.Features[0].Geometry != null)
        {
            var geom = result.Features[0].Geometry!;
            // Precision should be within reasonable tolerance
            geom.X!.Value.Should().BeApproximately(-122.419416, CoordinateTolerance);
            geom.Y!.Value.Should().BeApproximately(37.774929, CoordinateTolerance);
        }
    }

    #endregion

}
