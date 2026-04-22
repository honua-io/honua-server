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
/// Integration tests for OData cursor-based $skiptoken pagination.
/// Verifies that opaque skip tokens are generated in nextLink URLs and can be followed
/// to retrieve subsequent pages. Also validates error handling for invalid tokens.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.ODataV4)]
public sealed class ODataSkipTokenTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const int TestLayerId = 0;

    public async Task InitializeAsync()
    {
        _fixture.UseSeed(Path.Combine("tests", "seed", "odata.yaml"));
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task Query_WithSkipToken_ReturnsCursorBasedPagination()
    {
        // First request with $skiptoken=0 to trigger opaque token generation in nextLink
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$top=3&$skiptoken=0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        var features = document.RootElement.GetProperty("value").EnumerateArray().ToList();
        features.Should().HaveCount(3);

        // nextLink should contain an opaque $skiptoken (not a raw integer)
        document.RootElement.TryGetProperty("@odata.nextLink", out var nextLinkElement).Should().BeTrue();
        var nextLink = nextLinkElement.GetString();
        nextLink.Should().NotBeNullOrEmpty();
        nextLink.Should().Contain("$skiptoken=");
        nextLink.Should().NotContain("$skip=");
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task Query_FollowSkipTokenNextLink_ReturnsNextPage()
    {
        // Get first page with skiptoken
        var firstResponse = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$top=5&$skiptoken=0");
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var firstContent = await firstResponse.Content.ReadAsStringAsync();
        using var firstDocument = JsonDocument.Parse(firstContent);

        var nextLink = firstDocument.RootElement.GetProperty("@odata.nextLink").GetString();

        // Follow the opaque skip token link
        var nextUri = new Uri(nextLink!);
        var secondResponse = await _fixture.Client.GetAsync(nextUri.PathAndQuery);

        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondContent = await secondResponse.Content.ReadAsStringAsync();
        using var secondDocument = JsonDocument.Parse(secondContent);

        var secondFeatures = secondDocument.RootElement.GetProperty("value")
            .EnumerateArray()
            .Select(e => e.Clone())
            .ToList();

        secondFeatures.Should().HaveCount(5);
        // The second page should start at objectId 6
        secondFeatures[0].GetProperty("ObjectId").GetInt64().Should().Be(6);
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task Query_WithInvalidSkipToken_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$skiptoken=not-a-valid-token!!!");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task Query_WithSkipTokenAndSkip_ReturnsBadRequest()
    {
        // $skip and $skiptoken are mutually exclusive
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$skip=5&$skiptoken=0");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task Query_LegacyIntegerSkipToken_StillWorks()
    {
        // Legacy integer skip tokens should continue to work for backward compatibility
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$skiptoken=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        var features = document.RootElement.GetProperty("value").EnumerateArray().ToList();
        features.Should().HaveCount(5); // 15 total - 10 skipped = 5 remaining
        features[0].GetProperty("ObjectId").GetInt64().Should().Be(11);
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task Query_SkipTokenIterateAllPages_ReturnsAllFeatures()
    {
        var allFeatures = new List<JsonElement>();
        string? requestUrl = $"/odata/Features({TestLayerId})?$top=5&$skiptoken=0";
        var pageCount = 0;
        const int maxPages = 10;

        while (requestUrl != null && pageCount < maxPages)
        {
            var response = await _fixture.Client.GetAsync(requestUrl);
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
                requestUrl = nextUri.PathAndQuery;
            }
            else
            {
                requestUrl = null;
            }

            pageCount++;
        }

        allFeatures.Should().HaveCount(15);
        pageCount.Should().Be(3); // 15 features / 5 per page = 3 pages
    }
}
