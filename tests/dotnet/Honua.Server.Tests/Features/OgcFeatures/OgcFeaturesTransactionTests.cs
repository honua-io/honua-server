// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.Ogc.Common;
using Honua.Server.Features.OgcFeatures;
using Honua.Server.Features.OgcFeatures.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.OgcFeatures;

[Collection("Database")]
[Protocol(Protocols.OgcApiFeatures)]
public sealed class OgcFeaturesTransactionTests : IAsyncLifetime, IDisposable
{
    private readonly WebAppFixture _fixture = new();
    private const int TestLayerId = 0;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items")]
    public async Task CreateFeature_WithValidGeoJson_ReturnsCreated()
    {
        var feature = new GeoJsonFeature
        {
            Type = "Feature",
            Geometry = new SimpleGeoJsonGeometry
            {
                Type = "Point",
                CoordinatesJson = "[-122.4194, 37.7749]"
            },
            Properties = new Dictionary<string, object?>
            {
                ["name"] = "Created Feature"
            }
        };

        var json = JsonSerializer.Serialize(feature, OgcJsonContext.Default.GeoJsonFeature);
        var response = await _fixture.Client.PostAsync(
            $"/ogc/features/collections/{TestLayerId}/items",
            new StringContent(json, Encoding.UTF8, MediaTypes.GeoJson));

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var responseContent = await response.Content.ReadAsStringAsync();
        var created = JsonSerializer.Deserialize(responseContent, OgcJsonContext.Default.GeoJsonFeature);
        created.Should().NotBeNull();
        created!.Id.Should().NotBeNull();
        created.Properties.Should().ContainKey("name");
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task UpdateFeature_WithValidGeoJson_ReturnsUpdated()
    {
        var existingId = await _fixture.InsertFeatureAsync(TestLayerId, "Original");

        var feature = new GeoJsonFeature
        {
            Type = "Feature",
            Id = existingId,
            Geometry = new SimpleGeoJsonGeometry
            {
                Type = "Point",
                CoordinatesJson = "[-122.4194, 37.7749]"
            },
            Properties = new Dictionary<string, object?>
            {
                ["name"] = "Updated Feature"
            }
        };

        var json = JsonSerializer.Serialize(feature, OgcJsonContext.Default.GeoJsonFeature);
        var response = await _fixture.Client.PutAsync(
            $"/ogc/features/collections/{TestLayerId}/items/{existingId}",
            new StringContent(json, Encoding.UTF8, MediaTypes.GeoJson));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        var updated = JsonSerializer.Deserialize(responseContent, OgcJsonContext.Default.GeoJsonFeature);
        updated.Should().NotBeNull();
        updated!.Id.Should().Be(existingId);
        updated.Properties.Should().ContainKey("name");
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task PatchFeature_WithPropertiesOnly_ReturnsUpdatedFeatureAndPreservesGeometry()
    {
        var createFeature = new GeoJsonFeature
        {
            Type = "Feature",
            Geometry = new SimpleGeoJsonGeometry
            {
                Type = "Point",
                CoordinatesJson = "[-122.4194, 37.7749]"
            },
            Properties = new Dictionary<string, object?>
            {
                ["name"] = "Patch Original"
            }
        };

        var createJson = JsonSerializer.Serialize(createFeature, OgcJsonContext.Default.GeoJsonFeature);
        var createResponse = await _fixture.Client.PostAsync(
            $"/ogc/features/collections/{TestLayerId}/items",
            new StringContent(createJson, Encoding.UTF8, MediaTypes.GeoJson));

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var createdContent = await createResponse.Content.ReadAsStringAsync();
        var created = JsonSerializer.Deserialize(createdContent, OgcJsonContext.Default.GeoJsonFeature);
        created.Should().NotBeNull();
        created!.Id.Should().NotBeNull();

        var patchJson = """
        {
            "properties": {
                "name": "Patch Updated"
            }
        }
        """;

        using var patchRequest = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/ogc/features/collections/{TestLayerId}/items/{created.Id}")
        {
            Content = new StringContent(patchJson, Encoding.UTF8, "application/merge-patch+json")
        };

        var patchResponse = await _fixture.Client.SendAsync(patchRequest);
        patchResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var patchedContent = await patchResponse.Content.ReadAsStringAsync();
        var patched = JsonSerializer.Deserialize(patchedContent, OgcJsonContext.Default.GeoJsonFeature);
        patched.Should().NotBeNull();
        patched!.Id.Should().Be(created.Id);
        patched.Properties["name"]!.ToString().Should().Be("Patch Updated");
        patched.Geometry.Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task PatchFeature_WithInvalidPropertiesShape_ReturnsBadRequest()
    {
        var existingId = await _fixture.InsertFeatureAsync(TestLayerId, "Invalid Patch");

        var patchJson = """
        {
            "properties": "not-an-object"
        }
        """;

        using var patchRequest = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/ogc/features/collections/{TestLayerId}/items/{existingId}")
        {
            Content = new StringContent(patchJson, Encoding.UTF8, "application/merge-patch+json")
        };

        var response = await _fixture.Client.SendAsync(patchRequest);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Delete)]
    [Endpoint("DELETE /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task DeleteFeature_WithValidId_ReturnsNoContent()
    {
        var existingId = await _fixture.InsertFeatureAsync(TestLayerId, "Delete Me");

        var response = await _fixture.Client.DeleteAsync(
            $"/ogc/features/collections/{TestLayerId}/items/{existingId}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items")]
    public async Task CreateFeature_WhenEventPublishFails_ReturnsCreated()
    {
        await using var fixture = new WebAppFixture()
            .ReplaceService<IFeatureChangeEventPublisher>(new ThrowingFeatureChangeEventPublisher());
        await fixture.InitializeAsync();

        var feature = new GeoJsonFeature
        {
            Type = "Feature",
            Geometry = new SimpleGeoJsonGeometry
            {
                Type = "Point",
                CoordinatesJson = "[-122.4194, 37.7749]"
            },
            Properties = new Dictionary<string, object?>
            {
                ["name"] = "Created Despite Publish Failure"
            }
        };

        var json = JsonSerializer.Serialize(feature, OgcJsonContext.Default.GeoJsonFeature);
        var response = await fixture.Client.PostAsync(
            $"/ogc/features/collections/{TestLayerId}/items",
            new StringContent(json, Encoding.UTF8, MediaTypes.GeoJson));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [IntegrationTest]
    [Operation(Operations.Delete)]
    [Endpoint("DELETE /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task DeleteFeature_WhenEventPublishFails_ReturnsNoContent()
    {
        await using var fixture = new WebAppFixture()
            .ReplaceService<IFeatureChangeEventPublisher>(new ThrowingFeatureChangeEventPublisher());
        await fixture.InitializeAsync();

        var existingId = await fixture.InsertFeatureAsync(TestLayerId, "Delete Despite Publish Failure");

        var response = await fixture.Client.DeleteAsync(
            $"/ogc/features/collections/{TestLayerId}/items/{existingId}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task UpdateFeature_WhenEventPublishFails_ReturnsUpdated()
    {
        await using var fixture = new WebAppFixture()
            .ReplaceService<IFeatureChangeEventPublisher>(new ThrowingFeatureChangeEventPublisher());
        await fixture.InitializeAsync();

        var existingId = await fixture.InsertFeatureAsync(TestLayerId, "Original");
        var feature = new GeoJsonFeature
        {
            Type = "Feature",
            Id = existingId,
            Geometry = new SimpleGeoJsonGeometry
            {
                Type = "Point",
                CoordinatesJson = "[-122.4194, 37.7749]"
            },
            Properties = new Dictionary<string, object?>
            {
                ["name"] = "Updated Despite Publish Failure"
            }
        };

        var json = JsonSerializer.Serialize(feature, OgcJsonContext.Default.GeoJsonFeature);
        var response = await fixture.Client.PutAsync(
            $"/ogc/features/collections/{TestLayerId}/items/{existingId}",
            new StringContent(json, Encoding.UTF8, MediaTypes.GeoJson));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task PatchFeature_WhenEventPublishFails_ReturnsUpdated()
    {
        await using var fixture = new WebAppFixture()
            .ReplaceService<IFeatureChangeEventPublisher>(new ThrowingFeatureChangeEventPublisher());
        await fixture.InitializeAsync();

        var existingId = await fixture.InsertFeatureAsync(TestLayerId, "Patch Despite Publish Failure");

        using var patchRequest = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/ogc/features/collections/{TestLayerId}/items/{existingId}")
        {
            Content = new StringContent(
                """
                {
                    "properties": {
                        "name": "Patched Despite Publish Failure"
                    }
                }
                """,
                Encoding.UTF8,
                "application/merge-patch+json")
        };

        var response = await fixture.Client.SendAsync(patchRequest);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed class ThrowingFeatureChangeEventPublisher : IFeatureChangeEventPublisher
    {
        public Task PublishAsync(FeatureChangeEventRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("publish failed");
    }

}
