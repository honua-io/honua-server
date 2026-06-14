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
        _fixture.UseSeed(Path.Combine("tests", "seed", "odata.yaml"));
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
        nextLink.GetString().Should().Contain($"$top={MaxPageSize}",
            "the nextLink carries the clamped page size so clients page at a safe size");
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

    private static string ToRelative(string url)
    {
        var uri = new Uri(url, UriKind.RelativeOrAbsolute);
        return uri.IsAbsoluteUri ? uri.PathAndQuery : url;
    }
}
