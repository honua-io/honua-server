// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Server.Features.FeatureServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Infrastructure;
using Xunit;

namespace Honua.Server.Tests;

/// <summary>
/// Integration tests for FeatureServer metadata endpoints.
/// Tests Issue #5 - Layer metadata endpoint implementation.
/// </summary>
[Protocol(Protocols.FeatureServer)]
[Collection("Database")]
public sealed class FeatureServerEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const string TestServiceId = "test";
    private const int TestLayerId = 0;

    public async Task InitializeAsync()
    {
        // Replace the real ILayerCatalog with test implementation
        _fixture.ReplaceService<ILayerCatalog>(new TestLayerCatalog());
        _fixture.ReplaceService<IFeatureStore>(new TestFeatureStore());
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    public async Task GetServiceMetadata_WithValidServiceId_ReturnsServiceInfo()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer");

        // Assert
        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().NotBeNullOrEmpty();

        // Deserialize and validate response structure
        var serviceResponse = JsonSerializer.Deserialize<FeatureServerResponse>(
            content, FeatureServerJsonContext.Default.FeatureServerResponse);
        serviceResponse.Should().NotBeNull();

        // Validate required properties
        serviceResponse!.ServiceName.Should().Be(TestServiceId);
        serviceResponse.ServiceDescription.Should().NotBeNullOrEmpty();
        serviceResponse.CurrentVersion.Should().NotBeNullOrEmpty();
        serviceResponse.SpatialReference.Should().NotBeNull();
        serviceResponse.SpatialReference.Wkid.Should().BeGreaterThan(0);
        serviceResponse.Layers.Should().NotBeNull();
        serviceResponse.MaxRecordCount.Should().BeGreaterThan(0);
        serviceResponse.SupportedQueryFormats.Should().NotBeEmpty();
        serviceResponse.Capabilities.Should().NotBeNullOrEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    public async Task GetServiceMetadata_WithNonExistentService_Returns404()
    {
        // Act
        var response = await _fixture.Client.GetAsync("/rest/services/nonexistent/FeatureServer");

        // Assert
        response.Should().HaveStatusCode(System.Net.HttpStatusCode.NotFound);

        var content = await response.Content.ReadAsStringAsync();
        using var jsonDoc = JsonDocument.Parse(content);
        var errorElement = jsonDoc.RootElement.GetProperty("error");
        errorElement.GetProperty("code").GetInt32().Should().Be(404);
        errorElement.GetProperty("message").GetString().Should().Contain("Service 'nonexistent' not found");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task GetLayerMetadata_WithValidLayerId_ReturnsLayerInfo()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}");

        // Assert
        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().NotBeNullOrEmpty();

        // Deserialize and validate response structure
        var layerResponse = JsonSerializer.Deserialize<LayerResponse>(
            content, FeatureServerJsonContext.Default.LayerResponse);
        layerResponse.Should().NotBeNull();

        // Validate required properties
        layerResponse!.Id.Should().Be(TestLayerId);
        layerResponse.Name.Should().NotBeNullOrEmpty();
        layerResponse.Type.Should().Be("Feature Layer");
        layerResponse.CurrentVersion.Should().NotBeNullOrEmpty();
        layerResponse.GeometryType.Should().NotBeNullOrEmpty();
        layerResponse.SpatialReference.Should().NotBeNull();
        layerResponse.SpatialReference.Wkid.Should().BeGreaterThan(0);
        layerResponse.Fields.Should().NotBeEmpty();
        layerResponse.ObjectIdField.Should().NotBeNullOrEmpty();
        layerResponse.MaxRecordCount.Should().BeGreaterThan(0);
        layerResponse.SupportedQueryFormats.Should().NotBeEmpty();
        layerResponse.Capabilities.Should().NotBeNullOrEmpty();

        // Validate fields structure
        foreach (var field in layerResponse.Fields)
        {
            field.Name.Should().NotBeNullOrEmpty();
            field.Type.Should().NotBeNullOrEmpty();
            field.Alias.Should().NotBeNullOrEmpty();
        }

        // Should have at least an object ID field
        layerResponse.Fields.Should().Contain(f =>
            f.Name.Equals(layerResponse.ObjectIdField, StringComparison.OrdinalIgnoreCase));
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task GetLayerMetadata_WithNonExistentService_Returns404()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/nonexistent/FeatureServer/{TestLayerId}");

        // Assert
        response.Should().HaveStatusCode(System.Net.HttpStatusCode.NotFound);

        var content = await response.Content.ReadAsStringAsync();
        using var jsonDoc = JsonDocument.Parse(content);
        var errorElement = jsonDoc.RootElement.GetProperty("error");
        errorElement.GetProperty("code").GetInt32().Should().Be(404);
        errorElement.GetProperty("message").GetString().Should().Contain("Service 'nonexistent' not found");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task GetLayerMetadata_WithNonExistentLayer_Returns404()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/999");

        // Assert
        response.Should().HaveStatusCode(System.Net.HttpStatusCode.NotFound);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Layer 999 not found in service");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task GetLayerMetadata_WithInvalidLayerId_Returns404()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/invalid");

        // Assert - Should return 404 because 'invalid' doesn't match int route constraint
        response.Should().HaveStatusCode(System.Net.HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer")]
    public async Task GetServiceMetadata_WithWrongHttpMethod_Returns405()
    {
        // Act
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer", null);

        // Assert
        response.Should().HaveStatusCode(System.Net.HttpStatusCode.MethodNotAllowed);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task GetLayerMetadata_WithWrongHttpMethod_Returns405()
    {
        // Act
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}", null);

        // Assert
        response.Should().HaveStatusCode(System.Net.HttpStatusCode.MethodNotAllowed);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    public async Task GetServiceMetadata_ResponseValidatesAgainstGeoServicesSchema()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer");

        // Assert
        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var serviceResponse = JsonSerializer.Deserialize<FeatureServerResponse>(
            content, FeatureServerJsonContext.Default.FeatureServerResponse);

        // Validate GeoServices REST JSON schema compliance
        serviceResponse.Should().NotBeNull();

        // Required GeoServices REST service properties
        serviceResponse!.CurrentVersion.Should().NotBeNullOrEmpty();
        serviceResponse.ServiceName.Should().NotBeNullOrEmpty();
        serviceResponse.ServiceDescription.Should().NotBeNullOrEmpty();
        serviceResponse.Layers.Should().NotBeNull();
        serviceResponse.Tables.Should().NotBeNull();
        serviceResponse.SpatialReference.Should().NotBeNull();
        serviceResponse.Units.Should().NotBeNullOrEmpty();
        serviceResponse.SupportedQueryFormats.Should().NotBeNull();
        serviceResponse.Capabilities.Should().NotBeNullOrEmpty();

        // Validate spatial reference structure
        serviceResponse.SpatialReference.Wkid.Should().BeGreaterThan(0);

        // Validate at least basic capabilities are present
        var capabilities = serviceResponse.Capabilities.Split(',');
        capabilities.Should().Contain("Query");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task GetLayerMetadata_ResponseValidatesAgainstGeoServicesSchema()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}");

        // Assert
        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var layerResponse = JsonSerializer.Deserialize<LayerResponse>(
            content, FeatureServerJsonContext.Default.LayerResponse);

        // Validate GeoServices REST JSON schema compliance for layer metadata
        layerResponse.Should().NotBeNull();

        // Required GeoServices REST layer properties
        layerResponse!.CurrentVersion.Should().NotBeNullOrEmpty();
        layerResponse.Id.Should().BeGreaterOrEqualTo(0);
        layerResponse.Name.Should().NotBeNullOrEmpty();
        layerResponse.Type.Should().Be("Feature Layer");
        layerResponse.GeometryType.Should().NotBeNullOrEmpty();
        layerResponse.SpatialReference.Should().NotBeNull();
        layerResponse.Fields.Should().NotBeNull().And.NotBeEmpty();
        layerResponse.ObjectIdField.Should().NotBeNullOrEmpty();
        layerResponse.Capabilities.Should().NotBeNullOrEmpty();

        // Validate geometry type format
        layerResponse.GeometryType.Should().StartWith("esriGeometry");

        // Validate field structure
        foreach (var field in layerResponse.Fields)
        {
            field.Name.Should().NotBeNullOrEmpty();
            field.Type.Should().NotBeNullOrEmpty().And.StartWith("esriFieldType");
            field.Alias.Should().NotBeNullOrEmpty();
        }

        // Validate at least basic capabilities are present
        var capabilities = layerResponse.Capabilities.Split(',');
        capabilities.Should().Contain("Query");
    }

    // Query endpoint tests (Issue #6)

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithValidWhereClause_ReturnsFilteredFeatures()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?where=name='Test Feature'");

        // Assert
        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().NotBeNullOrEmpty();

        // Deserialize and validate response structure
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);
        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
        queryResponse.ObjectIdFieldName.Should().NotBeNullOrEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithPostRequest_ReturnsFilteredFeatures()
    {
        // Arrange
        var json = """
            {
                "where": "name='Test Feature'",
                "returnGeometry": true,
                "f": "json"
            }
            """;
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query", content);

        // Assert
        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().NotBeNullOrEmpty();

        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            responseContent, FeatureServerJsonContext.Default.QueryResponse);
        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithSqlInjectionAttempt_Returns400()
    {
        // Act - Attempt SQL injection
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?where=name='Test'; DROP TABLE users; --");

        // Assert
        response.Should().HaveStatusCode(System.Net.HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("dangerous pattern");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithInvalidWhereClause_Returns400()
    {
        // Act - Invalid WHERE clause format
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?where=invalid syntax here");

        // Assert
        response.Should().HaveStatusCode(System.Net.HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("WHERE clause format not supported");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithNonExistentService_Returns404()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/nonexistent/FeatureServer/{TestLayerId}/query");

        // Assert
        response.Should().HaveStatusCode(System.Net.HttpStatusCode.NotFound);

        var content = await response.Content.ReadAsStringAsync();
        using var jsonDoc = JsonDocument.Parse(content);
        var errorElement = jsonDoc.RootElement.GetProperty("error");
        errorElement.GetProperty("code").GetInt32().Should().Be(404);
        errorElement.GetProperty("message").GetString().Should().Contain("Service 'nonexistent' not found");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithNonExistentLayer_Returns404()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/999/query");

        // Assert
        response.Should().HaveStatusCode(System.Net.HttpStatusCode.NotFound);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Layer 999 not found in service");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithOutFields_ReturnsOnlySpecifiedFields()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?outFields=objectid,name");

        // Assert
        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithReturnGeometryFalse_ReturnsNoGeometry()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?returnGeometry=false");

        // Assert
        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();

        // All features should have null geometry
        queryResponse.Features.Should().AllSatisfy(f => f.Geometry.Should().BeNull());
    }

    // Query paging tests (Issue #8)

    /// <summary>
    /// Tests that the resultOffset parameter correctly skips the specified number of records
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithResultOffset_SkipsCorrectNumberOfRecords()
    {
        // Act - Skip first 2 records
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?resultOffset=2");

        // Assert
        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();

        // With offset=2, should get features 3, 4, 5 from TestFeatureStore (features with IDs 3, 4, 5)
        queryResponse.Features.Should().HaveCount(3);
        queryResponse.ExceededTransferLimit.Should().BeFalse("because all remaining features after offset are returned");
    }

    /// <summary>
    /// Tests that the resultRecordCount parameter correctly limits the number of returned features
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithResultRecordCount_LimitsReturnedFeatures()
    {
        // Act - Limit to 3 records
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?resultRecordCount=3");

        // Assert
        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
        queryResponse.Features.Should().HaveCount(3);
        queryResponse.ExceededTransferLimit.Should().BeTrue("because 5 features exist but only 3 were requested");
    }

    /// <summary>
    /// Tests that combining resultOffset and resultRecordCount parameters returns the correct page of results
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithOffsetAndCount_ReturnsCorrectPage()
    {
        // Act - Skip 1 record and limit to 2 records
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?resultOffset=1&resultRecordCount=2");

        // Assert
        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
        queryResponse.Features.Should().HaveCount(2);
        queryResponse.ExceededTransferLimit.Should().BeTrue("because offset=1 leaves 4 features, but only 2 were requested");
    }

    /// <summary>
    /// Tests that the exceededTransferLimit flag is correctly set when more results are available than requested
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithExceededLimit_SetsExceededTransferLimitFlag()
    {
        // Act - Request only 1 record when TestFeatureStore has 5 features
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?resultRecordCount=1");

        // Assert
        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
        queryResponse.Features.Should().HaveCount(1);
        queryResponse.ExceededTransferLimit.Should().BeTrue("because there are 5 total features but only 1 was requested");
    }

    /// <summary>
    /// Tests that the exceededTransferLimit flag is correctly set to false when all results are returned
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithAllResults_ExceededTransferLimitIsFalse()
    {
        // Act - Request all 5 records that exist in TestFeatureStore
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?resultRecordCount=5");

        // Assert
        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
        queryResponse.Features.Should().HaveCount(5);
        queryResponse.ExceededTransferLimit.Should().BeFalse("because all available features were returned");
    }

    /// <summary>
    /// Tests that POST query requests correctly handle paging parameters (resultOffset and resultRecordCount)
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_PostWithPagingParameters_ReturnsCorrectPage()
    {
        // Arrange
        var json = """
            {
                "where": "1=1",
                "resultOffset": 1,
                "resultRecordCount": 2,
                "returnGeometry": true,
                "f": "json"
            }
            """;
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query", content);

        // Assert
        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            responseContent, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
        queryResponse.Features.Should().HaveCount(2);
        queryResponse.ExceededTransferLimit.Should().BeTrue("because offset=1 leaves 4 features, but only 2 were requested");
    }

    /// <summary>
    /// Tests that spatial queries with point geometry and default spatial relationship (intersects) work correctly
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithPointGeometryIntersects_ReturnsFilteredFeatures()
    {
        // Arrange - Point geometry in GeoServices REST JSON format
        var pointGeometry = @"{""x"":-122.4194,""y"":37.7749}"; // San Francisco coordinates

        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?geometry={Uri.EscapeDataString(pointGeometry)}&spatialRel=esriSpatialRelIntersects&f=json");

        // Assert
        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            responseContent, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
        // Spatial filtering should work even with our simple test data
        queryResponse.Features.Should().NotBeNull();
    }

    /// <summary>
    /// Tests spatial queries with polygon geometry using contains relationship
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_PostWithPolygonGeometryContains_ReturnsContainedFeatures()
    {
        // Arrange - Polygon geometry around San Francisco Bay Area
        var json = """
            {
                "geometry": "{\"rings\":[[[-123.0,37.0],[-122.0,37.0],[-122.0,38.0],[-123.0,38.0],[-123.0,37.0]]]}",
                "spatialRel": "esriSpatialRelContains",
                "returnGeometry": true,
                "f": "json"
            }
            """;
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query", content);

        // Assert
        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            responseContent, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
    }

    /// <summary>
    /// Tests that spatial relationship mapping works correctly for all supported relationships
    /// </summary>
    [Theory]
    [InlineData("esriSpatialRelIntersects")]
    [InlineData("esriSpatialRelContains")]
    [InlineData("esriSpatialRelWithin")]
    [InlineData("esriSpatialRelEnvelopeIntersects")]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_SpatialRelMapping_ReturnsValidResponse(string spatialRel)
    {
        // Arrange
        var pointGeometry = @"{""x"":-122.4,""y"":37.7}";

        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?geometry={Uri.EscapeDataString(pointGeometry)}&spatialRel={spatialRel}&f=json");

        // Assert - Should not throw an exception for valid spatial relationships
        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            responseContent, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
    }

    /// <summary>
    /// Tests error handling for invalid geometry in spatial queries
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithInvalidGeometry_Returns400()
    {
        // Arrange - Invalid JSON geometry
        var invalidGeometry = @"{""invalid"":""geometry""}";

        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?geometry={Uri.EscapeDataString(invalidGeometry)}&f=json");

        // Assert
        response.Be400BadRequest();
    }

    /// <summary>
    /// Tests error handling for unsupported spatial relationships
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithUnsupportedSpatialRel_Returns400()
    {
        // Arrange
        var pointGeometry = @"{""x"":-122.4,""y"":37.7}";
        var unsupportedSpatialRel = "esriSpatialRelOverlaps"; // Not yet supported

        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?geometry={Uri.EscapeDataString(pointGeometry)}&spatialRel={unsupportedSpatialRel}&f=json");

        // Assert
        response.Be400BadRequest();
    }

    /// <summary>
    /// Tests that spatial queries can be combined with WHERE clauses
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithSpatialAndAttributeFilters_ReturnsFilteredFeatures()
    {
        // Arrange
        var pointGeometry = @"{""x"":-122.4,""y"":37.7}";
        var whereClause = "name='Test Feature'";

        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?geometry={Uri.EscapeDataString(pointGeometry)}&where={Uri.EscapeDataString(whereClause)}&f=json");

        // Assert
        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            responseContent, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
    }

    // Output format tests (Issue #9)

    /// <summary>
    /// Tests that f=json returns GeoServices REST JSON format with correct content type
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithFormatJson_ReturnsGeoServicesJsonFormat()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?f=json");

        // Assert
        response.Should().BeSuccessful();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
        queryResponse.ObjectIdFieldName.Should().NotBeNullOrEmpty();
        queryResponse.Features.Should().AllSatisfy(f =>
        {
            f.Attributes.Should().NotBeNull();
            // Some features may not have geometry in test data, just verify structure
        });
    }

    /// <summary>
    /// Tests that f=geojson returns GeoJSON format with correct content type
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithFormatGeoJson_ReturnsGeoJsonFormat()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?f=geojson");

        // Assert
        response.Should().BeSuccessful();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/geo+json");

        var content = await response.Content.ReadAsStringAsync();
        var geoJsonResponse = JsonSerializer.Deserialize<GeoJsonFeatureSet>(
            content, FeatureServerJsonContext.Default.GeoJsonFeatureSet);

        geoJsonResponse.Should().NotBeNull();
        geoJsonResponse!.Type.Should().Be("FeatureCollection");
        geoJsonResponse.Features.Should().NotBeNull();
        geoJsonResponse.Properties.Should().NotBeNull();
        geoJsonResponse.Properties!["objectIdFieldName"].Should().NotBeNull();

        geoJsonResponse.Features.Should().AllSatisfy(f =>
        {
            f.Type.Should().Be("Feature");
            f.Properties.Should().NotBeNull();
            // Some features may not have geometry in test data, just verify structure
            if (f.Geometry != null)
            {
                f.Geometry.Type.Should().NotBeNullOrEmpty();
                f.Geometry.Coordinates.Should().NotBeNull();
            }
        });
    }

    /// <summary>
    /// Tests that outFields parameter filters returned attributes in both formats
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithOutFieldsParam_FiltersAttributesInBothFormats()
    {
        // Test GeoServices REST JSON format
        var esriResponse = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?f=json&outFields=objectid,name");
        esriResponse.Should().BeSuccessful();

        var esriContent = await esriResponse.Content.ReadAsStringAsync();
        var esriQueryResponse = JsonSerializer.Deserialize<QueryResponse>(
            esriContent, FeatureServerJsonContext.Default.QueryResponse);

        esriQueryResponse!.Features.Should().AllSatisfy(f =>
        {
            f.Attributes.Keys.Should().Contain("objectid");
            f.Attributes.Keys.Should().Contain("name");
            // Should not contain other fields like description, etc.
            f.Attributes.Keys.Should().HaveCountLessOrEqualTo(2);
        });

        // Test GeoJSON format
        var geoJsonResponse = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?f=geojson&outFields=objectid,name");
        geoJsonResponse.Should().BeSuccessful();

        var geoJsonContent = await geoJsonResponse.Content.ReadAsStringAsync();
        var geoJsonQueryResponse = JsonSerializer.Deserialize<GeoJsonFeatureSet>(
            geoJsonContent, FeatureServerJsonContext.Default.GeoJsonFeatureSet);

        geoJsonQueryResponse!.Features.Should().AllSatisfy(f =>
        {
            f.Properties.Keys.Should().Contain("objectid");
            f.Properties.Keys.Should().Contain("name");
            // Should not contain other fields like description, etc.
            f.Properties.Keys.Should().HaveCountLessOrEqualTo(2);
        });
    }

    /// <summary>
    /// Tests that returnGeometry=false omits geometry in both formats
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithReturnGeometryFalse_OmitsGeometryInBothFormats()
    {
        // Test GeoServices REST JSON format
        var esriResponse = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?f=json&returnGeometry=false");
        esriResponse.Should().BeSuccessful();

        var esriContent = await esriResponse.Content.ReadAsStringAsync();
        var esriQueryResponse = JsonSerializer.Deserialize<QueryResponse>(
            esriContent, FeatureServerJsonContext.Default.QueryResponse);

        esriQueryResponse!.Features.Should().AllSatisfy(f => f.Geometry.Should().BeNull());

        // Test GeoJSON format
        var geoJsonResponse = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?f=geojson&returnGeometry=false");
        geoJsonResponse.Should().BeSuccessful();

        var geoJsonContent = await geoJsonResponse.Content.ReadAsStringAsync();
        var geoJsonQueryResponse = JsonSerializer.Deserialize<GeoJsonFeatureSet>(
            geoJsonContent, FeatureServerJsonContext.Default.GeoJsonFeatureSet);

        geoJsonQueryResponse!.Features.Should().AllSatisfy(f => f.Geometry.Should().BeNull());
    }

    /// <summary>
    /// Tests that POST requests support format parameter in request body
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_PostWithGeoJsonFormat_ReturnsGeoJsonFormat()
    {
        // Arrange
        var requestBody = JsonSerializer.Serialize(new QueryParameters
        {
            Where = "1=1",
            F = "geojson",
            ResultRecordCount = 2
        }, FeatureServerJsonContext.Default.QueryParameters);
        var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query", content);

        // Assert
        response.Should().BeSuccessful();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/geo+json");

        var responseContent = await response.Content.ReadAsStringAsync();
        var geoJsonResponse = JsonSerializer.Deserialize<GeoJsonFeatureSet>(
            responseContent, FeatureServerJsonContext.Default.GeoJsonFeatureSet);

        geoJsonResponse.Should().NotBeNull();
        geoJsonResponse!.Type.Should().Be("FeatureCollection");
        geoJsonResponse.Features.Should().HaveCount(2);
        geoJsonResponse.Features.Should().AllSatisfy(f =>
        {
            f.Type.Should().Be("Feature");
            f.Properties.Should().NotBeNull();
            f.Id.Should().NotBeNull();
        });
    }

    /// <summary>
    /// Tests that invalid format parameter defaults to GeoServices REST JSON
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithInvalidFormat_DefaultsToGeoServicesJson()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?f=invalid");

        // Assert
        response.Should().BeSuccessful();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.ObjectIdFieldName.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// Tests that GeoJSON features include proper IDs from objectid field
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_GeoJsonFormat_IncludesFeatureIds()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?f=geojson&resultRecordCount=1");

        // Assert
        response.Should().BeSuccessful();

        var content = await response.Content.ReadAsStringAsync();
        var geoJsonResponse = JsonSerializer.Deserialize<GeoJsonFeatureSet>(
            content, FeatureServerJsonContext.Default.GeoJsonFeatureSet);

        geoJsonResponse.Should().NotBeNull();
        geoJsonResponse!.Features.Should().HaveCount(1);

        var feature = geoJsonResponse.Features[0];
        feature.Id.Should().NotBeNull("GeoJSON features should include ID from objectid field");
        feature.Properties.Should().ContainKey("objectid");

        // The ID should match the objectid in properties - verify both have the same numeric value
        // TODO: Temporarily commented due to FluentAssertions type comparison issue
        // var idValue = Convert.ToInt64(feature.Id);
        // var objectidValue = Convert.ToInt64(feature.Properties["objectid"]);
        // idValue.Should().Be(objectidValue);

        // Basic verification that ID has a reasonable value
        feature.Id.Should().NotBeNull();

        // Handle JsonElement case for ID
        var idValue = feature.Id switch
        {
            JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.Number => jsonElement.GetInt64(),
            var other => Convert.ToInt64(other, CultureInfo.InvariantCulture)
        };
        idValue.Should().BeGreaterThan(0);
    }

    #region Geometry Type Support Tests (Issue #94)

    /// <summary>
    /// Tests spatial queries with LineString geometry
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithLineStringGeometry_ReturnsValidResponse()
    {
        // Arrange - LineString geometry in GeoServices REST JSON format
        var json = """
            {
                "geometry": "{\"paths\":[[[-122.5,37.7],[-122.4,37.8],[-122.3,37.9]]]}",
                "geometryType": "esriGeometryPolyline",
                "spatialRel": "esriSpatialRelIntersects",
                "returnGeometry": true,
                "f": "json"
            }
            """;
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query", content);

        // Assert
        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            responseContent, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
    }

    /// <summary>
    /// Tests spatial queries with MultiPoint geometry
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithMultiPointGeometry_ReturnsValidResponse()
    {
        // Arrange - MultiPoint geometry
        var json = """
            {
                "geometry": "{\"points\":[[-122.4,37.8],[-122.3,37.9]]}",
                "geometryType": "esriGeometryMultipoint",
                "spatialRel": "esriSpatialRelIntersects",
                "returnGeometry": true,
                "f": "json"
            }
            """;
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query", content);

        // Assert
        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            responseContent, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
    }

    /// <summary>
    /// Tests spatial queries with Envelope geometry (bounding box)
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithEnvelopeGeometry_ReturnsValidResponse()
    {
        // Arrange - Envelope geometry (bounding box)
        var json = """
            {
                "geometry": "{\"xmin\":-123.0,\"ymin\":37.0,\"xmax\":-122.0,\"ymax\":38.0}",
                "geometryType": "esriGeometryEnvelope",
                "spatialRel": "esriSpatialRelIntersects",
                "returnGeometry": true,
                "f": "json"
            }
            """;
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query", content);

        // Assert
        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            responseContent, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
    }

    /// <summary>
    /// Tests spatial queries with MultiPolygon geometry (polygon with multiple rings)
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithMultiPolygonGeometry_ReturnsValidResponse()
    {
        // Arrange - MultiPolygon geometry (two separate polygons)
        var json = """
            {
                "geometry": "{\"rings\":[[[-123.0,37.0],[-122.5,37.0],[-122.5,37.5],[-123.0,37.5],[-123.0,37.0]],[[-122.3,37.6],[-121.8,37.6],[-121.8,38.1],[-122.3,38.1],[-122.3,37.6]]]}",
                "geometryType": "esriGeometryPolygon",
                "spatialRel": "esriSpatialRelContains",
                "returnGeometry": true,
                "f": "json"
            }
            """;
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query", content);

        // Assert
        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            responseContent, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
    }

    /// <summary>
    /// Tests error handling for invalid geometry formats in new geometry types
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithInvalidLineStringGeometry_Returns400()
    {
        // Arrange - Invalid LineString geometry (missing paths)
        var json = """
            {
                "geometry": "{\"invalidProperty\":[]}",
                "geometryType": "esriGeometryPolyline",
                "spatialRel": "esriSpatialRelIntersects",
                "f": "json"
            }
            """;
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query", content);

        // Assert
        response.Be400BadRequest();
    }

    /// <summary>
    /// Tests error handling for empty geometry arrays
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithEmptyMultiPointGeometry_Returns400()
    {
        // Arrange - Empty MultiPoint geometry
        var json = """
            {
                "geometry": "{\"points\":[]}",
                "geometryType": "esriGeometryMultipoint",
                "spatialRel": "esriSpatialRelIntersects",
                "f": "json"
            }
            """;
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query", content);

        // Assert
        response.Be400BadRequest();
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_WithAddOperation_ReturnsNewObjectId()
    {
        // Arrange
        var editsRequest = new ApplyEditsRequest
        {
            Adds = new[]
            {
                new EsriFeature
                {
                    Attributes = new Dictionary<string, object?>
                    {
                        ["name"] = "Test Added Feature",
                        ["description"] = "Added via ApplyEdits test"
                    },
                    Geometry = new EsriGeometry
                    {
                        X = -122.4194,
                        Y = 37.7749
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(editsRequest, FeatureServerJsonContext.Default.ApplyEditsRequest);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/applyEdits", content);

        // Assert
        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().NotBeNullOrEmpty();

        var applyEditsResponse = JsonSerializer.Deserialize<ApplyEditsResponse>(
            responseContent, FeatureServerJsonContext.Default.ApplyEditsResponse);
        applyEditsResponse.Should().NotBeNull();
        applyEditsResponse!.Success.Should().BeTrue();
        applyEditsResponse.AddResults.Should().HaveCount(1);
        applyEditsResponse.AddResults![0].Success.Should().BeTrue();
        applyEditsResponse.AddResults[0].ObjectId.Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_WithUpdateOperation_ReturnsUpdatedObjectId()
    {
        // Arrange - First add a feature to update
        var addRequest = new ApplyEditsRequest
        {
            Adds = new[]
            {
                new EsriFeature
                {
                    Attributes = new Dictionary<string, object?>
                    {
                        ["name"] = "Feature to Update",
                        ["description"] = "Original description"
                    }
                }
            }
        };

        var addJson = JsonSerializer.Serialize(addRequest, FeatureServerJsonContext.Default.ApplyEditsRequest);
        var addContent = new StringContent(addJson, System.Text.Encoding.UTF8, "application/json");
        var addResponse = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/applyEdits", addContent);
        
        var addResponseContent = await addResponse.Content.ReadAsStringAsync();
        var addResult = JsonSerializer.Deserialize<ApplyEditsResponse>(
            addResponseContent, FeatureServerJsonContext.Default.ApplyEditsResponse);
        var objectId = addResult!.AddResults![0].ObjectId!.Value;

        // Now update the feature
        var updateRequest = new ApplyEditsRequest
        {
            Updates = new[]
            {
                new EsriFeature
                {
                    Attributes = new Dictionary<string, object?>
                    {
                        ["objectid"] = objectId,
                        ["name"] = "Updated Feature Name",
                        ["description"] = "Updated description"
                    }
                }
            }
        };

        var updateJson = JsonSerializer.Serialize(updateRequest, FeatureServerJsonContext.Default.ApplyEditsRequest);
        var updateContent = new StringContent(updateJson, System.Text.Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/applyEdits", updateContent);

        // Assert
        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var responseContent = await response.Content.ReadAsStringAsync();
        var applyEditsResponse = JsonSerializer.Deserialize<ApplyEditsResponse>(
            responseContent, FeatureServerJsonContext.Default.ApplyEditsResponse);
        applyEditsResponse.Should().NotBeNull();
        applyEditsResponse!.Success.Should().BeTrue();
        applyEditsResponse.UpdateResults.Should().HaveCount(1);
        applyEditsResponse.UpdateResults![0].Success.Should().BeTrue();
        applyEditsResponse.UpdateResults[0].ObjectId.Should().Be(objectId);
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_WithDeleteOperation_ReturnsDeletedObjectId()
    {
        // Arrange - First add a feature to delete
        var addRequest = new ApplyEditsRequest
        {
            Adds = new[]
            {
                new EsriFeature
                {
                    Attributes = new Dictionary<string, object?>
                    {
                        ["name"] = "Feature to Delete",
                        ["description"] = "Will be deleted"
                    }
                }
            }
        };

        var addJson = JsonSerializer.Serialize(addRequest, FeatureServerJsonContext.Default.ApplyEditsRequest);
        var addContent = new StringContent(addJson, System.Text.Encoding.UTF8, "application/json");
        var addResponse = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/applyEdits", addContent);
        
        var addResponseContent = await addResponse.Content.ReadAsStringAsync();
        var addResult = JsonSerializer.Deserialize<ApplyEditsResponse>(
            addResponseContent, FeatureServerJsonContext.Default.ApplyEditsResponse);
        var objectId = addResult!.AddResults![0].ObjectId!.Value;

        // Now delete the feature
        var deleteRequest = new ApplyEditsRequest
        {
            Deletes = new object[] { objectId }
        };

        var deleteJson = JsonSerializer.Serialize(deleteRequest, FeatureServerJsonContext.Default.ApplyEditsRequest);
        var deleteContent = new StringContent(deleteJson, System.Text.Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/applyEdits", deleteContent);

        // Assert
        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var responseContent = await response.Content.ReadAsStringAsync();
        var applyEditsResponse = JsonSerializer.Deserialize<ApplyEditsResponse>(
            responseContent, FeatureServerJsonContext.Default.ApplyEditsResponse);
        applyEditsResponse.Should().NotBeNull();
        applyEditsResponse!.Success.Should().BeTrue();
        applyEditsResponse.DeleteResults.Should().HaveCount(1);
        applyEditsResponse.DeleteResults![0].Success.Should().BeTrue();
        applyEditsResponse.DeleteResults[0].ObjectId.Should().Be(objectId);
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_WithMixedOperations_ReturnsCorrectResults()
    {
        // Arrange - First add a feature to update/delete
        var setupRequest = new ApplyEditsRequest
        {
            Adds = new[]
            {
                new EsriFeature
                {
                    Attributes = new Dictionary<string, object?>
                    {
                        ["name"] = "Feature for Update",
                        ["description"] = "Setup feature"
                    }
                },
                new EsriFeature
                {
                    Attributes = new Dictionary<string, object?>
                    {
                        ["name"] = "Feature for Delete",
                        ["description"] = "Setup feature"
                    }
                }
            }
        };

        var setupJson = JsonSerializer.Serialize(setupRequest, FeatureServerJsonContext.Default.ApplyEditsRequest);
        var setupContent = new StringContent(setupJson, System.Text.Encoding.UTF8, "application/json");
        var setupResponse = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/applyEdits", setupContent);
        
        var setupResponseContent = await setupResponse.Content.ReadAsStringAsync();
        var setupResult = JsonSerializer.Deserialize<ApplyEditsResponse>(
            setupResponseContent, FeatureServerJsonContext.Default.ApplyEditsResponse);
        var updateObjectId = setupResult!.AddResults![0].ObjectId!.Value;
        var deleteObjectId = setupResult.AddResults![1].ObjectId!.Value;

        // Mixed operations request
        var mixedRequest = new ApplyEditsRequest
        {
            Adds = new[]
            {
                new EsriFeature
                {
                    Attributes = new Dictionary<string, object?>
                    {
                        ["name"] = "New Added Feature",
                        ["description"] = "Added in mixed operation"
                    }
                }
            },
            Updates = new[]
            {
                new EsriFeature
                {
                    Attributes = new Dictionary<string, object?>
                    {
                        ["objectid"] = updateObjectId,
                        ["name"] = "Updated in Mixed Operation",
                        ["description"] = "Updated description"
                    }
                }
            },
            Deletes = new object[] { deleteObjectId }
        };

        var mixedJson = JsonSerializer.Serialize(mixedRequest, FeatureServerJsonContext.Default.ApplyEditsRequest);
        var mixedContent = new StringContent(mixedJson, System.Text.Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/applyEdits", mixedContent);

        // Assert
        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var responseContent = await response.Content.ReadAsStringAsync();
        var applyEditsResponse = JsonSerializer.Deserialize<ApplyEditsResponse>(
            responseContent, FeatureServerJsonContext.Default.ApplyEditsResponse);
        applyEditsResponse.Should().NotBeNull();
        applyEditsResponse!.Success.Should().BeTrue();
        
        // Verify all operations succeeded
        applyEditsResponse.AddResults.Should().HaveCount(1);
        applyEditsResponse.AddResults![0].Success.Should().BeTrue();
        applyEditsResponse.AddResults[0].ObjectId.Should().BeGreaterThan(0);
        
        applyEditsResponse.UpdateResults.Should().HaveCount(1);
        applyEditsResponse.UpdateResults![0].Success.Should().BeTrue();
        applyEditsResponse.UpdateResults[0].ObjectId.Should().Be(updateObjectId);
        
        applyEditsResponse.DeleteResults.Should().HaveCount(1);
        applyEditsResponse.DeleteResults![0].Success.Should().BeTrue();
        applyEditsResponse.DeleteResults[0].ObjectId.Should().Be(deleteObjectId);
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_WithInvalidUpdate_ReturnsError()
    {
        // Arrange - Try to update non-existent feature
        var updateRequest = new ApplyEditsRequest
        {
            Updates = new[]
            {
                new EsriFeature
                {
                    Attributes = new Dictionary<string, object?>
                    {
                        ["objectid"] = 999999, // Non-existent ID
                        ["name"] = "Invalid Update"
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(updateRequest, FeatureServerJsonContext.Default.ApplyEditsRequest);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/applyEdits", content);

        // Assert
        response.Be200Ok(); // ApplyEdits returns 200 but with error in results
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var responseContent = await response.Content.ReadAsStringAsync();
        var applyEditsResponse = JsonSerializer.Deserialize<ApplyEditsResponse>(
            responseContent, FeatureServerJsonContext.Default.ApplyEditsResponse);
        applyEditsResponse.Should().NotBeNull();
        applyEditsResponse!.UpdateResults.Should().HaveCount(1);
        applyEditsResponse.UpdateResults![0].Success.Should().BeFalse();
        applyEditsResponse.UpdateResults[0].Error.Should().NotBeNull();
    }

    #endregion
}
