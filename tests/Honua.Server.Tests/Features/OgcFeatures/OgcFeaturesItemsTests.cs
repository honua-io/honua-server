// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.OgcFeatures;

[Collection("Database")]
[Protocol(Protocols.OgcApiFeatures)]
[Operation(Operations.Query)]
public class OgcFeaturesItemsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const int TestLayerId = 0; // Use existing test layer

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_BasicRequest_ReturnsFeatureCollection()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestLayerId}/items");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/geo+json");

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");
        json.RootElement.GetProperty("features").EnumerateArray().Should().NotBeEmpty();
        json.RootElement.TryGetProperty("numberReturned", out _).Should().BeTrue();
        json.RootElement.TryGetProperty("numberMatched", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithLimit_ReturnsLimitedFeatures()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestLayerId}/items?limit=2");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        var features = json.RootElement.GetProperty("features").EnumerateArray().ToArray();
        features.Length.Should().BeLessThanOrEqualTo(2);

        var numberReturned = json.RootElement.GetProperty("numberReturned").GetInt32();
        numberReturned.Should().BeLessThanOrEqualTo(2);
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithOffset_ReturnsOffsetFeatures()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestLayerId}/items?offset=1");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        // Should be valid GeoJSON FeatureCollection
        json.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");
        json.RootElement.TryGetProperty("features", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithCqlFilter_ReturnsFilteredFeatures()
    {
        // Act - Use a basic CQL2-Text filter
        var filter = "name = 'Test Feature'";
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestLayerId}/items?filter={Uri.EscapeDataString(filter)}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");

        // Features should be filtered (may be empty if no matches)
        var features = json.RootElement.GetProperty("features").EnumerateArray().ToArray();

        // If features exist, they should match the filter criteria
        foreach (var feature in features)
        {
            if (feature.TryGetProperty("properties", out var properties) &&
                properties.TryGetProperty("name", out var nameProperty))
            {
                nameProperty.GetString().Should().Be("Test Feature");
            }
        }
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithInvalidCqlFilter_ReturnsBadRequest()
    {
        // Act - Use an invalid CQL2-Text filter with syntax error
        var filter = "name = Test Feature"; // Missing quotes - should be invalid
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestLayerId}/items?filter={Uri.EscapeDataString(filter)}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Invalid CQL filter");
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithComplexCqlFilter_ReturnsFilteredFeatures()
    {
        // Act - Use a more complex CQL2-Text filter
        var filter = "name = 'Test Feature' AND category = 'test'";
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestLayerId}/items?filter={Uri.EscapeDataString(filter)}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");
        json.RootElement.TryGetProperty("features", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_NonExistentCollection_ReturnsNotFound()
    {
        // Act
        var response = await _fixture.Client.GetAsync("/ogc/features/collections/99999/items");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_InvalidCollectionId_ReturnsNotFound()
    {
        // Act - Use non-numeric collection ID
        var response = await _fixture.Client.GetAsync("/ogc/features/collections/invalid/items");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithAllParameters_ReturnsProperlyFilteredAndPaginated()
    {
        // Act - Combine filter, limit, and offset
        var filter = "category = 'test'";
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{TestLayerId}/items?filter={Uri.EscapeDataString(filter)}&limit=1&offset=0");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");

        var features = json.RootElement.GetProperty("features").EnumerateArray().ToArray();
        features.Length.Should().BeLessThanOrEqualTo(1);

        var numberReturned = json.RootElement.GetProperty("numberReturned").GetInt32();
        numberReturned.Should().BeLessThanOrEqualTo(1);
    }
}
