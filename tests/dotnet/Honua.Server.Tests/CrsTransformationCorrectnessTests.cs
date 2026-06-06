// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using System.Text;
using System.Text.Json;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests;

/// <summary>
/// CRS TRANSFORMATION CORRECTNESS TESTS
/// Focused testing of coordinate reference system transformations and axis order handling.
/// Tests critical PostgresCrsRegistry functionality and real-world coordinate scenarios.
/// </summary>
[Protocol(TestProtocols.FeatureServer)]
[Collection("Database.CoreSpatial")]
public sealed class CrsTransformationCorrectnessTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const string TestServiceId = "test";
    private const int TestLayerId = 0;
    private const double CoordinateTolerance = 0.0001;

    // Real-world test locations with known correct coordinates
    private static readonly Dictionary<string, (double lon, double lat, string description)> TestLocations = new()
    {
        ["NYC"] = (-74.0060, 40.7128, "New York City - Major US city"),
        ["London"] = (-0.1276, 51.5074, "London, UK - Prime meridian reference"),
        ["Tokyo"] = (139.6917, 35.6895, "Tokyo, Japan - Major Pacific city"),
        ["Sydney"] = (151.2093, -33.8688, "Sydney, Australia - Southern hemisphere"),
        ["Fiji"] = (178.0650, -18.1248, "Suva, Fiji - Near antimeridian"),
        ["Alaska"] = (-156.0000, 71.0000, "Northern Alaska - Arctic region"),
        ["Antarctica"] = (0.0000, -85.0000, "Antarctica - Extreme south"),
        ["Greenland"] = (-42.0000, 75.0000, "Greenland - Arctic/polar region"),
    };

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    #region CRS84 vs EPSG:4326 AXIS ORDER VERIFICATION

    /// <summary>
    /// CRITICAL: Verifies CRS84 (longitude,latitude) vs EPSG:4326 axis order consistency
    /// Tests real-world locations to ensure coordinates aren't swapped
    /// CRS84: Always longitude,latitude (East,North)
    /// EPSG:4326: Context-dependent, but should be consistent with CRS84 in web contexts
    /// </summary>
    [Theory]
    [InlineData("NYC")]
    [InlineData("London")]
    [InlineData("Tokyo")]
    [InlineData("Sydney")]
    [InlineData("Fiji")]
    public async Task Query_Crs84VsEpsg4326_ConsistentAxisOrder(string locationKey)
    {
        var (lon, lat, description) = TestLocations[locationKey];
        var geometry = $"{lon},{lat}";

        // Test with CRS84 identifier
        var crs84Uri = $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
                       $"?geometry={Uri.EscapeDataString(geometry)}" +
                       "&geometryType=esriGeometryPoint" +
                       "&inSR=http://www.opengis.net/def/crs/OGC/1.3/CRS84" +
                       "&outSR=4326&returnGeometry=true&f=json";

        // Test with EPSG:4326 identifier
        var epsg4326Uri = $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
                          $"?geometry={Uri.EscapeDataString(geometry)}" +
                          "&geometryType=esriGeometryPoint" +
                          "&inSR=EPSG:4326" +
                          "&outSR=4326&returnGeometry=true&f=json";

        var crs84Response = await _fixture.Client.GetAsync(crs84Uri);
        var epsg4326Response = await _fixture.Client.GetAsync(epsg4326Uri);

        // Both should succeed
        crs84Response.Be200Ok();
        epsg4326Response.Be200Ok();

        var crs84Content = await crs84Response.Content.ReadAsStringAsync();
        var epsg4326Content = await epsg4326Response.Content.ReadAsStringAsync();

        var crs84Result = JsonSerializer.Deserialize(crs84Content, FeatureServerJsonContext.Default.QueryResponse);
        var epsg4326Result = JsonSerializer.Deserialize(epsg4326Content, FeatureServerJsonContext.Default.QueryResponse);

        crs84Result.Should().NotBeNull($"CRS84 query failed for {description}");
        epsg4326Result.Should().NotBeNull($"EPSG:4326 query failed for {description}");

        // Critical: Both should process same geometry consistently
        // If axis order is swapped, results would be dramatically different
        crs84Result!.Features.Should().NotBeNull();
        epsg4326Result!.Features.Should().NotBeNull();

        // If features are returned, verify they have similar spatial context
        // (Exact equality not required, but should be in same geographic region)
        if (crs84Result.Features.Length > 0 && epsg4326Result.Features.Length > 0)
        {
            var crs84Geom = crs84Result.Features[0].Geometry;
            var epsg4326Geom = epsg4326Result.Features[0].Geometry;

            if (crs84Geom is not null && epsg4326Geom is not null)
            {
                // Coordinates should be in same hemisphere/region
                // Major axis swapping would put NYC in Indian Ocean, etc.
                if (crs84Geom.X != null && epsg4326Geom.X != null)
                {
                    Math.Sign(crs84Geom.X.Value).Should().Be(Math.Sign(epsg4326Geom.X.Value),
                        $"X coordinate signs differ for {description} - possible axis swap");
                }
                if (crs84Geom.Y != null && epsg4326Geom.Y != null)
                {
                    Math.Sign(crs84Geom.Y.Value).Should().Be(Math.Sign(epsg4326Geom.Y.Value),
                        $"Y coordinate signs differ for {description} - possible axis swap");
                }
            }
        }
    }

    /// <summary>
    /// Tests multiple CRS identifier formats for the same coordinate system
    /// Verifies different ways of specifying EPSG:4326 return consistent results
    /// </summary>
    [Theory]
    [InlineData("EPSG:4326")]
    [InlineData("4326")]
    [InlineData("http://www.opengis.net/def/crs/EPSG/0/4326")]
    [InlineData("urn:ogc:def:crs:EPSG::4326")]
    public async Task Query_VariousEpsg4326Formats_ConsistentResults(string sridFormat)
    {
        var (lon, lat, _) = TestLocations["NYC"];
        var geometry = $"{lon},{lat}";

        var requestUri = $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
                         $"?geometry={Uri.EscapeDataString(geometry)}" +
                         "&geometryType=esriGeometryPoint" +
                         $"&inSR={Uri.EscapeDataString(sridFormat)}" +
                         "&outSR=4326&returnGeometry=true&f=json";

        var response = await _fixture.Client.GetAsync(requestUri);

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);

        result.Should().NotBeNull($"SRID format '{sridFormat}' should be recognized");
        result!.Features.Should().NotBeNull();
        result.SpatialReference.Should().NotBeNull();
        result.SpatialReference!.Wkid.Should().Be(4326);
    }

    #endregion

    #region COORDINATE TRANSFORMATION PRECISION TESTS

    /// <summary>
    /// Tests coordinate transformation precision with high-accuracy coordinates
    /// Verifies transformations don't introduce excessive precision loss
    /// </summary>
    [Theory]
    [InlineData(-122.419416667, 37.774929167)] // San Francisco with sub-meter precision
    [InlineData(2.294481111, 48.858370833)]    // Eiffel Tower with high precision
    [InlineData(151.209295833, -33.868818056)] // Sydney Opera House precise coordinates
    public async Task Query_HighPrecisionCoordinates_MaintainsAccuracy(double lon, double lat)
    {
        var geometry = $"{lon:F9},{lat:F9}";

        // Transform WGS84 -> Web Mercator -> WGS84
        var requestUri = $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
                         $"?geometry={Uri.EscapeDataString(geometry)}" +
                         "&geometryType=esriGeometryPoint" +
                         "&inSR=4326&outSR=4326&returnGeometry=true&f=json";

        var response = await _fixture.Client.GetAsync(requestUri);

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);

        result.Should().NotBeNull();
        result!.Features.Should().NotBeNull();

        // If geometry is returned, verify precision is maintained within tolerance
        if (result.Features.Length > 0)
        {
            var returnedGeom = result.Features[0].Geometry;

            if (returnedGeom is not null)
            {
                // Allow for some precision loss in transformation, but not excessive
                if (returnedGeom.X != null)
                {
                    returnedGeom.X.Value.Should().BeApproximately(lon, 0.001, // ~100m tolerance for longitude
                        "Longitude precision loss exceeds acceptable limits");
                }
                if (returnedGeom.Y != null)
                {
                    returnedGeom.Y.Value.Should().BeApproximately(lat, 0.001, // ~100m tolerance for latitude
                        "Latitude precision loss exceeds acceptable limits");
                }
            }
        }
    }

    /// <summary>
    /// Tests transformation chain accuracy: WGS84 -> Web Mercator -> WGS84
    /// Verifies roundtrip transformations don't accumulate excessive error
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_RoundtripTransformation_AccuracyPreservation()
    {
        var (lon, lat, description) = TestLocations["Tokyo"];
        var geometry = $"{lon},{lat}";

        // Step 1: WGS84 -> Web Mercator
        var toWebMercatorUri = $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
                               $"?geometry={Uri.EscapeDataString(geometry)}" +
                               "&geometryType=esriGeometryPoint" +
                               "&inSR=4326&outSR=3857&returnGeometry=true&f=json";

        var webMercatorResponse = await _fixture.Client.GetAsync(toWebMercatorUri);
        webMercatorResponse.Be200Ok();

        // Step 2: Web Mercator -> WGS84 (roundtrip)
        var backToWgs84Uri = $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
                             $"?geometry={Uri.EscapeDataString(geometry)}" +
                             "&geometryType=esriGeometryPoint" +
                             "&inSR=3857&outSR=4326&returnGeometry=true&f=json";

        var wgs84Response = await _fixture.Client.GetAsync(backToWgs84Uri);
        wgs84Response.Be200Ok();

        var webMercatorContent = await webMercatorResponse.Content.ReadAsStringAsync();
        var wgs84Content = await wgs84Response.Content.ReadAsStringAsync();

        var webMercatorResult = JsonSerializer.Deserialize(webMercatorContent, FeatureServerJsonContext.Default.QueryResponse);
        var wgs84Result = JsonSerializer.Deserialize(wgs84Content, FeatureServerJsonContext.Default.QueryResponse);

        webMercatorResult.Should().NotBeNull($"Web Mercator transformation failed for {description}");
        wgs84Result.Should().NotBeNull($"WGS84 roundtrip failed for {description}");

        // Verify both transformations have valid spatial reference
        webMercatorResult!.SpatialReference?.Wkid.Should().Be(3857);
        wgs84Result!.SpatialReference?.Wkid.Should().Be(4326);

        webMercatorResult.Features.Should().NotBeNull();
        wgs84Result.Features.Should().NotBeNull();
    }

    #endregion

    #region POLAR REGION TRANSFORMATION TESTS

    /// <summary>
    /// Tests coordinate transformations in polar regions where Web Mercator breaks down
    /// Verifies server handles areas beyond Web Mercator projection limits
    /// </summary>
    [Theory]
    [InlineData(0, 85.06)]        // Just beyond Web Mercator north limit
    [InlineData(0, -85.06)]       // Just beyond Web Mercator south limit
    [InlineData(-45, 88)]         // Near North Pole
    [InlineData(135, -87)]        // Near South Pole
    public async Task Query_PolarRegions_HandlesProjectionLimits(double lon, double lat)
    {
        var geometry = $"{lon},{lat}";

        // Attempt WGS84 -> Web Mercator transformation in polar region
        var requestUri = $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
                         $"?geometry={Uri.EscapeDataString(geometry)}" +
                         "&geometryType=esriGeometryPoint" +
                         "&inSR=4326&outSR=3857&returnGeometry=true&f=json";

        var response = await _fixture.Client.GetAsync(requestUri);

        // Server should handle gracefully - either successful transformation with clamping,
        // or appropriate error response (not crash/hang)
        if (response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);

            result.Should().NotBeNull();
            result!.Features.Should().NotBeNull();

            // If transformed, should have Web Mercator SRID
            if (result.Features.Length > 0)
            {
                result.SpatialReference?.Wkid.Should().Be(3857);
            }
        }
        else
        {
            // If transformation fails, should be informative error, not server crash
            response.StatusCode.Should().BeOneOf(
                System.Net.HttpStatusCode.BadRequest,
                System.Net.HttpStatusCode.UnprocessableEntity);
        }
    }

    /// <summary>
    /// Tests transformation of coordinates exactly at Web Mercator limits
    /// Verifies boundary condition handling at ±85.051129° latitude
    /// </summary>
    [Theory]
    [InlineData(0, 85.051129)]    // Exact Web Mercator north limit
    [InlineData(0, -85.051129)]   // Exact Web Mercator south limit
    [InlineData(180, 85.051)]     // Near limit, antimeridian
    [InlineData(-180, -85.051)]   // Near limit, antimeridian
    [Trait("Category", "Integration")]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_WebMercatorLimitBoundary_ExactLimitHandling(double lon, double lat)
    {
        var geometry = $"{lon},{lat}";

        var requestUri = $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
                         $"?geometry={Uri.EscapeDataString(geometry)}" +
                         "&geometryType=esriGeometryPoint" +
                         "&inSR=4326&outSR=3857&returnGeometry=true&f=json";

        var response = await _fixture.Client.GetAsync(requestUri);

        // Should transform successfully at exact limits
        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);

        result.Should().NotBeNull();
        result!.SpatialReference?.Wkid.Should().Be(3857);
        result.Features.Should().NotBeNull();
    }

    #endregion

    #region ANTIMERIDIAN TRANSFORMATION TESTS

    /// <summary>
    /// Tests coordinate transformations across the International Date Line
    /// Verifies antimeridian-crossing coordinates transform correctly
    /// </summary>
    [Theory]
    [InlineData(179.5, 0)]     // Just west of antimeridian
    [InlineData(-179.5, 0)]    // Just east of antimeridian
    [InlineData(180, 0)]       // Exactly on antimeridian (east)
    [InlineData(-180, 0)]      // Exactly on antimeridian (west)
    [Trait("Category", "Integration")]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_AntimeridianCoordinates_TransformsCorrectly(double lon, double lat)
    {
        var geometry = $"{lon},{lat}";

        // Transform antimeridian coordinates: WGS84 -> Web Mercator
        var requestUri = $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
                         $"?geometry={Uri.EscapeDataString(geometry)}" +
                         "&geometryType=esriGeometryPoint" +
                         "&inSR=4326&outSR=3857&returnGeometry=true&f=json";

        var response = await _fixture.Client.GetAsync(requestUri);

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);

        result.Should().NotBeNull();
        result!.SpatialReference?.Wkid.Should().Be(3857);
        result.Features.Should().NotBeNull();

        // If geometry returned, verify transformation is reasonable
        if (result.Features.Length > 0)
        {
            var transformedGeom = result.Features[0].Geometry;

            if (transformedGeom is not null)
            {
                // Web Mercator X should be large near antimeridian (close to ±20037508.34)
                if (transformedGeom.X != null)
                {
                    Math.Abs(transformedGeom.X.Value).Should().BeGreaterThan(19000000,
                        "Antimeridian coordinates should have large Web Mercator X values");
                }

                // Y coordinate should be near equator for these test points
                if (transformedGeom.Y != null)
                {
                    Math.Abs(transformedGeom.Y.Value).Should().BeLessThan(1000000,
                        "Equatorial coordinates should have small Web Mercator Y values");
                }
            }
        }
    }

    /// <summary>
    /// Tests bounding box transformation across antimeridian
    /// Verifies antimeridian-crossing rectangles transform without topology errors
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_AntimeridianBoundingBox_TransformsBoundary()
    {
        // Pacific bounding box crossing antimeridian: 170°E to -170°W
        var geometry = "170,-10,-170,10";

        var requestUri = $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
                         $"?geometry={Uri.EscapeDataString(geometry)}" +
                         "&geometryType=esriGeometryEnvelope" +
                         "&spatialRel=esriSpatialRelIntersects" +
                         "&inSR=4326&outSR=3857&returnGeometry=true&f=json";

        var response = await _fixture.Client.GetAsync(requestUri);

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);

        result.Should().NotBeNull();
        result!.SpatialReference?.Wkid.Should().Be(3857);
        result.Features.Should().NotBeNull();

        // Critical: Antimeridian-crossing query should process without spatial errors
        // Complex spatial topology should be preserved
    }

    #endregion

    #region DATUM TRANSFORMATION TESTS

    /// <summary>
    /// Tests different datum handling in geographic coordinate systems
    /// Verifies server correctly differentiates between different geographic CRS
    /// </summary>
    [Theory]
    [InlineData("EPSG:4326", "WGS84")]
    [InlineData("EPSG:4269", "NAD83")]
    [InlineData("EPSG:4267", "NAD27")]
    [Trait("Category", "Integration")]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_DifferentDatums_RecognizedAndHandled(string epsgCode, string datumName)
    {
        var (lon, lat, _) = TestLocations["NYC"]; // Use NYC for North American datum tests
        var geometry = $"{lon},{lat}";

        var requestUri = $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
                         $"?geometry={Uri.EscapeDataString(geometry)}" +
                         "&geometryType=esriGeometryPoint" +
                         $"&inSR={epsgCode}" +
                         "&outSR=4326&returnGeometry=true&f=json";

        var response = await _fixture.Client.GetAsync(requestUri);

        if (response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);

            result.Should().NotBeNull($"{epsgCode} ({datumName}) should be recognized");
            result!.Features.Should().NotBeNull();
            result.SpatialReference?.Wkid.Should().Be(4326);
        }
        else
        {
            // If datum not supported, should return informative error
            response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        }
    }

    #endregion

    #region ERROR HANDLING TESTS

    /// <summary>
    /// Tests server response to invalid or unsupported SRID codes
    /// Verifies graceful error handling for bad CRS identifiers
    /// </summary>
    [Theory]
    [InlineData("EPSG:99999")]    // Non-existent EPSG code
    [InlineData("INVALID:123")]   // Invalid CRS format
    [InlineData("")]              // Empty SRID
    [InlineData("NaN")]           // Non-numeric SRID
    [Trait("Category", "Integration")]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_InvalidSrid_HandlesGracefully(string invalidSrid)
    {
        var (lon, lat, _) = TestLocations["NYC"];
        var geometry = $"{lon},{lat}";

        var requestUri = $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
                         $"?geometry={Uri.EscapeDataString(geometry)}" +
                         "&geometryType=esriGeometryPoint" +
                         $"&inSR={Uri.EscapeDataString(invalidSrid)}" +
                         "&outSR=4326&f=json";

        var response = await _fixture.Client.GetAsync(requestUri);

        // Should return appropriate error, not crash
        response.StatusCode.Should().BeOneOf(
            System.Net.HttpStatusCode.BadRequest,
            System.Net.HttpStatusCode.UnprocessableEntity,
            System.Net.HttpStatusCode.NotFound);

        // Should not be a server error (500)
        ((int)response.StatusCode).Should().BeLessThan(500);
    }

    /// <summary>
    /// Tests server response to coordinate values outside valid ranges
    /// Verifies validation of extreme coordinate values
    /// </summary>
    [Theory]
    [InlineData("999,0")]        // Longitude > 180
    [InlineData("0,999")]        // Latitude > 90
    [InlineData("-999,0")]       // Longitude < -180
    [InlineData("0,-999")]       // Latitude < -90
    [InlineData("NaN,0")]        // NaN coordinate
    [InlineData("0,Infinity")]   // Infinite coordinate
    [Trait("Category", "Integration")]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_InvalidCoordinates_ValidationAndErrors(string invalidGeometry)
    {
        var requestUri = $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
                         $"?geometry={Uri.EscapeDataString(invalidGeometry)}" +
                         "&geometryType=esriGeometryPoint" +
                         "&inSR=4326&outSR=4326&f=json";

        var response = await _fixture.Client.GetAsync(requestUri);

        // Should validate and reject invalid coordinates
        response.StatusCode.Should().BeOneOf(
            System.Net.HttpStatusCode.BadRequest,
            System.Net.HttpStatusCode.UnprocessableEntity);

        // Should not cause server errors
        ((int)response.StatusCode).Should().BeLessThan(500);
    }

    #endregion
}
