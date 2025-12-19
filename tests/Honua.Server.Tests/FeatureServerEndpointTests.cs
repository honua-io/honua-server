// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
        content.Should().Contain("Service 'nonexistent' not found");
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
        content.Should().Contain("Service 'nonexistent' not found");
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
    public async Task GetServiceMetadata_ResponseValidatesAgainstEsriSchema()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer");

        // Assert
        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var serviceResponse = JsonSerializer.Deserialize<FeatureServerResponse>(
            content, FeatureServerJsonContext.Default.FeatureServerResponse);

        // Validate Esri JSON schema compliance
        serviceResponse.Should().NotBeNull();

        // Required Esri service properties
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
    public async Task GetLayerMetadata_ResponseValidatesAgainstEsriSchema()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}");

        // Assert
        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var layerResponse = JsonSerializer.Deserialize<LayerResponse>(
            content, FeatureServerJsonContext.Default.LayerResponse);

        // Validate Esri JSON schema compliance for layer metadata
        layerResponse.Should().NotBeNull();

        // Required Esri layer properties
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
        var queryParams = new QueryParameters
        {
            Where = "name='Test Feature'",
            ReturnGeometry = true,
            F = "json"
        };

        var json = JsonSerializer.Serialize(queryParams, FeatureServerJsonContext.Default.QueryParameters);
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
        content.Should().Contain("Service 'nonexistent' not found");
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
}
