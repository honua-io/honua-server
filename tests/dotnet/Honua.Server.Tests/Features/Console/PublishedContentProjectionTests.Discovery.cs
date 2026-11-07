// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Console;

public sealed partial class PublishedContentProjectionTests
{
    [IntegrationTest]
    [Endpoint("GET /api/v1/console/content")]
    [Endpoint("GET /api/v1/console/content/{id}")]
    [Endpoint("GET /sharing/rest/search")]
    public async Task GetContent_HiddenPublication_RemainsReadOnlyInManagementAndAbsentFromPortal()
    {
        var graph = Graph();
        _provider.SetGraph(graph with { Publications = [graph.Publications[0] with { Advertised = false }] });
        using var management = await ReadContentAsync("?itemType=layer");
        var layer = Assert.Single(management.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("publication", layer.GetProperty("id").GetString());
        Assert.Equal(["view"], layer.GetProperty("actions").EnumerateArray().Select(action => action.GetString()));
        using var detail = await _admin.GetAsync("/api/v1/console/content/publication");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using var anonymous = _fixture.CreateClient();
        using var response = await anonymous.GetAsync("/sharing/rest/search?f=json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var portal = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, portal.RootElement.GetProperty("total").GetInt32());
    }
}
