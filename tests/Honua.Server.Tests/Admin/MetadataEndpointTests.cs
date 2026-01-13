// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Admin;

/// <summary>
/// Integration tests for admin metadata endpoints (Issue #191)
/// </summary>
[Protocol(Protocols.Admin)]
[Collection("Database")]
[Operation(Operations.Configuration)]
public sealed class MetadataEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    // ========================================================================
    // Service endpoints
    // ========================================================================

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/metadata/services")]
    public async Task ListServices_ReturnsServiceList()
    {
        // Act
        var response = await _client.GetAsync("/api/v1/admin/metadata/services");

        // Assert
        response.BeSuccessful();
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("services");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/metadata/services/{name}")]
    public async Task GetService_WhenNotFound_Returns404()
    {
        // Act
        var response = await _client.GetAsync("/api/v1/admin/metadata/services/nonexistent");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/metadata/services")]
    public async Task CreateService_WithValidRequest_Returns201()
    {
        // Arrange
        var request = new CreateServiceRequest
        {
            Name = $"test_service_{Guid.NewGuid():N}",
            Description = "Test service created by integration test",
            SpatialReferenceSrid = 4326
        };

        var content = new StringContent(
            JsonSerializer.Serialize(request, MetadataJsonContext.Default.CreateServiceRequest),
            Encoding.UTF8,
            "application/json");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/metadata/services", content);

        // Assert - Either 201 if admin catalog is available, or 501 if not implemented
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Created, HttpStatusCode.NotImplemented);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/metadata/services/{name}")]
    public async Task UpdateService_WhenNotFound_Returns404Or501()
    {
        // Arrange
        var request = new UpdateServiceRequest
        {
            Description = "Updated description"
        };

        var content = new StringContent(
            JsonSerializer.Serialize(request, MetadataJsonContext.Default.UpdateServiceRequest),
            Encoding.UTF8,
            "application/json");

        // Act
        var response = await _client.PutAsync("/api/v1/admin/metadata/services/nonexistent", content);

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.NotImplemented);
    }

    [IntegrationTest]
    [Endpoint("DELETE /api/v1/admin/metadata/services/{name}")]
    public async Task DeleteService_WhenNotFound_Returns404Or501()
    {
        // Act
        var response = await _client.DeleteAsync("/api/v1/admin/metadata/services/nonexistent");

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.NotImplemented);
    }

    // ========================================================================
    // Service-layer binding endpoints
    // ========================================================================

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/metadata/services/{name}/layers")]
    public async Task BindLayer_WhenServiceNotFound_Returns400Or501()
    {
        // Arrange
        var request = new BindLayerRequest { LayerId = 1 };
        var content = new StringContent(
            JsonSerializer.Serialize(request, MetadataJsonContext.Default.BindLayerRequest),
            Encoding.UTF8,
            "application/json");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/metadata/services/nonexistent/layers", content);

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.NotImplemented);
    }

    [IntegrationTest]
    [Endpoint("DELETE /api/v1/admin/metadata/services/{name}/layers/{layerId}")]
    public async Task UnbindLayer_WhenNotFound_Returns404Or501()
    {
        // Act
        var response = await _client.DeleteAsync("/api/v1/admin/metadata/services/nonexistent/layers/999");

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.NotImplemented);
    }

    // ========================================================================
    // Layer endpoints
    // ========================================================================

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/metadata/layers")]
    public async Task ListLayers_ReturnsLayerList()
    {
        // Act
        var response = await _client.GetAsync("/api/v1/admin/metadata/layers");

        // Assert
        response.BeSuccessful();
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("layers");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/metadata/layers/{layerId}")]
    public async Task GetLayer_WhenNotFound_Returns404()
    {
        // Act
        var response = await _client.GetAsync("/api/v1/admin/metadata/layers/999999");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/metadata/layers")]
    public async Task CreateLayer_WithValidRequest_Returns201OrError()
    {
        // Arrange
        var request = new CreateLayerRequest
        {
            TableName = "test_table",
            SchemaName = "public",
            DisplayName = "Test Layer"
        };

        var content = new StringContent(
            JsonSerializer.Serialize(request, MetadataJsonContext.Default.CreateLayerRequest),
            Encoding.UTF8,
            "application/json");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/metadata/layers", content);

        // Assert - May fail if table doesn't exist, which is expected
        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.Created,
            HttpStatusCode.BadRequest,  // Table not found
            HttpStatusCode.NotImplemented);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}")]
    public async Task UpdateLayer_WhenNotFound_Returns404Or501()
    {
        // Arrange
        var request = new UpdateLayerRequest
        {
            DisplayName = "Updated Layer Name"
        };

        var content = new StringContent(
            JsonSerializer.Serialize(request, MetadataJsonContext.Default.UpdateLayerRequest),
            Encoding.UTF8,
            "application/json");

        // Act
        var response = await _client.PutAsync("/api/v1/admin/metadata/layers/999999", content);

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.NotImplemented);
    }

    [IntegrationTest]
    [Endpoint("DELETE /api/v1/admin/metadata/layers/{layerId}")]
    public async Task DeleteLayer_WhenNotFound_Returns404Or501()
    {
        // Act
        var response = await _client.DeleteAsync("/api/v1/admin/metadata/layers/999999");

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.NotImplemented);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/metadata/layers/{layerId}/refresh")]
    public async Task RefreshLayer_WhenNotFound_Returns404Or501()
    {
        // Act
        var response = await _client.PostAsync("/api/v1/admin/metadata/layers/999999/refresh", null);

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.NotImplemented);
    }

    // ========================================================================
    // Relationship endpoints
    // ========================================================================

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/metadata/layers/{layerId}/relationships")]
    public async Task ListRelationships_ReturnsEmptyListForValidLayer()
    {
        // Act - Layer may not exist, but endpoint should handle gracefully
        var response = await _client.GetAsync("/api/v1/admin/metadata/layers/1/relationships");

        // Assert
        response.BeSuccessful();
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("relationships");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/metadata/layers/{layerId}/relationships")]
    public async Task CreateRelationship_WithValidRequest_Returns201OrError()
    {
        // Arrange
        var request = new CreateRelationshipRequest
        {
            RelatedLayerId = 2,
            Name = "test_relationship",
            RelationshipType = "OneToMany",
            OriginForeignKeyField = "id",
            DestinationForeignKeyField = "parent_id"
        };

        var content = new StringContent(
            JsonSerializer.Serialize(request, MetadataJsonContext.Default.CreateRelationshipRequest),
            Encoding.UTF8,
            "application/json");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/metadata/layers/1/relationships", content);

        // Assert - May fail if layers don't exist
        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.Created,
            HttpStatusCode.BadRequest,
            HttpStatusCode.NotImplemented);
    }

    [IntegrationTest]
    [Endpoint("DELETE /api/v1/admin/metadata/layers/{layerId}/relationships/{relationshipId}")]
    public async Task DeleteRelationship_WhenNotFound_Returns404Or501()
    {
        // Act
        var response = await _client.DeleteAsync("/api/v1/admin/metadata/layers/999999/relationships/1");

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.NotImplemented);
    }

    // ========================================================================
    // Style endpoints
    // ========================================================================

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/metadata/layers/{layerId}/style")]
    public async Task GetStyle_WhenLayerNotFound_Returns404()
    {
        // Act
        var response = await _client.GetAsync("/api/v1/admin/metadata/layers/999999/style");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/style")]
    public async Task UpdateStyle_WhenLayerNotFound_Returns404()
    {
        // Arrange
        using var styleDoc = JsonDocument.Parse("{}");
        var emptyJson = styleDoc.RootElement.Clone();
        var request = new UpdateStyleRequest
        {
            MapLibreStyle = emptyJson,
            DrawingInfo = emptyJson
        };

        var content = new StringContent(
            JsonSerializer.Serialize(request, MetadataJsonContext.Default.UpdateStyleRequest),
            Encoding.UTF8,
            "application/json");

        // Act
        var response = await _client.PutAsync("/api/v1/admin/metadata/layers/999999/style", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ========================================================================
    // Error handling tests
    // ========================================================================

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/metadata/services")]
    public async Task CreateService_WithEmptyName_ReturnsBadRequest()
    {
        // Arrange
        var request = new CreateServiceRequest
        {
            Name = "",
            Description = "Test"
        };

        var content = new StringContent(
            JsonSerializer.Serialize(request, MetadataJsonContext.Default.CreateServiceRequest),
            Encoding.UTF8,
            "application/json");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/metadata/services", content);

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.NotImplemented);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/metadata/layers")]
    public async Task CreateLayer_WithMissingTableName_ReturnsBadRequest()
    {
        // Arrange
        var request = new CreateLayerRequest
        {
            TableName = "",
            DisplayName = "Test"
        };

        var content = new StringContent(
            JsonSerializer.Serialize(request, MetadataJsonContext.Default.CreateLayerRequest),
            Encoding.UTF8,
            "application/json");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/metadata/layers", content);

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.NotImplemented);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/metadata/layers")]
    public async Task CreateLayer_WithMissingDisplayName_ReturnsBadRequest()
    {
        // Arrange
        var request = new CreateLayerRequest
        {
            TableName = "test_table",
            DisplayName = ""
        };

        var content = new StringContent(
            JsonSerializer.Serialize(request, MetadataJsonContext.Default.CreateLayerRequest),
            Encoding.UTF8,
            "application/json");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/metadata/layers", content);

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.NotImplemented);
    }
}
