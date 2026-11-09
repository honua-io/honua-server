// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Catalog;

public sealed partial class HiddenLayerTests
{
    [IntegrationTest]
    [Protocol(TestProtocols.Stac)]
    [Operation(Operations.Query)]
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
