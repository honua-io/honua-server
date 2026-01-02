// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.OData;

/// <summary>
/// Comprehensive OData v4 pagination tests for $top, $skip, $count combinations
/// and nextLink validation. Implements parity with OGC API Features test matrices per issue #200.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.ODataV4)]
public sealed class ODataPaginationTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const int TestLayerId = 0;
    private const int TotalTestFeatures = 15;

    public async Task InitializeAsync()
    {
        _fixture.UseSeed(Path.Combine("tests", "seed", "odata.yaml"));
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    #region $top Parameter Tests

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})?$top=1")]
    public async Task Top_One_ReturnsSingleResult()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$top=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var (features, _) = await ParseResponseAsync(response);
        features.Should().ContainSingle();
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})?$top=0")]
    public async Task Top_Zero_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$top=0");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})?$top=100")]
    public async Task Top_ExceedsTotalCount_ReturnsAllResults()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$top=100");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var (features, _) = await ParseResponseAsync(response);
        features.Should().HaveCount(TotalTestFeatures);
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})?$top=5")]
    public async Task Top_WithinRange_ReturnsExactCount()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$top=5");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var (features, _) = await ParseResponseAsync(response);
        features.Should().HaveCount(5);
    }

    #endregion

    #region $skip Parameter Tests

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})?$skip=0")]
    public async Task Skip_Zero_ReturnsAllFromBeginning()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$skip=0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var (features, _) = await ParseResponseAsync(response);
        features.Should().HaveCount(TotalTestFeatures);
        features.First().GetProperty("ObjectId").GetInt64().Should().Be(1);
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})?$skip=10")]
    public async Task Skip_PartialOffset_ReturnsRemainingResults()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$skip=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var (features, _) = await ParseResponseAsync(response);
        features.Should().HaveCount(5); // 15 - 10 = 5
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})?$skip=15")]
    public async Task Skip_ExactTotalCount_ReturnsEmptyResults()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$skip={TotalTestFeatures}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var (features, _) = await ParseResponseAsync(response);
        features.Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})?$skip=100")]
    public async Task Skip_ExceedsTotalCount_ReturnsEmptyResults()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$skip=100");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var (features, _) = await ParseResponseAsync(response);
        features.Should().BeEmpty();
    }

    #endregion

    #region $top and $skip Combination Tests

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})?$top=5&$skip=0")]
    public async Task TopAndSkip_FirstPage_ReturnsFirstFive()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$top=5&$skip=0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var (features, _) = await ParseResponseAsync(response);
        features.Should().HaveCount(5);

        var objectIds = features.Select(f => f.GetProperty("ObjectId").GetInt64()).ToArray();
        objectIds.Should().ContainInOrder(1L, 2L, 3L, 4L, 5L);
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})?$top=5&$skip=5")]
    public async Task TopAndSkip_SecondPage_ReturnsNextFive()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$top=5&$skip=5");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var (features, _) = await ParseResponseAsync(response);
        features.Should().HaveCount(5);

        var objectIds = features.Select(f => f.GetProperty("ObjectId").GetInt64()).ToArray();
        objectIds.Should().ContainInOrder(6L, 7L, 8L, 9L, 10L);
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})?$top=5&$skip=10")]
    public async Task TopAndSkip_ThirdPage_ReturnsLastFive()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$top=5&$skip=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var (features, _) = await ParseResponseAsync(response);
        features.Should().HaveCount(5);

        var objectIds = features.Select(f => f.GetProperty("ObjectId").GetInt64()).ToArray();
        objectIds.Should().ContainInOrder(11L, 12L, 13L, 14L, 15L);
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})?$top=5&$skip=13")]
    public async Task TopAndSkip_PartialLastPage_ReturnsRemainingResults()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$top=5&$skip=13");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var (features, _) = await ParseResponseAsync(response);
        features.Should().HaveCount(2); // Only 14, 15 remain
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})?$top=1&$skip=0")]
    public async Task TopOneSkipZero_ReturnsFirstResult()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$top=1&$skip=0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var (features, _) = await ParseResponseAsync(response);
        features.Should().ContainSingle();
        features[0].GetProperty("ObjectId").GetInt64().Should().Be(1);
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})?$top=1&$skip=14")]
    public async Task TopOneSkipToLast_ReturnsLastResult()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$top=1&$skip=14");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var (features, _) = await ParseResponseAsync(response);
        features.Should().ContainSingle();
        features[0].GetProperty("ObjectId").GetInt64().Should().Be(15);
    }

    #endregion

    #region $count Parameter Tests

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})?$count=true")]
    public async Task Count_True_ReturnsTotalCount()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$count=true");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        document.RootElement.TryGetProperty("@odata.count", out var countElement).Should().BeTrue();
        countElement.GetInt64().Should().Be(TotalTestFeatures);
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})?$count=false")]
    public async Task Count_False_DoesNotIncludeCount()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$count=false");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        document.RootElement.TryGetProperty("@odata.count", out _).Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})?$top=5&$count=true")]
    public async Task TopWithCount_ReturnsTotalCountNotLimited()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$top=5&$count=true");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        var features = document.RootElement.GetProperty("value").EnumerateArray().ToList();
        features.Should().HaveCount(5);

        // Count should be total, not limited by $top
        document.RootElement.TryGetProperty("@odata.count", out var countElement).Should().BeTrue();
        countElement.GetInt64().Should().Be(TotalTestFeatures);
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})?$top=5&$skip=10&$count=true")]
    public async Task TopSkipCount_ReturnsTotalCountWithPagination()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$top=5&$skip=10&$count=true");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        var features = document.RootElement.GetProperty("value").EnumerateArray().ToList();
        features.Should().HaveCount(5);

        // Count is still the total
        document.RootElement.TryGetProperty("@odata.count", out var countElement).Should().BeTrue();
        countElement.GetInt64().Should().Be(TotalTestFeatures);
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})?$filter=...&$count=true")]
    public async Task FilterWithCount_ReturnsFilteredCount()
    {
        var filter = "state eq 'California'";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}&$count=true");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        // Count should be filtered count (5 California cities)
        document.RootElement.TryGetProperty("@odata.count", out var countElement).Should().BeTrue();
        countElement.GetInt64().Should().Be(5);
    }

    #endregion

    #region nextLink Validation Tests

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})?$top=5")]
    public async Task NextLink_WhenMoreResultsExist_ReturnsValidNextLink()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$top=5");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        document.RootElement.TryGetProperty("@odata.nextLink", out var nextLinkElement).Should().BeTrue();
        var nextLink = nextLinkElement.GetString();

        nextLink.Should().NotBeNullOrEmpty();
        nextLink.Should().Contain("$skip=5");
        nextLink.Should().Contain("$top=5");
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})?$top=5 follow nextLink")]
    public async Task NextLink_FollowNextLink_ReturnsNextPage()
    {
        // Get first page
        var firstResponse = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$top=5");
        var firstContent = await firstResponse.Content.ReadAsStringAsync();
        using var firstDocument = JsonDocument.Parse(firstContent);

        var nextLink = firstDocument.RootElement.GetProperty("@odata.nextLink").GetString();

        // Extract relative path from nextLink
        var nextUri = new Uri(nextLink!);
        var relativePath = nextUri.PathAndQuery;

        // Follow next link
        var secondResponse = await _fixture.Client.GetAsync(relativePath);

        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var (secondFeatures, _) = await ParseResponseAsync(secondResponse);
        secondFeatures.Should().HaveCount(5);

        // First feature should be objectId 6
        secondFeatures[0].GetProperty("ObjectId").GetInt64().Should().Be(6);
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})?$top=5 iterate all pages")]
    public async Task NextLink_IterateAllPages_ReturnsAllFeatures()
    {
        var allFeatures = new List<JsonElement>();
        string? nextLink = $"/odata/Features({TestLayerId})?$top=5";
        var pageCount = 0;
        const int maxPages = 10; // Safety limit

        while (nextLink != null && pageCount < maxPages)
        {
            var response = await _fixture.Client.GetAsync(nextLink);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var content = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(content);

            var features = document.RootElement.GetProperty("value")
                .EnumerateArray()
                .Select(e => e.Clone())
                .ToList();

            allFeatures.AddRange(features);

            if (document.RootElement.TryGetProperty("@odata.nextLink", out var nextLinkElement))
            {
                var fullNextLink = nextLinkElement.GetString();
                var nextUri = new Uri(fullNextLink!);
                nextLink = nextUri.PathAndQuery;
            }
            else
            {
                nextLink = null;
            }

            pageCount++;
        }

        allFeatures.Should().HaveCount(TotalTestFeatures);
        pageCount.Should().Be(3); // 15 features / 5 per page = 3 pages
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})?$top=5&$skip=10")]
    public async Task NextLink_LastPage_NoNextLink()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$top=5&$skip=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        // Last page should not have nextLink
        document.RootElement.TryGetProperty("@odata.nextLink", out _).Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})?$top=100")]
    public async Task NextLink_AllResultsInOnePage_NoNextLink()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$top=100");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        // Should not have nextLink when all results fit in one page
        document.RootElement.TryGetProperty("@odata.nextLink", out _).Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})?$filter=...&$top=3")]
    public async Task NextLink_WithFilter_PreservesFilterInNextLink()
    {
        var filter = "state eq 'California'";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}&$top=3");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        if (document.RootElement.TryGetProperty("@odata.nextLink", out var nextLinkElement))
        {
            var nextLink = nextLinkElement.GetString();
            nextLink.Should().Contain("$filter");
            nextLink.Should().Contain("California");
        }
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})?$orderby=...&$top=3")]
    public async Task NextLink_WithOrderBy_PreservesOrderByInNextLink()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$orderby=population desc&$top=3");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        document.RootElement.TryGetProperty("@odata.nextLink", out var nextLinkElement).Should().BeTrue();
        var nextLink = nextLinkElement.GetString();
        nextLink.Should().Contain("$orderby");
        nextLink.Should().Contain("population");
    }

    #endregion

    #region Layer Pagination Tests

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Layers?$top=1")]
    public async Task Layers_WithTop_ReturnsLimitedResults()
    {
        var response = await _fixture.Client.GetAsync("/odata/Layers?$top=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var (features, _) = await ParseResponseAsync(response);
        features.Should().ContainSingle();
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Layers?$skip=1")]
    public async Task Layers_WithSkip_ReturnsOffsetResults()
    {
        var response = await _fixture.Client.GetAsync("/odata/Layers?$skip=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var (features, _) = await ParseResponseAsync(response);
        features.Should().ContainSingle(); // 2 layers - 1 skipped = 1
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Layers?$count=true")]
    public async Task Layers_WithCount_ReturnsLayerCount()
    {
        var response = await _fixture.Client.GetAsync("/odata/Layers?$count=true");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        document.RootElement.TryGetProperty("@odata.count", out var countElement).Should().BeTrue();
        countElement.GetInt64().Should().Be(2); // US Cities and City Landmarks
    }

    #endregion

    #region Edge Cases

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})?$top=0&$count=true")]
    public async Task TopZero_WithCount_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$top=0&$count=true");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})?$skip=1000&$count=true")]
    public async Task SkipBeyondResults_WithCount_ReturnsZeroCount()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$skip=1000&$count=true");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        var features = document.RootElement.GetProperty("value").EnumerateArray().ToList();
        features.Should().BeEmpty();

        document.RootElement.TryGetProperty("@odata.count", out var countElement).Should().BeTrue();
        countElement.GetInt64().Should().Be(0);
    }

    #endregion

    #region Helper Methods

    private static async Task<(List<JsonElement> Features, JsonDocument Document)> ParseResponseAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        var document = JsonDocument.Parse(content);
        var features = document.RootElement.GetProperty("value")
            .EnumerateArray()
            .Select(e => e.Clone())
            .ToList();
        return (features, document);
    }

    #endregion
}
