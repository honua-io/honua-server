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
    public async Task GetItems_ReturnsOnlyPublishedProperties()
    {
        var queryablesResponse = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestLayerId}/queryables");
        queryablesResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var queryablesJson = JsonDocument.Parse(await queryablesResponse.Content.ReadAsStringAsync());
        var publishedProperties = queryablesJson.RootElement
            .GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestLayerId}/items?limit=1");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var featureProperties = json.RootElement
            .GetProperty("features")[0]
            .GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .ToArray();

        featureProperties.Should().OnlyContain(property => publishedProperties.Contains(property));
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_DoesNotDuplicateTopLevelIdInProperties()
    {
        var featureId = await _fixture.InsertFeatureAsync(TestLayerId, "No Duplicate ID");

        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{TestLayerId}/items?ids={featureId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var feature = json.RootElement.GetProperty("features").EnumerateArray().Single();
        var properties = feature.GetProperty("properties");

        feature.GetProperty("id").GetInt64().Should().Be(featureId);
        properties.TryGetProperty("id", out _).Should().BeFalse();
        properties.TryGetProperty("objectid", out _).Should().BeFalse();
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
    public async Task GetItems_WithOffsetBeyondResults_ReturnsEmptyFeatureCollection()
    {
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestLayerId}/items?offset=999999");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        var features = json.RootElement.GetProperty("features").EnumerateArray().ToArray();

        json.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");
        features.Should().BeEmpty();
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
    public async Task GetItems_WithMalformedCqlFilter_DoesNotLeakParserDetails()
    {
        const string sentinel = "CQL_SENTINEL";
        var filter = $"name = '{sentinel}";
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{TestLayerId}/items?filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        var problem = JsonDocument.Parse(content);
        var detail = problem.RootElement.GetProperty("detail").GetString();
        content.Should().Contain("Invalid CQL filter");
        detail.Should().NotContain(sentinel);
        content.Should().NotContain("BytePositionInLine");
        content.Should().NotContain("LineNumber");
        content.Should().NotContain("System.Text.Json");
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithSemanticCqlError_DoesNotReportSyntaxFailure()
    {
        var filter = "ST_Area(missing_geometry) > 1000";
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{TestLayerId}/items?filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        var detail = JsonDocument.Parse(content).RootElement.GetProperty("detail").GetString();

        detail.Should().NotBeNull();
        detail!.ToLowerInvariant().Should().NotContain("syntax error");
        detail.ToLowerInvariant().Should().NotContain("parse error");
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
    public async Task GetItems_WithCql2JsonFilter_ReturnsFilteredFeatures()
    {
        var filterJson = """{"op":"=","args":[{"property":"category"},"test"]}""";
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{TestLayerId}/items?filter-lang=cql2-json&filter={Uri.EscapeDataString(filterJson)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        var features = json.RootElement.GetProperty("features").EnumerateArray().ToArray();
        features.Should().NotBeEmpty();

        foreach (var feature in features)
        {
            var properties = feature.GetProperty("properties");
            properties.GetProperty("category").GetString().Should().Be("test");
        }
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

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithIds_ReturnsRequestedFeaturesOnly()
    {
        var id1 = await _fixture.InsertFeatureAsync(TestLayerId, "IDs 1");
        await _fixture.InsertFeatureAsync(TestLayerId, "IDs 2");

        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{TestLayerId}/items?ids={id1}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        var features = json.RootElement.GetProperty("features").EnumerateArray().ToArray();

        features.Should().NotBeEmpty();
        features.All(f => f.GetProperty("id").GetInt64() == id1).Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithProperties_ReturnsProjectedProperties()
    {
        var featureId = await _fixture.InsertFeatureAsync(TestLayerId, "Projected Name");

        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{TestLayerId}/items?ids={featureId}&properties=name");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        var feature = json.RootElement.GetProperty("features").EnumerateArray().Single();
        var properties = feature.GetProperty("properties");

        properties.EnumerateObject().Select(p => p.Name).Should().Equal("name");
        properties.GetProperty("name").GetString().Should().Be("Projected Name");
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithSortBy_ReturnsOrderedFeatures()
    {
        var alphaId = await _fixture.InsertFeatureAsync(TestLayerId, "SortBy Alpha");
        var zuluId = await _fixture.InsertFeatureAsync(TestLayerId, "SortBy Zulu");

        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{TestLayerId}/items?ids={alphaId},{zuluId}&sortby=-name");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        var names = json.RootElement.GetProperty("features")
            .EnumerateArray()
            .Select(feature => feature.GetProperty("properties").GetProperty("name").GetString())
            .ToArray();

        names.Should().Equal("SortBy Zulu", "SortBy Alpha");
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithStringIdsOnNumericIdLayer_ReturnsEmptyCollection()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{TestLayerId}/items?ids=abc");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        json.RootElement.GetProperty("features").EnumerateArray().Should().BeEmpty();
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithMalformedIdsDelimiter_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{TestLayerId}/items?ids=1,,2");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithInvalidProperties_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{TestLayerId}/items?properties=does_not_exist");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithMalformedPropertiesDelimiter_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{TestLayerId}/items?properties=name,,name");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithInvalidSortBy_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{TestLayerId}/items?sortby=not_a_field");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithMalformedSortByDelimiter_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{TestLayerId}/items?sortby=name,,name");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
