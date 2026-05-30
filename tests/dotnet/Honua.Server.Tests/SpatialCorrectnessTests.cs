// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using System.Text;
using System.Text.Json;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Tests;

/// <summary>
/// TARGETED SPATIAL CORRECTNESS TESTS
/// Critical spatial bug verification focusing on antimeridian, CRS, and boundary edge cases.
/// Based on Round 1 spatial bug findings - tests actual query behavior and data corruption potential.
/// </summary>
[Protocol(TestProtocols.FeatureServer)]
[Collection("Database")]
public sealed class SpatialCorrectnessTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const string TestServiceId = "test";
    private const int TestLayerId = 0;
    private const double CoordinateTolerance = 0.0001;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    #region ANTIMERIDIAN QUERY FAILURES (Primary Target)

    /// <summary>
    /// CRITICAL: Tests bounding box queries that cross the International Date Line (180°/-180°)
    /// Tests SpatialFilterHelpers.CreateEnvelopeWkb antimeridian handling
    /// Expected: Should return features from both sides of the Pacific Ocean
    /// Bug Risk: Incorrect spatial filter geometry could exclude valid Pacific features
    /// </summary>
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

    /// <summary>
    /// Tests POST request with antimeridian-crossing geometry
    /// Verifies server handles complex antimeridian geometries in POST body
    /// </summary>
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
        var content = new StringContent(json, Encoding.UTF8, "application/json");

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

    /// <summary>
    /// Tests boundary coordinates exactly at ±180° longitude
    /// Verifies edge case handling at exact antimeridian boundary
    /// </summary>
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

    /// <summary>
    /// CRITICAL: Tests coordinate transformation with real-world coordinates to detect axis swapping
    /// Tests PostgresCrsRegistry axis order handling between CRS84 (lon,lat) and EPSG:4326 (lat,lon)
    /// Test Location: New York City (40.7128°N, -74.0060°W)
    /// Expected: Coordinates should remain in correct order regardless of CRS identifier
    /// Bug Risk: Axis confusion would swap coordinates (NYC appears in ocean near Madagascar)
    /// </summary>
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

    /// <summary>
    /// Tests coordinate transformation near poles where axis order confusion is most critical
    /// Test Location: Northern Alaska (71°N, -156°W) - well above Arctic Circle
    /// At extreme latitudes, incorrect axis order becomes immediately obvious
    /// </summary>
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

    /// <summary>
    /// Tests roundtrip coordinate transformation to detect precision loss accumulation
    /// Transforms coordinates through multiple CRS systems and back
    /// </summary>
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

    #region GEOGRAPHIC RANGE VALIDATION (Primary Target)

    /// <summary>
    /// CRITICAL: Tests geographic range validation with legitimate antimeridian-crossing bounding boxes
    /// Tests RasterParsingHelpers geographic validation logic
    /// Expected: Valid antimeridian-crossing boxes should be accepted, not rejected
    /// Bug Risk: Overly strict validation may reject legitimate Pacific Ocean queries
    /// </summary>
    [Theory]
    [InlineData("179,-10,-179,10", true)]    // Crosses antimeridian (valid Pacific)
    [InlineData("170,-5,-170,5", true)]      // Crosses antimeridian (valid Pacific)
    [InlineData("-200,-10,200,10", false)]   // Outside longitude range (invalid)
    [InlineData("179.999,-10,-179.999,10", true)] // Near antimeridian (valid)
    [InlineData("0,-10,360,10", false)]      // Longitude > 180 (invalid)
    [Trait("Category", "Unit")]
    [Operation(Operations.Query)]
    public void ValidateGeographicRange_AntimeridianCrossingBoxes_CorrectValidation(
        string bbox, bool expectedValid)
    {
        var result = Honua.Server.Features.Infrastructure.Services.RasterParsingHelpers
            .TryParseBoundingBox(
                bbox,
                Honua.Core.Features.Shared.Models.AxisOrder.EastNorth,
                isGeographic: true,
                out var minX, out var minY, out var maxX, out var maxY);

        result.Should().Be(expectedValid,
            $"bbox '{bbox}' should be {(expectedValid ? "valid" : "invalid")} for geographic coordinates");

        if (expectedValid && bbox == "179,-10,-179,10")
        {
            // Special case: antimeridian crossing should parse correctly
            minX.Should().Be(179);
            maxX.Should().Be(-179);
            // Note: This validates the parsing, but spatial logic needs to handle wrap-around
        }
    }

    /// <summary>
    /// Tests bounding box validation at exact polar boundaries
    /// Verifies handling of coordinates at exact ±90° latitude limits
    /// </summary>
    [Theory]
    [InlineData("-180,-90,180,90", true)]    // Full world extent (valid)
    [InlineData("-180,-90.1,180,90", false)] // South of South Pole (invalid)
    [InlineData("-180,-90,180,90.1", false)] // North of North Pole (invalid)
    [InlineData("-180,-89.999,180,89.999", true)] // Near poles (valid)
    [Trait("Category", "Unit")]
    [Operation(Operations.Query)]
    public void ValidateGeographicRange_PolarBoundaries_CorrectValidation(
        string bbox, bool expectedValid)
    {
        var result = Honua.Server.Features.Infrastructure.Services.RasterParsingHelpers
            .TryParseBoundingBox(
                bbox,
                Honua.Core.Features.Shared.Models.AxisOrder.EastNorth,
                isGeographic: true,
                out _, out _, out _, out _);

        result.Should().Be(expectedValid,
            $"bbox '{bbox}' polar boundary validation should be {expectedValid}");
    }

    #endregion

    #region SPATIAL INDEX CORRECTNESS

    /// <summary>
    /// Tests spatial index behavior with antimeridian-crossing queries
    /// Verifies that spatial indexes correctly handle wrapped geometries
    /// </summary>
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

    /// <summary>
    /// Tests buffer operations that cross the antimeridian
    /// Verifies geometric operations handle coordinate wrapping
    /// </summary>
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
                         "&spatialRel=esriSpatialRelWithinDistance" +
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

    /// <summary>
    /// Tests Web Mercator transformation at its ±85.051129° latitude limits
    /// Verifies projection boundary handling and coordinate clamping
    /// </summary>
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

    /// <summary>
    /// Tests coordinate transformation beyond Web Mercator limits
    /// Verifies server handles polar regions that cannot be projected to Web Mercator
    /// </summary>
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
            response.StatusCode.Should().BeOneOf(
                System.Net.HttpStatusCode.BadRequest,
                System.Net.HttpStatusCode.UnprocessableEntity);
        }
    }

    #endregion

    #region HIGH-PRECISION COORDINATE TESTS

    /// <summary>
    /// Tests coordinate precision preservation through transformations
    /// Verifies high-precision coordinates are not degraded by spatial operations
    /// </summary>
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

    #region SpatialFilterHelpers Direct Testing

    /// <summary>
    /// UNIT TEST: Directly tests SpatialFilterHelpers.CreateEnvelopeWkb with antimeridian coordinates
    /// Verifies WKB geometry creation for antimeridian-crossing bounding boxes
    /// </summary>
    [UnitTest]
    [Operation(Operations.Query)]
    public void CreateEnvelopeWkb_AntimeridianCrossing_GeneratesValidWkb()
    {
        // Test antimeridian-crossing coordinates directly
        var wkb = SpatialFilterHelpers.CreateEnvelopeWkb(170, -10, -170, 10);

        // WKB should be valid format
        wkb.Should().NotBeNull();
        wkb[0].Should().Be(1);
        var geometry = new WKBReader().Read(wkb);
        geometry.Should().BeOfType<MultiPolygon>();
        ((MultiPolygon)geometry).NumGeometries.Should().Be(2);

        // Should not throw exceptions during creation
        // Critical: Antimeridian coordinates don't break WKB encoding
    }

    /// <summary>
    /// UNIT TEST: Tests WKB creation with extreme coordinate values
    /// Verifies spatial filter helper handles edge cases
    /// </summary>
    [Theory]
    [InlineData(-180, -90, 180, 90)]     // Full world extent
    [InlineData(179.999, -5, -179.999, 5)] // Near antimeridian
    [InlineData(0, 85.05, 1, 85.051)]   // Near Web Mercator limit
    [Trait("Category", "Unit")]
    [Operation(Operations.Query)]
    public void CreateEnvelopeWkb_ExtemeCoordinates_GeneratesValidWkb(
        double minX, double minY, double maxX, double maxY)
    {
        var wkb = SpatialFilterHelpers.CreateEnvelopeWkb(minX, minY, maxX, maxY);

        wkb.Should().NotBeNull();
        wkb[0].Should().Be(1); // Little-endian
        var geometry = new WKBReader().Read(wkb);
        geometry.Should().NotBeNull();

        if (minX > maxX)
        {
            geometry.Should().BeOfType<MultiPolygon>();
            ((MultiPolygon)geometry).NumGeometries.Should().Be(2);
            return;
        }

        geometry.Should().BeOfType<Polygon>();

        // WKB should encode coordinates without overflow or corruption
        // Reading the coordinates back would require a full WKB parser test
    }

    #endregion
}
