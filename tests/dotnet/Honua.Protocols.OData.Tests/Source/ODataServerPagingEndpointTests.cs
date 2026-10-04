// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.OData;

/// <summary>
/// Verifies OData server-driven paging (OData:MaxPageSize, #1644): a large $top must
/// return promptly with a bounded first page and an @odata.nextLink, rather than
/// materializing an unbounded LIMIT that hangs past the request budget on ad hoc
/// spatial queries (geo.intersects + $select). The fixture pins a small MaxPageSize
/// so the 15-feature seed exercises the multi-page path.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.ODataV4)]
public sealed class ODataServerPagingEndpointTests : IAsyncLifetime
{
    private const int TestLayerId = 0;
    private const int MaxPageSize = 5;

    private readonly WebAppFixture _fixture = new WebAppFixture()
        .WithTestLicense(HonuaEdition.Pro)
        .ConfigureWebHost(builder => builder.UseSetting(
            "OData:MaxPageSize",
            MaxPageSize.ToString(CultureInfo.InvariantCulture)));

    public async Task InitializeAsync()
    {
        // All segments are relative literal path fragments (not user input), so none can be
        // rooted and silently drop earlier arguments.
        _fixture.UseSeed(Path.Join("tests", "seed", "odata.yaml"));
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$top=5000")]
    public async Task Features_WithLargeTop_ReturnsBoundedPageWithNextLink()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$top=5000&$select=ObjectId,name",
            cts.Token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync(cts.Token);
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        var items = root.GetProperty("value").EnumerateArray().ToList();
        items.Count.Should().Be(MaxPageSize, "the effective page size is clamped to OData:MaxPageSize");

        root.TryGetProperty("@odata.nextLink", out var nextLink).Should().BeTrue(
            "more rows than the page size exist, so the server must emit a nextLink for server-driven paging");
        nextLink.GetString().Should().Contain("$skip=5")
            .And.Contain("$top=4995",
                "the nextLink carries the remaining $top budget, not the clamped page size (#5464)");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$top=5000")]
    public async Task Features_WithLargeTop_PagesThroughAllRowsViaNextLink()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        var seen = new HashSet<int>();
        var relativePath = $"/odata/Features({TestLayerId})?$top=5000&$select=ObjectId,name";
        var pages = 0;

        while (relativePath != null && pages < 20)
        {
            var response = await _fixture.Client.GetAsync(relativePath, cts.Token);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var content = await response.Content.ReadAsStringAsync(cts.Token);
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;

            foreach (var item in root.GetProperty("value").EnumerateArray())
            {
                seen.Add(item.GetProperty("ObjectId").GetInt32());
            }

            relativePath = root.TryGetProperty("@odata.nextLink", out var nextLink)
                ? ToRelative(nextLink.GetString()!)
                : null;
            pages++;
        }

        // The seed has 15 features on layer 0; all must be reachable across pages.
        seen.Count.Should().Be(15);
        pages.Should().BeGreaterThan(1, "a 15-row layer with a page size of 5 requires multiple pages");
    }

