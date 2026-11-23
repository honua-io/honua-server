// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Console;

public sealed partial class PublishedContentProjectionTests
{
    [IntegrationTheory]
    [InlineData("?cursor=aGVsbG8%3D")]
    [InlineData("?cursor=%25%25%25%25")]
    [InlineData("?cursor=LTE%3D")]
    [InlineData("/search?q=Parcels&cursor=aGVsbG8%3D")]
    [Endpoint("GET /api/v1/console/content")]
    [Endpoint("GET /api/v1/console/content/search")]
    public async Task ListContent_InvalidCursor_IsRefusedInsteadOfRestartingAtFirstPage(string suffix)
    {
        using var response = await _admin.GetAsync("/api/v1/console/content" + suffix);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Invalid cursor", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/content")]
    public async Task ListContent_NextCursor_ReturnsTheFollowingPage()
    {
        _provider.SetGraph(Graph());
        using var first = await ReadContentAsync("?limit=1");
        var total = first.RootElement.GetProperty("total").GetInt32();
        Assert.True(total >= 2, $"The seeded graph should project at least two items, found {total}.");
        var firstId = first.RootElement.GetProperty("items")[0].GetProperty("id").GetString();
        var cursor = first.RootElement.GetProperty("nextCursor").GetString();

        using var second = await ReadContentAsync("?limit=1&cursor=" + Uri.EscapeDataString(cursor!));

        Assert.NotEqual(firstId, second.RootElement.GetProperty("items")[0].GetProperty("id").GetString());
    }
}
