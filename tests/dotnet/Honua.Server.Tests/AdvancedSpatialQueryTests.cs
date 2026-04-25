// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests;

/// <summary>
/// Integration tests for advanced spatial query patterns (Issue #99).
/// Tests distance-based queries (ST_DWithin, ST_Distance) and K-Nearest Neighbor (KNN) queries.
/// </summary>
[Protocol(TestProtocols.FeatureServer)]
[Collection("Database")]
public sealed class AdvancedSpatialQueryTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const string TestServiceId = "test";
    private const int TestLayerId = 0;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    #region Distance-Based Query Tests (WithinDistance)

    /// <summary>
    /// Tests that queries with esriSpatialRelWithinDistance return features within the specified distance
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithinDistance_ReturnsNearbyFeatures()
    {
        // Arrange - Point geometry and distance in meters
        var pointGeometry = @"{""x"":-122.4194,""y"":37.7749}"; // San Francisco coordinates
        var distance = 50000; // 50km

        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
            $"?geometry={Uri.EscapeDataString(pointGeometry)}" +
            $"&spatialRel=esriSpatialRelWithinDistance" +
            $"&distance={distance}" +
            $"&units=esriSRUnit_Meter" +
            $"&f=json");

        // Assert
        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
    }

    /// <summary>
    /// Tests that distance-based queries work with POST requests
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_PostWithinDistance_ReturnsNearbyFeatures()
    {
        // Arrange
        var json = """
            {
                "geometry": "{\"x\":-122.4194,\"y\":37.7749}",
                "spatialRel": "esriSpatialRelWithinDistance",
                "distance": 100000,
                "units": "esriSRUnit_Meter",
                "returnGeometry": true,
                "f": "json"
            }
            """;
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query", content);

        // Assert
        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            responseContent, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
    }

    /// <summary>
    /// Tests distance queries with kilometers as the unit
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithinDistanceKilometers_ReturnsNearbyFeatures()
    {
        // Arrange
        var pointGeometry = @"{""x"":-122.4194,""y"":37.7749}";
        var distance = 100; // 100km

        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
            $"?geometry={Uri.EscapeDataString(pointGeometry)}" +
            $"&spatialRel=esriSpatialRelWithinDistance" +
            $"&distance={distance}" +
            $"&units=esriSRUnit_Kilometer" +
            $"&f=json");

        // Assert
        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
    }

    /// <summary>
    /// Tests distance queries with miles as the unit
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithinDistanceMiles_ReturnsNearbyFeatures()
    {
        // Arrange
        var pointGeometry = @"{""x"":-122.4194,""y"":37.7749}";
        var distance = 50; // 50 miles

        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
            $"?geometry={Uri.EscapeDataString(pointGeometry)}" +
            $"&spatialRel=esriSpatialRelWithinDistance" +
            $"&distance={distance}" +
            $"&units=esriSRUnit_StatuteMile" +
            $"&f=json");

        // Assert
        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
    }

    /// <summary>
    /// Tests that missing distance parameter returns 400 error for distance-based queries
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithinDistanceMissingDistance_Returns400()
    {
        // Arrange - Missing distance parameter
        var pointGeometry = @"{""x"":-122.4194,""y"":37.7749}";

        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
            $"?geometry={Uri.EscapeDataString(pointGeometry)}" +
            $"&spatialRel=esriSpatialRelWithinDistance" +
            $"&f=json");

        // Assert
        response.Be400BadRequest();
    }

    #endregion

    #region Distance-Based Query Tests (BeyondDistance)

    /// <summary>
    /// Tests that queries with esriSpatialRelBeyondDistance return features beyond the specified distance
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_BeyondDistance_ReturnsFarFeatures()
    {
        // Arrange
        var pointGeometry = @"{""x"":-122.4194,""y"":37.7749}";
        var distance = 1000; // 1km - features beyond this distance

        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
            $"?geometry={Uri.EscapeDataString(pointGeometry)}" +
            $"&spatialRel=esriSpatialRelBeyondDistance" +
            $"&distance={distance}" +
            $"&units=esriSRUnit_Meter" +
            $"&f=json");

        // Assert
        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
    }

    #endregion

    #region K-Nearest Neighbor (KNN) Query Tests

    /// <summary>
    /// Tests that nearestCount parameter returns the specified number of closest features
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_NearestNeighbor_ReturnsKClosestFeatures()
    {
        // Arrange
        var pointGeometry = @"{""x"":-122.4194,""y"":37.7749}";
        var nearestCount = 3;

        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
            $"?geometry={Uri.EscapeDataString(pointGeometry)}" +
            $"&nearestCount={nearestCount}" +
            $"&f=json");

        // Assert
        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
        // Should return at most nearestCount features
        queryResponse.Features.Length.Should().BeLessThanOrEqualTo(nearestCount);
    }

    /// <summary>
    /// Tests KNN query with returnDistance=true includes distance in results
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_NearestNeighborWithDistance_IncludesDistanceInResults()
    {
        // Arrange
        var pointGeometry = @"{""x"":-122.4194,""y"":37.7749}";
        var nearestCount = 3;

        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
            $"?geometry={Uri.EscapeDataString(pointGeometry)}" +
            $"&nearestCount={nearestCount}" +
            $"&returnDistance=true" +
            $"&f=json");

        // Assert
        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
        queryResponse.Fields.Should().Contain(field => field.Name.Equals("distance", StringComparison.OrdinalIgnoreCase));
        queryResponse.Features.Should().NotBeEmpty();
        queryResponse.Features[0].Attributes.Should().ContainKey("distance");
    }

    /// <summary>
    /// Tests KNN query via POST request
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_PostNearestNeighbor_ReturnsKClosestFeatures()
    {
        // Arrange
        var json = """
            {
                "geometry": "{\"x\":-122.4194,\"y\":37.7749}",
                "nearestCount": 5,
                "returnDistance": true,
                "returnGeometry": true,
                "f": "json"
            }
            """;
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query", content);

        // Assert
        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            responseContent, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
        queryResponse.Features.Length.Should().BeLessThanOrEqualTo(5);
    }

    /// <summary>
    /// Tests that KNN query without geometry returns 400 error
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_NearestNeighborWithoutGeometry_Returns400()
    {
        // Arrange - Missing geometry parameter
        var nearestCount = 3;

        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
            $"?nearestCount={nearestCount}" +
            $"&f=json");

        // Assert
        response.Be400BadRequest();
    }

    #endregion

    #region Combined Filter Tests

    /// <summary>
    /// Tests combining distance-based spatial query with WHERE clause attribute filter
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_DistanceWithWhereClause_ReturnsFilteredNearbyFeatures()
    {
        // Arrange
        var pointGeometry = @"{""x"":-122.4194,""y"":37.7749}";
        var whereClause = "name='Test Feature'";

        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
            $"?geometry={Uri.EscapeDataString(pointGeometry)}" +
            $"&spatialRel=esriSpatialRelWithinDistance" +
            $"&distance=100000" +
            $"&units=esriSRUnit_Meter" +
            $"&where={Uri.EscapeDataString(whereClause)}" +
            $"&f=json");

        // Assert
        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
    }

    /// <summary>
    /// Tests combining KNN query with pagination
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_NearestNeighborWithPagination_ReturnsPagedResults()
    {
        // Arrange
        var pointGeometry = @"{""x"":-122.4194,""y"":37.7749}";

        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
            $"?geometry={Uri.EscapeDataString(pointGeometry)}" +
            $"&nearestCount=10" +
            $"&resultOffset=2" +
            $"&f=json");

        // Assert
        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
    }

    #endregion

    #region GeoJSON Format Tests

    /// <summary>
    /// Tests that distance-based queries return proper GeoJSON format
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_DistanceWithGeoJsonFormat_ReturnsGeoJsonFeatures()
    {
        // Arrange
        var pointGeometry = @"{""x"":-122.4194,""y"":37.7749}";

        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
            $"?geometry={Uri.EscapeDataString(pointGeometry)}" +
            $"&spatialRel=esriSpatialRelWithinDistance" +
            $"&distance=100000" +
            $"&units=meters" +
            $"&f=geojson");

        // Assert
        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/geo+json");

        var content = await response.Content.ReadAsStringAsync();
        var geoJsonResponse = JsonSerializer.Deserialize<GeoJsonFeatureSet>(
            content, FeatureServerJsonContext.Default.GeoJsonFeatureSet);

        geoJsonResponse.Should().NotBeNull();
        geoJsonResponse!.Type.Should().Be("FeatureCollection");
        geoJsonResponse.Features.Should().NotBeNull();
    }

    /// <summary>
    /// Tests that KNN queries return proper GeoJSON format
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_NearestNeighborWithGeoJsonFormat_ReturnsGeoJsonFeatures()
    {
        // Arrange
        var pointGeometry = @"{""x"":-122.4194,""y"":37.7749}";

        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
            $"?geometry={Uri.EscapeDataString(pointGeometry)}" +
            $"&nearestCount=5" +
            $"&returnDistance=true" +
            $"&f=geojson");

        // Assert
        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/geo+json");

        var content = await response.Content.ReadAsStringAsync();
        var geoJsonResponse = JsonSerializer.Deserialize<GeoJsonFeatureSet>(
            content, FeatureServerJsonContext.Default.GeoJsonFeatureSet);

        geoJsonResponse.Should().NotBeNull();
        geoJsonResponse!.Type.Should().Be("FeatureCollection");
        geoJsonResponse.Features.Should().NotBeNull();
    }

    #endregion

    #region Unit Support Tests

    /// <summary>
    /// Tests that simple unit names (meters, feet, km, miles) are properly supported
    /// </summary>
    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("meters", 10000)]
    [InlineData("m", 10000)]
    [InlineData("feet", 32808)]
    [InlineData("ft", 32808)]
    [InlineData("kilometers", 10)]
    [InlineData("km", 10)]
    [InlineData("miles", 6)]
    [InlineData("mi", 6)]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithSimpleUnitNames_ReturnsValidResponse(string unit, double distance)
    {
        // Arrange
        var pointGeometry = @"{""x"":-122.4194,""y"":37.7749}";

        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
            $"?geometry={Uri.EscapeDataString(pointGeometry)}" +
            $"&spatialRel=esriSpatialRelWithinDistance" +
            $"&distance={distance}" +
            $"&units={unit}" +
            $"&f=json");

        // Assert
        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
    }

    #endregion
}
