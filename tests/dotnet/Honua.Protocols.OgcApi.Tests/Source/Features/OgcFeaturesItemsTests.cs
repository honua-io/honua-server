// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

    // Independently computed from tests/seed/server.yaml (layer 0, objectids 1-5):
    //   1 (-122.5, 37.5) category=test    2 (-122.7, 37.7) category=sample
    //   3 (null geometry) category=test   4 (-121.9, 37.3) category=sample
    //   5 (-122.3, 37.8) category=test
    // The fixture owns a private Postgres schema, and every feature other tests in
    // this class insert carries only a `name` and a null geometry, so the category
    // and bbox partitions below stay exact for the whole class run.
    private static readonly long[] SeededCategoryTestIds = [1, 3, 5];
    private static readonly long[] SeededCategorySampleIds = [2, 4];

    private static long[] FeatureIds(JsonElement collection) => collection
        .GetProperty("features")
        .EnumerateArray()
        .Select(feature => feature.GetProperty("id").GetInt64())
        .OrderBy(id => id)
        .ToArray();

    private async Task<JsonDocument> GetItemsAsync(string query)
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{TestLayerId}/items?{query}");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body);
    }

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
        // The limit is applied over a pinned candidate set (the three seeded
        // category=test rows), so the page is exactly 2 of 3 known ids rather than
        // "at most 2", which an empty result would also satisfy (#4393).
        using var page = await GetItemsAsync(
            $"filter={Uri.EscapeDataString("category = 'test'")}&limit=2");

        var ids = FeatureIds(page.RootElement);
        ids.Should().HaveCount(2);
        ids.Should().BeSubsetOf(SeededCategoryTestIds);
        page.RootElement.GetProperty("numberReturned").GetInt32().Should().Be(2);
        page.RootElement.GetProperty("numberMatched").GetInt32().Should().Be(SeededCategoryTestIds.Length);
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
        // Pin the candidate set to the three seeded category=test rows, then assert
        // that offset=1 drops exactly the first row of the unoffset page. An ignored
        // offset returns all three and fails; the previous shape-only assertion did
        // not (#4393).
        const string filter = "category = 'test'";
        using var all = await GetItemsAsync($"filter={Uri.EscapeDataString(filter)}");
        var firstPageOrder = all.RootElement.GetProperty("features").EnumerateArray()
            .Select(feature => feature.GetProperty("id").GetInt64()).ToArray();
        firstPageOrder.OrderBy(id => id).Should().Equal(SeededCategoryTestIds);

        using var offset = await GetItemsAsync($"filter={Uri.EscapeDataString(filter)}&offset=1");
        FeatureIds(offset.RootElement).Should().Equal(firstPageOrder.Skip(1).OrderBy(id => id));
        offset.RootElement.GetProperty("numberReturned").GetInt32()
            .Should().Be(SeededCategoryTestIds.Length - 1);
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
        // `name = 'Test Feature'` matches seeded objectid 1 and nothing else, so the
        // matched id set is asserted exactly. The previous foreach body proved only
        // the absence of false positives and passed on zero features (#4393).
        using var json = await GetItemsAsync(
            $"filter={Uri.EscapeDataString("name = 'Test Feature'")}");

        json.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");
        FeatureIds(json.RootElement).Should().Equal(1L);
        json.RootElement.GetProperty("numberMatched").GetInt32().Should().Be(1);
        json.RootElement.GetProperty("features").EnumerateArray().Single()
            .GetProperty("properties").GetProperty("name").GetString().Should().Be("Test Feature");
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
        // Both conjuncts are load-bearing and asserted as exact id sets: `category =
        // 'test'` alone matches {1,3,5} and `name = 'Test Feature'` alone matches {1},
        // so the conjunction must be {1}. The previous shape-only assertion passed
        // whether the filter returned everything or nothing (#4393).
        using var conjunction = await GetItemsAsync(
            $"filter={Uri.EscapeDataString("name = 'Test Feature' AND category = 'test'")}");
        conjunction.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");
        FeatureIds(conjunction.RootElement).Should().Equal(1L);

        using var categoryOnly = await GetItemsAsync(
            $"filter={Uri.EscapeDataString("category = 'test'")}");
        FeatureIds(categoryOnly.RootElement).Should().Equal(SeededCategoryTestIds);

        // A conjunction whose second term excludes the first term's only match must
        // return nothing at all — an unapplied filter would return the whole layer.
        using var contradiction = await GetItemsAsync(
            $"filter={Uri.EscapeDataString("name = 'Test Feature' AND category = 'sample'")}");
        FeatureIds(contradiction.RootElement).Should().BeEmpty();
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
        // Exact id set, not just NotBeEmpty: an over-restrictive filter that returned
        // only objectid 1 passed the previous per-row re-check (#4393).
        FeatureIds(json.RootElement).Should().Equal(SeededCategoryTestIds);
        json.RootElement.GetProperty("numberMatched").GetInt32().Should().Be(SeededCategoryTestIds.Length);

        foreach (var properties in features.Select(feature => feature.GetProperty("properties")))
        {
            properties.GetProperty("category").GetString().Should().Be("test");
        }

        // The complementary partition must be exactly the other two seeded rows, so
        // the two id sets together account for every categorised seeded feature.
        var sampleResponse = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{TestLayerId}/items?filter-lang=cql2-json&filter=" +
            Uri.EscapeDataString("""{"op":"=","args":[{"property":"category"},"sample"]}"""));
        sampleResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var sample = JsonDocument.Parse(await sampleResponse.Content.ReadAsStringAsync());
        FeatureIds(sample.RootElement).Should().Equal(SeededCategorySampleIds);
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
        // Walk the whole filtered set one page at a time: each page must hold exactly
        // one feature, the pages must be distinct, and their union must be exactly
        // {1,3,5}. The previous BeLessThanOrEqualTo(1) assertions passed on zero
        // features and never checked the filter component at all (#4393).
        const string filter = "category = 'test'";
        var paged = new List<long>();
        for (var offset = 0; offset < SeededCategoryTestIds.Length; offset++)
        {
            using var page = await GetItemsAsync(
                $"filter={Uri.EscapeDataString(filter)}&limit=1&offset={offset}");
            page.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");
            page.RootElement.GetProperty("numberReturned").GetInt32().Should().Be(1);
            page.RootElement.GetProperty("numberMatched").GetInt32()
                .Should().Be(SeededCategoryTestIds.Length, "numberMatched reports the filtered total, not the page");
            var id = FeatureIds(page.RootElement).Single();
            page.RootElement.GetProperty("features").EnumerateArray().Single()
                .GetProperty("properties").GetProperty("category").GetString().Should().Be("test");
            paged.Add(id);
        }

        paged.Should().OnlyHaveUniqueItems();
        paged.OrderBy(id => id).Should().Equal(SeededCategoryTestIds);

        using var beyond = await GetItemsAsync(
            $"filter={Uri.EscapeDataString(filter)}&limit=1&offset={SeededCategoryTestIds.Length}");
        FeatureIds(beyond.RootElement).Should().BeEmpty();
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
