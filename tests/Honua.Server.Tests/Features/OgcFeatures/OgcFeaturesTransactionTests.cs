// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.OgcFeatures;
using Honua.Server.Features.OgcFeatures.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.OgcFeatures;

[Collection("Database")]
[Protocol(Protocols.OgcApiFeatures)]
public sealed class OgcFeaturesTransactionTests : IAsyncLifetime, IDisposable
{
    private readonly WebAppFixture _fixture = new();
    private TestFeatureStore _featureStore = null!;
    private const int TestLayerId = 0;

    public async Task InitializeAsync()
    {
        _featureStore = new TestFeatureStore();
        _fixture.ReplaceService<ILayerCatalog>(new TestLayerCatalog());
        _fixture.ReplaceService<IFeatureStore>(_featureStore);
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        _featureStore?.Dispose();
        await _fixture.DisposeAsync();
    }

    public void Dispose()
    {
        _featureStore?.Dispose();
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
        var existing = await _featureStore.CreateAsync(
            TestLayerId,
            Feature.Create(0, null, ImmutableDictionary<string, object?>.Empty.Add("name", "Original")));

        var feature = new GeoJsonFeature
        {
            Type = "Feature",
            Id = existing.Id,
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
            $"/ogc/features/collections/{TestLayerId}/items/{existing.Id}",
            new StringContent(json, Encoding.UTF8, MediaTypes.GeoJson));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        var updated = JsonSerializer.Deserialize(responseContent, OgcJsonContext.Default.GeoJsonFeature);
        updated.Should().NotBeNull();
        updated!.Id.Should().Be(existing.Id);
        updated.Properties.Should().ContainKey("name");
    }

    [IntegrationTest]
    [Operation(Operations.Delete)]
    [Endpoint("DELETE /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task DeleteFeature_WithValidId_ReturnsNoContent()
    {
        var existing = await _featureStore.CreateAsync(
            TestLayerId,
            Feature.Create(0, null, ImmutableDictionary<string, object?>.Empty.Add("name", "Delete Me")));

        var response = await _fixture.Client.DeleteAsync(
            $"/ogc/features/collections/{TestLayerId}/items/{existing.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
