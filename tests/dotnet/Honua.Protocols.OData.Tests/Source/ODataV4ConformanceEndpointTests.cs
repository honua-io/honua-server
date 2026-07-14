// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
/// OData v4 conformance regressions (#1989):
/// <list type="number">
/// <item><description><c>$top=0</c> returns a 200 with an empty <c>value</c> array (not a 400),
/// while <c>$count=true</c> still reports the true total.</description></item>
/// <item><description>The <c>Layers</c> collection emits a followable <c>@odata.nextLink</c>
/// when more layers exist than the requested <c>$top</c>.</description></item>
/// <item><description>Feature pagination is not truncated: a full page always carries an
/// <c>@odata.nextLink</c> regardless of provider <c>TotalCount</c> fidelity.</description></item>
/// </list>
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.ODataV4)]
public sealed class ODataV4ConformanceEndpointTests : IAsyncLifetime
{
    private const int TestLayerId = 0;

    // The odata.yaml seed publishes 15 features on layer 0 and 2 layers total.
    private const int SeededFeatureCount = 15;
    private const int SeededLayerCount = 2;

    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

    public async Task InitializeAsync()
    {
        // All segments are relative literal path fragments (not user input), so none can be
        // rooted and silently drop earlier arguments.
        _fixture.UseSeed(Path.Combine("tests", "seed", "odata.yaml"));
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$top=0")]
    public async Task Features_TopZero_Returns200WithEmptyValue()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$top=0");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "OData v4 allows $top=0 (a deliberately empty page) and must not return a 400");

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.GetProperty("value").EnumerateArray().Should().BeEmpty();
        root.TryGetProperty("@odata.nextLink", out _).Should().BeFalse(
            "an empty page has no continuation");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$top=0&$count=true")]
    public async Task Features_TopZeroWithCount_ReturnsEmptyValueAndTrueCount()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$top=0&$count=true");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.GetProperty("value").EnumerateArray().Should().BeEmpty();
        root.TryGetProperty("@odata.count", out var count).Should().BeTrue(
            "$count=true must report the total even for a $top=0 page");
        count.GetInt64().Should().Be(SeededFeatureCount,
            "the count is the true total, independent of the empty data page");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$top=-1")]
    public async Task Features_NegativeTop_Returns400()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$top=-1");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a negative $top is still invalid; only $top=0 is allowed");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Layers?$top=1")]
    public async Task Layers_WithTop_EmitsFollowableNextLink()
    {
        var response = await _fixture.Client.GetAsync("/odata/Layers?$top=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.GetProperty("value").EnumerateArray().Should().HaveCount(1,
            "the requested page size is honored");
        root.TryGetProperty("@odata.nextLink", out var nextLink).Should().BeTrue(
            "more layers exist than the requested $top, so a continuation must be emitted (#1989)");
        nextLink.GetString().Should().NotBeNullOrWhiteSpace();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Layers?$top=1")]
    public async Task Layers_PagesThroughAllLayersViaNextLink()
    {
        var seen = new HashSet<int>();
        var relativePath = "/odata/Layers?$top=1";
        var pages = 0;

        while (relativePath != null && pages < 10)
        {
            var response = await _fixture.Client.GetAsync(relativePath);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var content = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;

            foreach (var item in root.GetProperty("value").EnumerateArray())
            {
                seen.Add(item.GetProperty("Id").GetInt32());
            }

            relativePath = root.TryGetProperty("@odata.nextLink", out var nextLink)
                ? ToRelative(nextLink.GetString()!)
                : null;
            pages++;
        }

        seen.Count.Should().Be(SeededLayerCount,
            "every layer must be reachable by following the Layers nextLink");
        pages.Should().BeGreaterThan(1,
            "a 2-layer collection paged at size 1 requires multiple pages");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$top=5")]
    public async Task Features_FullPage_EmitsNextLinkWithoutCount()
    {
        // No $count=true: the provider does not run COUNT(*) OVER(), so nextLink must be
        // derived from the continuation probe (a full page came back), not from TotalCount.
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$top=5&$select=ObjectId");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.GetProperty("value").EnumerateArray().Should().HaveCount(5);
        root.TryGetProperty("@odata.nextLink", out _).Should().BeTrue(
            "a full page over a 15-row layer must carry a continuation even without $count=true (#1989)");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$top=5")]
    public async Task Features_PagesThroughAllRowsWithoutTruncation()
    {
        var seen = new HashSet<int>();
        var relativePath = $"/odata/Features({TestLayerId})?$top=5&$select=ObjectId";
        var pages = 0;

        while (relativePath != null && pages < 20)
        {
            var response = await _fixture.Client.GetAsync(relativePath);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var content = await response.Content.ReadAsStringAsync();
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

        seen.Count.Should().Be(SeededFeatureCount,
            "paging must not truncate; all rows are reachable across pages");
        pages.Should().BeGreaterThan(1);
    }

    private static string ToRelative(string url)
    {
        var uri = new Uri(url, UriKind.RelativeOrAbsolute);
        return uri.IsAbsoluteUri ? uri.PathAndQuery : url;
    }
}
