// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Catalog;

public sealed partial class CatalogAudienceEndpointsTests
{
    [IntegrationTheory]
    [InlineData(null, HttpStatusCode.OK)]
    [InlineData(MetadataV2LifecycleStatus.Active, HttpStatusCode.OK)]
    [InlineData(MetadataV2LifecycleStatus.Deprecated, HttpStatusCode.OK)]
    [InlineData(MetadataV2LifecycleStatus.Draft, HttpStatusCode.NotFound)]
    [InlineData(MetadataV2LifecycleStatus.Retired, HttpStatusCode.NotFound)]
    [InlineData(MetadataV2LifecycleStatus.Unknown, HttpStatusCode.NotFound)]
    [Endpoint("GET /api/v1/catalogs/{catalogId}")]
    public async Task GetCatalog_Lifecycle_UsesSharedServingSemantics(MetadataV2LifecycleStatus? lifecycle, HttpStatusCode expected)
    {
        var graph = Graph();
        var catalog = graph.Catalogs[0];
        _provider.SetGraph(graph with { Catalogs = [catalog with
        { Status = lifecycle is { } value ? new() { Lifecycle = value } : null! }] });
        using var response = await _anonymous.GetAsync("/api/v1/catalogs/public");
        Assert.Equal(expected, response.StatusCode);
    }

    [IntegrationTheory]
    [InlineData("service")]
    [InlineData("layer")]
    [InlineData("dataset")]
    [Endpoint("GET /api/v1/catalogs/{catalogId}/items")]
    public async Task ListItems_DeprecatedEntity_ReportsStatusInItemsAndFacets(string kind)
    {
        var graph = Graph();
        var deprecated = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Deprecated };
        _provider.SetGraph(graph with
        {
            Services = graph.Services.Select(service => kind == "service" ? service with { Status = deprecated } : service).ToArray(),
            Resources = graph.Resources.Select(dataset => kind == "dataset" ? dataset with { Status = deprecated } : dataset).ToArray(),
            Publications = graph.Publications.Select(layer => kind == "layer" ? layer with { Status = deprecated } : layer).ToArray()
        });
        using var response = await _anonymous.GetAsync("/api/v1/catalogs/public/items?kind=" + kind + "&status=deprecated");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, document.RootElement.GetProperty("total").GetInt32());
        Assert.Equal("deprecated", Assert.Single(document.RootElement.GetProperty("items").EnumerateArray()).GetProperty("status").GetString());
        var facet = Assert.Single(document.RootElement.GetProperty("facets").GetProperty("status").EnumerateArray());
        Assert.Equal("deprecated", facet.GetProperty("value").GetString());
        Assert.Equal(1, facet.GetProperty("count").GetInt32());
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/catalogs/{catalogId}/items")]
    public async Task ListItems_NonDatasetTarget_DoesNotInventDatasetIdentity()
    {
        var graph = Graph();
        _provider.SetGraph(graph with { Resources = graph.Resources.Select(resource => resource with
        { Type = MetadataV2ResourceType.Map }).ToArray() });
        using var response = await _anonymous.GetAsync("/api/v1/catalogs/public/items?kind=dataset");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, document.RootElement.GetProperty("total").GetInt32());
        Assert.Empty(document.RootElement.GetProperty("items").EnumerateArray());
    }
}
