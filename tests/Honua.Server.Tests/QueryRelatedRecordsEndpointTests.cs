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
/// Integration tests for FeatureServer queryRelatedRecords endpoint.
/// Tests Issue #14 - Related records query functionality implementation.
/// </summary>
[Protocol(Protocols.FeatureServer)]
[Collection("Database")]
public sealed class QueryRelatedRecordsEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const string TestServiceId = "test";
    private const int TestLayerId = 0;
    private const int TestRelationshipId = 1;

    public async Task InitializeAsync()
    {
        // Replace services with test implementations that support relationships
        _fixture.ReplaceService<ILayerCatalog>(new TestLayerCatalogWithRelationships())
                .ReplaceService<IFeatureStore>(new TestFeatureStoreWithRelationships());
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    #region Success Cases

    [IntegrationTest]
    [Operation(Operations.QueryRelatedRecords)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryRelatedRecords")]
    public async Task QueryRelatedRecords_WithValidParameters_ReturnsRelatedFeatures()
    {
        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/queryRelatedRecords?objectIds=1,2&relationshipId={TestRelationshipId}");

        // Assert
        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().NotBeNullOrEmpty();

        // Deserialize and validate response structure
        var queryResponse = JsonSerializer.Deserialize<QueryRelatedRecordsResponse>(
            content, FeatureServerJsonContext.Default.QueryRelatedRecordsResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.RelatedRecordGroups.Should().NotBeNull();
        queryResponse.RelatedRecordGroups.Should().HaveCount(2); // Two object IDs requested

        // Validate each related record group
        foreach (var group in queryResponse.RelatedRecordGroups)
        {
            group.ObjectId.Should().BeOneOf(1L, 2L);
            group.RelatedRecords.Should().NotBeNull();
            group.RelatedRecords!.Features.Should().NotBeNull();

            // Validate feature structure
            foreach (var feature in group.RelatedRecords.Features)
            {
                feature.Attributes.Should().NotBeNull();
                feature.Attributes.Should().ContainKey("objectid");
                feature.Geometry.Should().NotBeNull();
            }
        }
    }

    [IntegrationTest]
    [Operation(Operations.QueryRelatedRecords)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryRelatedRecords")]
    public async Task QueryRelatedRecords_WithSingleObjectId_ReturnsOneGroup()
    {
        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/queryRelatedRecords?objectIds=1&relationshipId={TestRelationshipId}");

        // Assert
        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryRelatedRecordsResponse>(
            content, FeatureServerJsonContext.Default.QueryRelatedRecordsResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.RelatedRecordGroups.Should().HaveCount(1);
        queryResponse.RelatedRecordGroups[0].ObjectId.Should().Be(1L);
    }

    [IntegrationTest]
    [Operation(Operations.QueryRelatedRecords)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryRelatedRecords")]
    public async Task QueryRelatedRecords_WithWhereClause_FiltersRelatedRecords()
    {
        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/queryRelatedRecords?objectIds=1&relationshipId={TestRelationshipId}&where=name='Related Feature 1'");

        // Assert
        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryRelatedRecordsResponse>(
            content, FeatureServerJsonContext.Default.QueryRelatedRecordsResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.RelatedRecordGroups.Should().HaveCount(1);

        var relatedRecords = queryResponse.RelatedRecordGroups[0].RelatedRecords;
        if (relatedRecords?.Features.Length > 0)
        {
            relatedRecords.Features.Should().AllSatisfy(f =>
                f.Attributes.Should().ContainKey("name"));
        }
    }

    [IntegrationTest]
    [Operation(Operations.QueryRelatedRecords)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryRelatedRecords")]
    public async Task QueryRelatedRecords_WithOutFields_ReturnsOnlySpecifiedFields()
    {
        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/queryRelatedRecords?objectIds=1&relationshipId={TestRelationshipId}&outFields=objectid,name");

        // Assert
        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryRelatedRecordsResponse>(
            content, FeatureServerJsonContext.Default.QueryRelatedRecordsResponse);

        queryResponse.Should().NotBeNull();
        var relatedRecords = queryResponse!.RelatedRecordGroups[0].RelatedRecords;

        if (relatedRecords?.Features.Length > 0)
        {
            relatedRecords.Features.Should().AllSatisfy(f =>
            {
                f.Attributes.Keys.Should().Contain("objectid");
                f.Attributes.Keys.Should().Contain("name");
                f.Attributes.Keys.Should().HaveCountLessOrEqualTo(2);
            });
        }
    }

    [IntegrationTest]
    [Operation(Operations.QueryRelatedRecords)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryRelatedRecords")]
    public async Task QueryRelatedRecords_WithReturnGeometryFalse_ReturnsNoGeometry()
    {
        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/queryRelatedRecords?objectIds=1&relationshipId={TestRelationshipId}&returnGeometry=false");

        // Assert
        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryRelatedRecordsResponse>(
            content, FeatureServerJsonContext.Default.QueryRelatedRecordsResponse);

        queryResponse.Should().NotBeNull();
        var relatedRecords = queryResponse!.RelatedRecordGroups[0].RelatedRecords;

        relatedRecords?.Features.Should().AllSatisfy(f => f.Geometry.Should().BeNull());
    }

    [IntegrationTest]
    [Operation(Operations.QueryRelatedRecords)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryRelatedRecords")]
    public async Task QueryRelatedRecords_WithResultRecordCount_LimitsResults()
    {
        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/queryRelatedRecords?objectIds=1&relationshipId={TestRelationshipId}&resultRecordCount=1");

        // Assert
        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryRelatedRecordsResponse>(
            content, FeatureServerJsonContext.Default.QueryRelatedRecordsResponse);

        queryResponse.Should().NotBeNull();
        var relatedRecords = queryResponse!.RelatedRecordGroups[0].RelatedRecords;

        relatedRecords?.Features.Should().HaveCountLessOrEqualTo(1);
    }

    [IntegrationTest]
    [Operation(Operations.QueryRelatedRecords)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryRelatedRecords")]
    public async Task QueryRelatedRecords_WithPostRequest_ReturnsRelatedFeatures()
    {
        // Arrange
        var requestBody = JsonSerializer.Serialize(new
        {
            objectIds = "1,2",
            relationshipId = TestRelationshipId,
            returnGeometry = true,
            f = "json"
        });
        var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/queryRelatedRecords", content);

        // Assert
        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var responseContent = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryRelatedRecordsResponse>(
            responseContent, FeatureServerJsonContext.Default.QueryRelatedRecordsResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.RelatedRecordGroups.Should().HaveCount(2);
    }

    #endregion

    #region Error Cases

    [IntegrationTest]
    [Operation(Operations.QueryRelatedRecords)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryRelatedRecords")]
    public async Task QueryRelatedRecords_WithMissingObjectIds_Returns400()
    {
        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/queryRelatedRecords?relationshipId={TestRelationshipId}");

        // Assert
        response.Be400BadRequest();

        var content = await response.Content.ReadAsStringAsync();
        using var jsonDoc = JsonDocument.Parse(content);
        var errorElement = jsonDoc.RootElement.GetProperty("error");
        errorElement.GetProperty("code").GetInt32().Should().Be(400);
        errorElement.GetProperty("message").GetString().Should().Contain("objectIds parameter is required");
    }

    [IntegrationTest]
    [Operation(Operations.QueryRelatedRecords)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryRelatedRecords")]
    public async Task QueryRelatedRecords_WithMissingRelationshipId_Returns400()
    {
        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/queryRelatedRecords?objectIds=1,2");

        // Assert
        response.Be400BadRequest();

        var content = await response.Content.ReadAsStringAsync();
        using var jsonDoc = JsonDocument.Parse(content);
        var errorElement = jsonDoc.RootElement.GetProperty("error");
        errorElement.GetProperty("code").GetInt32().Should().Be(400);
        errorElement.GetProperty("message").GetString().Should().Contain("relationshipId parameter is required");
    }

    [IntegrationTest]
    [Operation(Operations.QueryRelatedRecords)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryRelatedRecords")]
    public async Task QueryRelatedRecords_WithInvalidObjectIds_Returns400()
    {
        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/queryRelatedRecords?objectIds=invalid,abc&relationshipId={TestRelationshipId}");

        // Assert
        response.Be400BadRequest();

        var content = await response.Content.ReadAsStringAsync();
        using var jsonDoc = JsonDocument.Parse(content);
        var errorElement = jsonDoc.RootElement.GetProperty("error");
        errorElement.GetProperty("message").GetString().Should().Contain("Invalid objectId");
    }

    [IntegrationTest]
    [Operation(Operations.QueryRelatedRecords)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryRelatedRecords")]
    public async Task QueryRelatedRecords_WithInvalidRelationshipId_Returns400()
    {
        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/queryRelatedRecords?objectIds=1,2&relationshipId=invalid");

        // Assert
        response.Be400BadRequest();

        var content = await response.Content.ReadAsStringAsync();
        using var jsonDoc = JsonDocument.Parse(content);
        var errorElement = jsonDoc.RootElement.GetProperty("error");
        errorElement.GetProperty("message").GetString().Should().Contain("relationshipId must be an integer");
    }

    [IntegrationTest]
    [Operation(Operations.QueryRelatedRecords)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryRelatedRecords")]
    public async Task QueryRelatedRecords_WithNonExistentService_Returns404()
    {
        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/nonexistent/FeatureServer/{TestLayerId}/queryRelatedRecords?objectIds=1&relationshipId={TestRelationshipId}");

        // Assert
        response.Should().HaveStatusCode(System.Net.HttpStatusCode.NotFound);

        var content = await response.Content.ReadAsStringAsync();
        using var jsonDoc = JsonDocument.Parse(content);
        var errorElement = jsonDoc.RootElement.GetProperty("error");
        errorElement.GetProperty("code").GetInt32().Should().Be(404);
        errorElement.GetProperty("message").GetString().Should().Contain("Service 'nonexistent' not found");
    }

    [IntegrationTest]
    [Operation(Operations.QueryRelatedRecords)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryRelatedRecords")]
    public async Task QueryRelatedRecords_WithNonExistentLayer_Returns404()
    {
        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/999/queryRelatedRecords?objectIds=1&relationshipId={TestRelationshipId}");

        // Assert
        response.Should().HaveStatusCode(System.Net.HttpStatusCode.NotFound);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Layer 999 not found in service");
    }

    [IntegrationTest]
    [Operation(Operations.QueryRelatedRecords)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryRelatedRecords")]
    public async Task QueryRelatedRecords_WithNonExistentRelationship_Returns404()
    {
        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/queryRelatedRecords?objectIds=1&relationshipId=999");

        // Assert
        response.Should().HaveStatusCode(System.Net.HttpStatusCode.NotFound);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Relationship 999 not found for layer");
    }

    [IntegrationTest]
    [Operation(Operations.QueryRelatedRecords)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryRelatedRecords")]
    public async Task QueryRelatedRecords_WithExcessiveResultRecordCount_Returns400()
    {
        // Act - Request more than maximum allowed
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/queryRelatedRecords?objectIds=1&relationshipId={TestRelationshipId}&resultRecordCount=99999");

        // Assert
        response.Be400BadRequest();

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Query parameters exceed configured limits");
    }

    #endregion

    #region Format Validation

    [IntegrationTest]
    [Operation(Operations.QueryRelatedRecords)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryRelatedRecords")]
    public async Task QueryRelatedRecords_ResponseValidatesAgainstGeoServicesSchema()
    {
        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/queryRelatedRecords?objectIds=1&relationshipId={TestRelationshipId}");

        // Assert
        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryRelatedRecordsResponse>(
            content, FeatureServerJsonContext.Default.QueryRelatedRecordsResponse);

        // Validate GeoServices REST JSON schema compliance for related records
        queryResponse.Should().NotBeNull();
        queryResponse!.RelatedRecordGroups.Should().NotBeNull();

        foreach (var group in queryResponse.RelatedRecordGroups)
        {
            group.ObjectId.Should().BeGreaterOrEqualTo(0);

            if (group.RelatedRecords != null)
            {
                group.RelatedRecords.Features.Should().NotBeNull();

                foreach (var feature in group.RelatedRecords.Features)
                {
                    feature.Attributes.Should().NotBeNull();
                    feature.Attributes.Should().ContainKey("objectid");

                    if (feature.Geometry != null)
                    {
                        feature.Geometry.X.Should().NotBe(0);
                        feature.Geometry.Y.Should().NotBe(0);
                        feature.Geometry.SpatialReference.Should().NotBeNull();
                        feature.Geometry.SpatialReference!.Wkid.Should().BeGreaterThan(0);
                    }
                }
            }
        }
    }

    [IntegrationTest]
    [Operation(Operations.QueryRelatedRecords)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryRelatedRecords")]
    public async Task QueryRelatedRecords_WithNoRelatedFeatures_ReturnsEmptyGroups()
    {
        // Act - Request related records for an object ID that has no related features
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/queryRelatedRecords?objectIds=999&relationshipId={TestRelationshipId}");

        // Assert
        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryRelatedRecordsResponse>(
            content, FeatureServerJsonContext.Default.QueryRelatedRecordsResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.RelatedRecordGroups.Should().HaveCount(1);
        queryResponse.RelatedRecordGroups[0].ObjectId.Should().Be(999L);
        queryResponse.RelatedRecordGroups[0].RelatedRecords.Should().BeNull();
    }

    #endregion

    #region Security Tests

    [IntegrationTest]
    [Operation(Operations.QueryRelatedRecords)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryRelatedRecords")]
    public async Task QueryRelatedRecords_WithSqlInjectionAttempt_Returns400()
    {
        // Act - Attempt SQL injection in WHERE clause
        var maliciousWhere = Uri.EscapeDataString("name='test'; DROP TABLE users; --");
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/queryRelatedRecords?objectIds=1&relationshipId={TestRelationshipId}&where={maliciousWhere}");

        // Assert
        response.Be400BadRequest();

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("dangerous pattern");
    }

    [IntegrationTest]
    [Operation(Operations.QueryRelatedRecords)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryRelatedRecords")]
    public async Task QueryRelatedRecords_WithInvalidWhereClause_Returns400()
    {
        // Act - Invalid WHERE clause format
        var invalidWhere = Uri.EscapeDataString("invalid syntax here");
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/queryRelatedRecords?objectIds=1&relationshipId={TestRelationshipId}&where={invalidWhere}");

        // Assert
        response.Be400BadRequest();

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("WHERE clause format not supported");
    }

    #endregion

    #region HTTP Method Tests

    [IntegrationTest]
    [Operation(Operations.QueryRelatedRecords)]
    [Endpoint("PUT /rest/services/{serviceId}/FeatureServer/{layerId}/queryRelatedRecords")]
    public async Task QueryRelatedRecords_WithWrongHttpMethod_Returns405()
    {
        // Act
        var response = await _fixture.Client.PutAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/queryRelatedRecords", null);

        // Assert
        response.Should().HaveStatusCode(System.Net.HttpStatusCode.MethodNotAllowed);
    }

    [IntegrationTest]
    [Operation(Operations.QueryRelatedRecords)]
    [Endpoint("DELETE /rest/services/{serviceId}/FeatureServer/{layerId}/queryRelatedRecords")]
    public async Task QueryRelatedRecords_WithDeleteMethod_Returns405()
    {
        // Act
        var response = await _fixture.Client.DeleteAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/queryRelatedRecords");

        // Assert
        response.Should().HaveStatusCode(System.Net.HttpStatusCode.MethodNotAllowed);
    }

    #endregion
}
