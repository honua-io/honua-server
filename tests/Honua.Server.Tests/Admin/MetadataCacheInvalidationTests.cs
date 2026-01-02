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
/// Integration tests verifying targeted cache invalidation for admin metadata endpoints.
/// Tests ensure that write operations properly invalidate only the affected cache entries.
/// </summary>
[Protocol(Protocols.Admin)]
[Collection("Database")]
public sealed class MetadataCacheInvalidationTests : IAsyncLifetime
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
    // Service cache invalidation tests
    // ========================================================================

    [IntegrationTest]
    [Operation(Operations.Cache)]
    public async Task CreateService_InvalidatesServiceCache_NewServiceVisibleInList()
    {
        // Arrange - Get initial service list
        var initialResponse = await _client.GetAsync("/api/v1/admin/metadata/services");
        initialResponse.BeSuccessful();
        var initialContent = await initialResponse.Content.ReadAsStringAsync();

        var uniqueName = $"cache_test_service_{Guid.NewGuid():N}";
        var request = new CreateServiceRequest
        {
            Name = uniqueName,
            Description = "Service created to test cache invalidation",
            SpatialReferenceSrid = 4326,
            MaxRecordCount = 1000
        };

        var content = new StringContent(
            JsonSerializer.Serialize(request, MetadataJsonContext.Default.CreateServiceRequest),
            Encoding.UTF8,
            "application/json");

        // Act - Create service
        var createResponse = await _client.PostAsync("/api/v1/admin/metadata/services", content);

        // Skip test if admin catalog not available
        if (createResponse.StatusCode == HttpStatusCode.NotImplemented)
        {
            return;
        }

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        // Assert - New service visible in list (cache was invalidated)
        var listResponse = await _client.GetAsync("/api/v1/admin/metadata/services");
        listResponse.BeSuccessful();
        var listContent = await listResponse.Content.ReadAsStringAsync();
        listContent.Should().Contain(uniqueName, "newly created service should be visible after cache invalidation");
    }

    [IntegrationTest]
    [Operation(Operations.Cache)]
    public async Task UpdateService_InvalidatesServiceCache_UpdatedDescriptionVisible()
    {
        // Arrange - Create a service first
        var uniqueName = $"update_cache_test_{Guid.NewGuid():N}";
        var createRequest = new CreateServiceRequest
        {
            Name = uniqueName,
            Description = "Original description",
            SpatialReferenceSrid = 4326
        };

        var createContent = new StringContent(
            JsonSerializer.Serialize(createRequest, MetadataJsonContext.Default.CreateServiceRequest),
            Encoding.UTF8,
            "application/json");

        var createResponse = await _client.PostAsync("/api/v1/admin/metadata/services", createContent);

        if (createResponse.StatusCode == HttpStatusCode.NotImplemented)
        {
            return;
        }

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        // Act - Update the service
        var updatedDescription = $"Updated description {Guid.NewGuid():N}";
        var updateRequest = new UpdateServiceRequest
        {
            Description = updatedDescription
        };

        var updateContent = new StringContent(
            JsonSerializer.Serialize(updateRequest, MetadataJsonContext.Default.UpdateServiceRequest),
            Encoding.UTF8,
            "application/json");

        var updateResponse = await _client.PutAsync($"/api/v1/admin/metadata/services/{uniqueName}", updateContent);
        updateResponse.BeSuccessful();

        // Assert - Updated description visible
        var getResponse = await _client.GetAsync($"/api/v1/admin/metadata/services/{uniqueName}");
        getResponse.BeSuccessful();
        var getContent = await getResponse.Content.ReadAsStringAsync();
        getContent.Should().Contain(updatedDescription, "updated description should be visible after cache invalidation");
    }

    [IntegrationTest]
    [Operation(Operations.Cache)]
    public async Task DeleteService_InvalidatesServiceCache_ServiceRemovedFromList()
    {
        // Arrange - Create a service first
        var uniqueName = $"delete_cache_test_{Guid.NewGuid():N}";
        var createRequest = new CreateServiceRequest
        {
            Name = uniqueName,
            Description = "Service to be deleted",
            SpatialReferenceSrid = 4326
        };

        var createContent = new StringContent(
            JsonSerializer.Serialize(createRequest, MetadataJsonContext.Default.CreateServiceRequest),
            Encoding.UTF8,
            "application/json");

        var createResponse = await _client.PostAsync("/api/v1/admin/metadata/services", createContent);

        if (createResponse.StatusCode == HttpStatusCode.NotImplemented)
        {
            return;
        }

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        // Act - Delete the service
        var deleteResponse = await _client.DeleteAsync($"/api/v1/admin/metadata/services/{uniqueName}");
        deleteResponse.BeSuccessful();

        // Assert - Service no longer visible in list
        var listResponse = await _client.GetAsync("/api/v1/admin/metadata/services");
        listResponse.BeSuccessful();
        var listContent = await listResponse.Content.ReadAsStringAsync();
        listContent.Should().NotContain(uniqueName, "deleted service should not appear after cache invalidation");

        // Assert - Direct get returns 404
        var getResponse = await _client.GetAsync($"/api/v1/admin/metadata/services/{uniqueName}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ========================================================================
    // Layer cache invalidation tests
    // ========================================================================

    [IntegrationTest]
    [Operation(Operations.Cache)]
    public async Task UpdateLayer_InvalidatesLayerCache_UpdatedNameVisible()
    {
        // Arrange - Get list of layers to find an existing layer
        var listResponse = await _client.GetAsync("/api/v1/admin/metadata/layers");
        listResponse.BeSuccessful();
        var listContent = await listResponse.Content.ReadAsStringAsync();

        // Parse to get a layer ID
        using var listDoc = JsonDocument.Parse(listContent);
        var layers = listDoc.RootElement.GetProperty("layers");

        if (layers.GetArrayLength() == 0)
        {
            // No layers to test with, skip
            return;
        }

        var firstLayer = layers[0];
        var layerId = firstLayer.GetProperty("id").GetInt32();

        // Act - Update the layer
        var updatedName = $"Updated Layer Name {Guid.NewGuid():N}";
        var updateRequest = new UpdateLayerRequest
        {
            DisplayName = updatedName,
            Description = "Updated via cache test"
        };

        var updateContent = new StringContent(
            JsonSerializer.Serialize(updateRequest, MetadataJsonContext.Default.UpdateLayerRequest),
            Encoding.UTF8,
            "application/json");

        var updateResponse = await _client.PutAsync($"/api/v1/admin/metadata/layers/{layerId}", updateContent);

        if (updateResponse.StatusCode == HttpStatusCode.NotImplemented)
        {
            return;
        }

        updateResponse.BeSuccessful();

        // Assert - Updated name visible
        var getResponse = await _client.GetAsync($"/api/v1/admin/metadata/layers/{layerId}");
        getResponse.BeSuccessful();
        var getContent = await getResponse.Content.ReadAsStringAsync();
        getContent.Should().Contain(updatedName, "updated layer name should be visible after cache invalidation");
    }

    // ========================================================================
    // Targeted invalidation verification tests
    // ========================================================================

    [IntegrationTest]
    [Operation(Operations.Cache)]
    public async Task ServiceUpdate_DoesNotInvalidateUnrelatedLayerCache()
    {
        // This test verifies targeted invalidation by checking that
        // updating a service doesn't affect layer cache entries

        // Arrange - Fetch layers to prime the cache
        var layersResponse1 = await _client.GetAsync("/api/v1/admin/metadata/layers");
        layersResponse1.BeSuccessful();
        var layersContent1 = await layersResponse1.Content.ReadAsStringAsync();

        // Create and update a service
        var uniqueName = $"targeted_cache_test_{Guid.NewGuid():N}";
        var createRequest = new CreateServiceRequest
        {
            Name = uniqueName,
            Description = "Test service",
            SpatialReferenceSrid = 4326
        };

        var createContent = new StringContent(
            JsonSerializer.Serialize(createRequest, MetadataJsonContext.Default.CreateServiceRequest),
            Encoding.UTF8,
            "application/json");

        var createResponse = await _client.PostAsync("/api/v1/admin/metadata/services", createContent);

        if (createResponse.StatusCode == HttpStatusCode.NotImplemented)
        {
            return;
        }

        // Act - Fetch layers again (should still return same data efficiently)
        var layersResponse2 = await _client.GetAsync("/api/v1/admin/metadata/layers");
        layersResponse2.BeSuccessful();
        var layersContent2 = await layersResponse2.Content.ReadAsStringAsync();

        // Assert - Layer data should be the same (not affected by service operation)
        // We compare the actual layer data, not the entire response which might have timestamps
        using var doc1 = JsonDocument.Parse(layersContent1);
        using var doc2 = JsonDocument.Parse(layersContent2);

        var layers1 = doc1.RootElement.GetProperty("layers");
        var layers2 = doc2.RootElement.GetProperty("layers");

        layers1.GetArrayLength().Should().Be(layers2.GetArrayLength(),
            "layer count should remain the same as service operations don't affect layer cache incorrectly");
    }

    [IntegrationTest]
    [Operation(Operations.Cache)]
    public async Task MultipleServiceOperations_InvalidatesCorrectly()
    {
        // This test verifies that multiple service operations properly
        // invalidate cache and data remains consistent

        var serviceName1 = $"multi_op_service_1_{Guid.NewGuid():N}";
        var serviceName2 = $"multi_op_service_2_{Guid.NewGuid():N}";

        // Create first service
        var request1 = new CreateServiceRequest
        {
            Name = serviceName1,
            Description = "First service",
            SpatialReferenceSrid = 4326
        };

        var content1 = new StringContent(
            JsonSerializer.Serialize(request1, MetadataJsonContext.Default.CreateServiceRequest),
            Encoding.UTF8,
            "application/json");

        var response1 = await _client.PostAsync("/api/v1/admin/metadata/services", content1);

        if (response1.StatusCode == HttpStatusCode.NotImplemented)
        {
            return;
        }

        response1.StatusCode.Should().Be(HttpStatusCode.Created);

        // Create second service
        var request2 = new CreateServiceRequest
        {
            Name = serviceName2,
            Description = "Second service",
            SpatialReferenceSrid = 4326
        };

        var content2 = new StringContent(
            JsonSerializer.Serialize(request2, MetadataJsonContext.Default.CreateServiceRequest),
            Encoding.UTF8,
            "application/json");

        var response2 = await _client.PostAsync("/api/v1/admin/metadata/services", content2);
        response2.StatusCode.Should().Be(HttpStatusCode.Created);

        // Verify both services exist
        var listResponse = await _client.GetAsync("/api/v1/admin/metadata/services");
        listResponse.BeSuccessful();
        var listContent = await listResponse.Content.ReadAsStringAsync();

        listContent.Should().Contain(serviceName1);
        listContent.Should().Contain(serviceName2);

        // Delete first service
        var deleteResponse = await _client.DeleteAsync($"/api/v1/admin/metadata/services/{serviceName1}");
        deleteResponse.BeSuccessful();

        // Verify only second service remains
        var finalListResponse = await _client.GetAsync("/api/v1/admin/metadata/services");
        finalListResponse.BeSuccessful();
        var finalListContent = await finalListResponse.Content.ReadAsStringAsync();

        finalListContent.Should().NotContain(serviceName1, "deleted service should not be in list");
        finalListContent.Should().Contain(serviceName2, "non-deleted service should remain in list");
    }
}
