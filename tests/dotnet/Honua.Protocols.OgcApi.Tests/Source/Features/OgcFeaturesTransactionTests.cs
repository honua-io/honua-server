// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Infrastructure.Events;
using Honua.Protocols.Ogc.Common;
using Honua.Protocols.Ogc.Api.Features;
using Honua.Protocols.Ogc.Api.Features.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

[Protocol(TestProtocols.OgcApiFeatures)]
[Collection("Database")]
public sealed class OgcFeaturesTransactionTests : IClassFixture<OgcFeaturesTransactionTestsFixture>
{
    private readonly WebAppFixture _fixture;
    private const int TestLayerId = 0;

    public OgcFeaturesTransactionTests(OgcFeaturesTransactionTestsFixture fixture)
    {
        _fixture = fixture.App;
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
        using var content = new StringContent(json, Encoding.UTF8, MediaTypes.GeoJson);
        var response = await _fixture.Client.PostAsync(
            $"/ogc/features/collections/{TestLayerId}/items",
            content);

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
        using var content = new StringContent(json, Encoding.UTF8, MediaTypes.GeoJson);
        var response = await _fixture.Client.PutAsync(
            $"/ogc/features/collections/{TestLayerId}/items/{existingId}",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        var updated = JsonSerializer.Deserialize(responseContent, OgcJsonContext.Default.GeoJsonFeature);
        updated.Should().NotBeNull();
        updated!.Id.Should().Be(existingId);
        updated.Properties.Should().ContainKey("name");
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items")]
    public async Task CreateFeature_WithUnsupportedContentType_ReturnsUnsupportedMediaType()
    {
        using var content = new StringContent("""{"type":"Feature","properties":{"name":"bad type"},"geometry":null}""", Encoding.UTF8, "text/plain");
        var response = await _fixture.Client.PostAsync(
            $"/ogc/features/collections/{TestLayerId}/items",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task UpdateFeature_WithUnsupportedContentType_ReturnsUnsupportedMediaType()
    {
        var existingId = await _fixture.InsertFeatureAsync(TestLayerId, "Original");

        using var content = new StringContent("""{"type":"Feature","properties":{"name":"bad type"},"geometry":null}""", Encoding.UTF8, "text/plain");
        var response = await _fixture.Client.PutAsync(
            $"/ogc/features/collections/{TestLayerId}/items/{existingId}",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task UpdateFeature_WithMismatchedPayloadId_ReturnsBadRequest()
    {
        var existingId = await _fixture.InsertFeatureAsync(TestLayerId, "Original");
        var feature = new GeoJsonFeature
        {
            Type = "Feature",
            Id = existingId + 1,
            Geometry = new SimpleGeoJsonGeometry
            {
                Type = "Point",
                CoordinatesJson = "[-122.4194, 37.7749]"
            },
            Properties = new Dictionary<string, object?>
            {
                ["name"] = "Mismatched ID"
            }
        };

        using var content = new StringContent(JsonSerializer.Serialize(feature, OgcJsonContext.Default.GeoJsonFeature), Encoding.UTF8, MediaTypes.GeoJson);
        var response = await _fixture.Client.PutAsync(
            $"/ogc/features/collections/{TestLayerId}/items/{existingId}",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items/{featureId}")]
    [Endpoint("PUT /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task UpdateFeature_WithGetItemEtagIfMatch_RoundTripsSuccessfully()
    {
        var existingId = await _fixture.InsertFeatureAsync(TestLayerId, "Original");

        var getResponse = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{TestLayerId}/items/{existingId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        getResponse.Headers.ETag.Should().NotBeNull();

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
                ["name"] = "Updated With ETag"
            }
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/ogc/features/collections/{TestLayerId}/items/{existingId}")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(feature, OgcJsonContext.Default.GeoJsonFeature),
                Encoding.UTF8,
                MediaTypes.GeoJson)
        };
        request.Headers.TryAddWithoutValidation("If-Match", getResponse.Headers.ETag!.ToString());

        var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        var updated = JsonSerializer.Deserialize(body, OgcJsonContext.Default.GeoJsonFeature);
        updated.Should().NotBeNull();
        updated!.Properties["name"]!.ToString().Should().Be("Updated With ETag");
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
        using var createContent = new StringContent(createJson, Encoding.UTF8, MediaTypes.GeoJson);
        var createResponse = await _fixture.Client.PostAsync(
            $"/ogc/features/collections/{TestLayerId}/items",
            createContent);

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
    public async Task PatchFeature_WithUnsupportedContentType_ReturnsUnsupportedMediaType()
    {
        var existingId = await _fixture.InsertFeatureAsync(TestLayerId, "Original");
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/ogc/features/collections/{TestLayerId}/items/{existingId}")
        {
            Content = new StringContent("""{"properties":{"name":"bad type"}}""", Encoding.UTF8, "text/plain")
        };

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items/{featureId}")]
    [Endpoint("PATCH /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task PatchFeature_WithGetItemEtagIfMatch_RoundTripsSuccessfully()
    {
        var existingId = await _fixture.InsertFeatureAsync(TestLayerId, "Patch Original");

        var getResponse = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{TestLayerId}/items/{existingId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        getResponse.Headers.ETag.Should().NotBeNull();

        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/ogc/features/collections/{TestLayerId}/items/{existingId}")
        {
            Content = new StringContent(
                """{"properties":{"name":"Patch With ETag"}}""",
                Encoding.UTF8,
                "application/merge-patch+json")
        };
        request.Headers.TryAddWithoutValidation("If-Match", getResponse.Headers.ETag!.ToString());

        var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        var patched = JsonSerializer.Deserialize(body, OgcJsonContext.Default.GeoJsonFeature);
        patched.Should().NotBeNull();
        patched!.Properties["name"]!.ToString().Should().Be("Patch With ETag");
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
        await using var fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro)
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
        using var content = new StringContent(json, Encoding.UTF8, MediaTypes.GeoJson);
        var response = await fixture.Client.PostAsync(
            $"/ogc/features/collections/{TestLayerId}/items",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [IntegrationTest]
    [Operation(Operations.Delete)]
    [Endpoint("DELETE /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task DeleteFeature_WhenEventPublishFails_ReturnsNoContent()
    {
        await using var fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro)
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
        await using var fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro)
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
        using var content = new StringContent(json, Encoding.UTF8, MediaTypes.GeoJson);
        var response = await fixture.Client.PutAsync(
            $"/ogc/features/collections/{TestLayerId}/items/{existingId}",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task PatchFeature_WhenEventPublishFails_ReturnsUpdated()
    {
        await using var fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro)
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

    // Regression tests for BH2-002: DELETE If-Match / optimistic-concurrency.
    // Before the fix HandleDeleteFeature ignored the If-Match header entirely, meaning a
    // DELETE would succeed unconditionally even when the ETag was stale (OGC API Features
    // Part 4 §7.3 requires 412 Precondition Failed in that case).

    [IntegrationTest]
    [Operation(Operations.Delete)]
    [Endpoint("DELETE /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task DeleteFeature_WithMatchingIfMatch_ReturnsNoContent()
    {
        var existingId = await _fixture.InsertFeatureAsync(TestLayerId, "Delete With ETag");

        // GET the feature to obtain its current ETag.
        var getResponse = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{TestLayerId}/items/{existingId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        getResponse.Headers.ETag.Should().NotBeNull();
        var etag = getResponse.Headers.ETag!.ToString();

        using var deleteRequest = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/ogc/features/collections/{TestLayerId}/items/{existingId}");
        deleteRequest.Headers.TryAddWithoutValidation("If-Match", etag);

        var response = await _fixture.Client.SendAsync(deleteRequest);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [IntegrationTest]
    [Operation(Operations.Delete)]
    [Endpoint("DELETE /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task DeleteFeature_WithStaleIfMatch_Returns412()
    {
        var existingId = await _fixture.InsertFeatureAsync(TestLayerId, "Delete Stale ETag");

        // GET the feature to capture the original ETag.
        var getResponse = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{TestLayerId}/items/{existingId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        getResponse.Headers.ETag.Should().NotBeNull();
        var staleEtag = getResponse.Headers.ETag!.ToString();

        // PUT an update to change the feature (and therefore its ETag).
        var updated = new GeoJsonFeature
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
                ["name"] = "Updated So ETag Changes"
            }
        };

        using var updateContent = new StringContent(
            JsonSerializer.Serialize(updated, OgcJsonContext.Default.GeoJsonFeature),
            Encoding.UTF8,
            MediaTypes.GeoJson);
        var putResponse = await _fixture.Client.PutAsync(
            $"/ogc/features/collections/{TestLayerId}/items/{existingId}",
            updateContent);
        putResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // DELETE with the now-stale ETag.  OGC API Features Part 4 §7.3 requires 412.
        using var deleteRequest = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/ogc/features/collections/{TestLayerId}/items/{existingId}");
        deleteRequest.Headers.TryAddWithoutValidation("If-Match", staleEtag);

        var response = await _fixture.Client.SendAsync(deleteRequest);
        response.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
    }

    private sealed class ThrowingFeatureChangeEventPublisher : IFeatureChangeEventPublisher
    {
        public Task PublishAsync(FeatureChangeEventRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("publish failed");

        public Task PublishStrictAsync(FeatureChangeEventRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("publish failed");
    }

}

public sealed class OgcFeaturesTransactionTestsFixture : IAsyncLifetime
{
    public WebAppFixture App { get; } = new WebAppFixture().WithTestLicense(HonuaEdition.Community);

    public Task InitializeAsync() => App.InitializeAsync();

    public Task DisposeAsync() => App.DisposeAsync();
}