    // OData 4.01 Part 1 §11.2.6.3 / §11.2.6.7 (#5464): $top bounds the whole requested
    // collection. When the server page is smaller than $top, each nextLink carries the
    // remaining budget, and the page that exhausts it carries no nextLink.
    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})?$top=12")]
    public async Task Features_WithTopAboveServerPage_ContinuationCarriesRemainingTop()
    {
        var pages = await FollowAsync($"/odata/Features({TestLayerId})?$top=12&$orderby=ObjectId&$select=ObjectId");

        pages.Select(page => page.Ids.Count).Should().Equal(5, 5, 2);
        pages[0].NextLink.Should().Contain("$skip=5").And.Contain("$top=7");
        pages[1].NextLink.Should().Contain("$skip=10").And.Contain("$top=2");
        pages[2].NextLink.Should().BeNull("the requested $top=12 is exhausted on the third page");
        pages.SelectMany(page => page.Ids).Should().Equal(Enumerable.Range(1, 12));
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})?$skiptoken=0&$top=7")]
    public async Task Features_WithSkipTokenAndTopAboveServerPage_StopsAtTop()
    {
        var pages = await FollowAsync($"/odata/Features({TestLayerId})?$skiptoken=0&$top=7&$select=ObjectId");

        pages.Select(page => page.Ids.Count).Should().Equal(5, 2);
        pages[0].NextLink.Should().Contain("$skiptoken=").And.Contain("$top=2");
        pages.SelectMany(page => page.Ids).Should().Equal(Enumerable.Range(1, 7));
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task NextLink_WhenMoreResultsExist_ReturnsValidNextLink()
    {
        var pages = await FollowAsync($"/odata/Features({TestLayerId})", maxPages: 1);

        pages[0].Ids.Should().HaveCount(MaxPageSize);
        pages[0].NextLink.Should().NotBeNullOrEmpty();
        pages[0].NextLink.Should().Contain("$skip=5");
        pages[0].NextLink.Should().NotContain("$top=",
            "a request without $top has no client ceiling, so the continuation must not invent one");
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId}) follow nextLink")]
    public async Task NextLink_FollowNextLink_ReturnsNextPage()
    {
        var pages = await FollowAsync($"/odata/Features({TestLayerId})", maxPages: 2);

        pages[1].Ids.Should().HaveCount(5);
        pages[1].Ids[0].Should().Be(6);
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId}) iterate all pages")]
    public async Task NextLink_IterateAllPages_ReturnsAllFeatures()
    {
        var pages = await FollowAsync($"/odata/Features({TestLayerId})");

        pages.SelectMany(page => page.Ids).Should().HaveCount(15);
        pages.Should().HaveCount(3); // 15 features / 5 per page = 3 pages
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})?$orderby=...")]
    public async Task NextLink_WithOrderBy_PreservesOrderByInNextLink()
    {
        var pages = await FollowAsync($"/odata/Features({TestLayerId})?$orderby=population desc", maxPages: 1);

        pages[0].NextLink.Should().Contain("$orderby");
        pages[0].NextLink.Should().Contain("population");
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})?$format=...")]
    public async Task NextLink_WithFormat_PreservesFormatInNextLink()
    {
        var pages = await FollowAsync(
            $"/odata/Features({TestLayerId})?$format=application/json;odata.metadata=none",
            maxPages: 1);

        pages[0].NextLink.Should().Contain("$format=");
        pages[0].NextLink.Should().Contain("odata.metadata");
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})?$skiptoken=0")]
    public async Task NextLink_WithSkipToken_UsesSkipTokenInNextLink()
    {
        var pages = await FollowAsync($"/odata/Features({TestLayerId})?$skiptoken=0", maxPages: 1);

        // Skip token is now an opaque Base64Url-encoded cursor, not a raw integer
        pages[0].NextLink.Should().Contain("$skiptoken=");
        pages[0].NextLink.Should().NotContain("$skip=");
    }

    private async Task<List<(List<long> Ids, string? NextLink)>> FollowAsync(string relativePath, int maxPages = 20)
    {
        var pages = new List<(List<long> Ids, string? NextLink)>();
        string? current = relativePath;
        while (current != null && pages.Count < maxPages)
        {
            var response = await _fixture.Client.GetAsync(current);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var ids = document.RootElement.GetProperty("value").EnumerateArray()
                .Select(item => item.GetProperty("ObjectId").GetInt64())
                .ToList();
            var nextLink = document.RootElement.TryGetProperty("@odata.nextLink", out var link)
                ? link.GetString()
                : null;
            pages.Add((ids, nextLink));
            current = nextLink == null ? null : ToRelative(nextLink);
        }

        return pages;
    }

    private static string ToRelative(string url)
    {
        var uri = new Uri(url, UriKind.RelativeOrAbsolute);
        return uri.IsAbsoluteUri ? uri.PathAndQuery : url;
    }
}
