// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

public sealed class OgcFeaturesItemsTestsFixture : IAsyncLifetime
{
    public WebAppFixture App { get; } = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

    public Task InitializeAsync() => App.InitializeAsync();

    public Task DisposeAsync() => App.DisposeAsync();
}

[Protocol(TestProtocols.OgcApiFeatures)]
[Operation(Operations.Query)]
[Collection("Database")]
public class OgcFeaturesItemsTests : IClassFixture<OgcFeaturesItemsTestsFixture>
{
    private readonly WebAppFixture _fixture;
    private const int TestLayerId = 0; // Use existing test layer

    public OgcFeaturesItemsTests(OgcFeaturesItemsTestsFixture fixture)
    {
        _fixture = fixture.App;
    }

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

        // limit sets FeatureQuery.Limit, which makes FeatureQueryBuilder append the
        // stable `ORDER BY objectid ASC` page order (RequiresStablePageOrder), so the
        // first page of the seeded layer is exactly objectids 1 and 2 — not "at most
        // two rows", which a limit that was parsed and then dropped also satisfies.
        FeatureIdsInResponseOrder(json).Should().Equal(1L, 2L);
        json.RootElement.GetProperty("numberReturned").GetInt32().Should().Be(2);
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
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestLayerId}/items?offset=1&limit=2");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");

        // Offset 1 over the stable objectid page order skips seeded feature 1 and starts
        // at 2. Asserting the shape only (the previous assertion) passes when the offset
        // is parsed and then never applied.
        FeatureIdsInResponseOrder(json).Should().Equal(2L, 3L);
        json.RootElement.GetProperty("numberReturned").GetInt32().Should().Be(2);
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

        // The seed gives layer 0 five rows with distinct names (tests/seed/server.yaml);
        // only objectid 1 is named 'Test Feature'. Asserting the exact id set fails both
        // when the filter is ignored (1..5 returned) and when it over-restricts (nothing
        // returned) — the previous foreach-over-the-result assertion passed on zero rows.
        FeatureIds(json).Should().Equal(1L);

        var only = json.RootElement.GetProperty("features").EnumerateArray().Single();
        only.GetProperty("properties").GetProperty("name").GetString().Should().Be("Test Feature");
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
        // Act - Use a more complex CQL2-Text filter. Both conjuncts have to do work for
        // the expected set to come back: category excludes the 'sample' rows 2 and 4, and
        // the name inequality excludes row 1. Seed: 1 test/'Test Feature', 2 sample,
        // 3 test/'Third Feature', 4 sample, 5 test/'Fifth Feature'.
        var filter = "category = 'test' AND name <> 'Test Feature'";
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestLayerId}/items?filter={Uri.EscapeDataString(filter)}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");

        // Shape-only assertions (the previous `TryGetProperty("features")`) pass on a
        // filter that returns everything and on one that returns nothing.
        FeatureIds(json).Should().Equal(3L, 5L);
        foreach (var properties in json.RootElement.GetProperty("features")
                     .EnumerateArray()
                     .Select(feature => feature.GetProperty("properties")))
        {
            properties.GetProperty("category").GetString().Should().Be("test");
            properties.GetProperty("name").GetString().Should().NotBe("Test Feature");
        }
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

        // Seeded categories: test = {1, 3, 5}, sample = {2, 4}. The per-row re-check below
        // proves the absence of false positives; the id set proves the absence of false
        // negatives, which an over-restrictive filter returning only feature 1 would hide.
        FeatureIds(json).Should().Equal(1L, 3L, 5L);

        foreach (var properties in json.RootElement.GetProperty("features")
                     .EnumerateArray()
                     .Select(feature => feature.GetProperty("properties")))
        {
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
        // Act/Assert - Combine filter, limit and offset. category = 'test' matches seeded
        // objectids 1, 3 and 5, so walking the three single-row pages must yield exactly
        // that sequence in the stable objectid page order. The previous
        // `BeLessThanOrEqualTo(1)` assertions passed on zero rows and never checked that
        // the filter component was applied at all.
        var filter = "category = 'test'";
        var expectedPages = new[] { 1L, 3L, 5L };

        for (var offset = 0; offset < expectedPages.Length; offset++)
        {
            var response = await _fixture.Client.GetAsync(
                $"/ogc/features/collections/{TestLayerId}/items?filter={Uri.EscapeDataString(filter)}&limit=1&offset={offset}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            json.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");
            json.RootElement.GetProperty("numberReturned").GetInt32().Should().Be(1);
            json.RootElement.GetProperty("numberMatched").GetInt32().Should().Be(expectedPages.Length);
            FeatureIdsInResponseOrder(json).Should().Equal(expectedPages[offset]);
        }

        // One page past the last match is empty, so the pager cannot be reporting a
        // truncated-but-unfiltered set.
        var pastEnd = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{TestLayerId}/items?filter={Uri.EscapeDataString(filter)}&limit=1&offset={expectedPages.Length}");
        pastEnd.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonDocument.Parse(await pastEnd.Content.ReadAsStringAsync())
            .RootElement.GetProperty("features").EnumerateArray().Should().BeEmpty();
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

    // Feature ids in the order the server returned them. Only pages that carry
    // limit/offset or a spatial filter are ordered (FeatureQueryBuilder appends
    // `ORDER BY objectid ASC` for those, see RequiresStablePageOrder), so this is used
    // only for paged assertions; unordered result sets use FeatureIds.
    private static long[] FeatureIdsInResponseOrder(JsonDocument json)
        => json.RootElement.GetProperty("features")
            .EnumerateArray()
            .Select(ReadFeatureId)
            .ToArray();

    // Feature ids sorted ascending, for result sets the server is free to return in any
    // order. Comparing a sorted id set is the assertion an ignored filter cannot satisfy.
    private static long[] FeatureIds(JsonDocument json)
        => FeatureIdsInResponseOrder(json).OrderBy(id => id).ToArray();

    private static long ReadFeatureId(JsonElement feature)
    {
        var id = feature.GetProperty("id");
        return id.ValueKind == JsonValueKind.String
            ? long.Parse(id.GetString()!, CultureInfo.InvariantCulture)
            : id.GetInt64();
    }
}
