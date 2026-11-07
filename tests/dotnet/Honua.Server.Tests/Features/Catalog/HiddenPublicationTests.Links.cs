// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Catalog;

public sealed partial class HiddenPublicationTests
{
    [IntegrationTest]
    [Endpoint("GET /api/v1/catalog")]
    public async Task GetCatalog_ProtocolEndpointLinks_ResolveForPublishedGraph()
    {
        using var response = await _fixture.Client.GetAsync("/api/v1/catalog?type=service");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var links = document.RootElement.GetProperty("items").EnumerateArray()
            .SelectMany(item => item.GetProperty("endpoints").EnumerateArray())
            .Select(endpoint => endpoint.GetProperty("href").GetString()!).Distinct(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(links);
        foreach (var href in links)
        {
            using var target = await _fixture.Client.GetAsync(href);
            Assert.Equal(HttpStatusCode.OK, target.StatusCode);
        }
    }

    [IntegrationTest]
    [Endpoint("GET /stac/search")]
    public async Task StacSearch_HiddenCollection_ResolvesExplicitCollectionButOmitsUnqualifiedSearch()
    {
        using var before = await _fixture.Client.GetAsync("/stac/search");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        using var document = JsonDocument.Parse(await before.Content.ReadAsStringAsync());
        var features = document.RootElement.GetProperty("features").EnumerateArray().ToArray();
        Assert.NotEmpty(features);
        var collection = features[0].GetProperty("collection").GetString()!;
        SetAdvertising(false);
        using var named = await _fixture.Client.GetAsync("/stac/search?collections=" + Uri.EscapeDataString(collection));
        Assert.Equal(HttpStatusCode.OK, named.StatusCode);
        using var namedResults = JsonDocument.Parse(await named.Content.ReadAsStringAsync());
        Assert.NotEmpty(namedResults.RootElement.GetProperty("features").EnumerateArray());
        using var unqualified = await _fixture.Client.GetAsync("/stac/search");
        Assert.Equal(HttpStatusCode.OK, unqualified.StatusCode);
        using var hiddenResults = JsonDocument.Parse(await unqualified.Content.ReadAsStringAsync());
        Assert.Empty(hiddenResults.RootElement.GetProperty("features").EnumerateArray());
    }
}
