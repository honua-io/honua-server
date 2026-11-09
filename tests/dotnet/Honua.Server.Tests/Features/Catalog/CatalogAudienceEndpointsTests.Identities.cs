// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Catalog;

public sealed partial class CatalogAudienceEndpointsTests
{
    [IntegrationTest]
    [Endpoint("GET /api/v1/catalogs/{catalogId}/items")]
    public async Task ListItems_SharedDatasetLayers_PreservesEveryCanonicalLayerIdentity()
    {
        var graph = Graph();
        var layer = graph.Publications[0];
        _provider.SetGraph(graph with
        {
            Publications = [layer, layer with
            {
                Metadata = layer.Metadata with { Id = "second-layer" }, IsPrimary = false,
                Identifier = new() { Value = "1", IsNumeric = true }
            }]
        });
        using var response = await _anonymous.GetAsync("/api/v1/catalogs/public/items?kind=layer");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, document.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(new[] { "public-layer", "second-layer" }, document.RootElement.GetProperty("items")
            .EnumerateArray().Select(item => item.GetProperty("id").GetString()).Order(StringComparer.Ordinal));
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/catalogs/{catalogId}/items")]
    public async Task ListItems_DatasetWithVisibleExposure_UsesDatasetIdentityAndCountsOnce()
    {
        var graph = Graph();
        var layer = graph.Publications[0];
        _provider.SetGraph(graph with { Publications = [layer, layer with
        { Metadata = layer.Metadata with { Id = "second-layer" }, IsPrimary = false }] });
        using var response = await _anonymous.GetAsync("/api/v1/catalogs/public/items?kind=dataset");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, document.RootElement.GetProperty("total").GetInt32());
        var item = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("public-dataset", item.GetProperty("id").GetString());
        Assert.Equal("feature-type", item.GetProperty("datasetKind").GetString());
        Assert.Equal(1, Assert.Single(document.RootElement.GetProperty("facets").GetProperty("owner").EnumerateArray())
            .GetProperty("count").GetInt32());
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/catalogs/{catalogId}/items")]
    public async Task ListItems_FolderScope_SelectsExactFolderIdentity()
    {
        var graph = Graph();
        var catalog = graph.Catalogs[1];
        _provider.SetGraph(graph with
        {
            Catalogs = [catalog with { Configuration = catalog.Configuration! with
            { Scope = new() { FolderIds = ["survey"], Kinds = ["service"] } } }],
            Folders = [new() { Metadata = new() { Id = "survey", Name = "Survey" } },
                new() { Metadata = new() { Id = "survey/parcels", Name = "Parcels" } }],
            Services = graph.Services.Select(service => service with
            { FolderId = service.Metadata.Id == "public" ? "survey" : "survey/parcels" }).ToArray()
        });
        using var response = await _member.GetAsync("/api/v1/catalogs/staff/items?kind=service");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("public", Assert.Single(document.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal("survey", Assert.Single(document.RootElement.GetProperty("facets").GetProperty("folder").EnumerateArray()).GetProperty("value").GetString());
    }
}
