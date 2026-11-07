// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Studio.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Catalog;

public sealed partial class PublicCatalogEndpointsTests
{
    [IntegrationTest]
    [Endpoint("GET /api/v1/catalog")]
    public async Task GetCatalog_AppSources_ResolvesStoredBindingsWithoutDeclaredServiceEdges()
    {
        var (draft, _) = await CreateAppAsync("public");
        var updated = await Store.UpdateDraftAsync(draft with
        {
            Envelope = draft.Envelope with
            {
                Dependencies = [],
                Body = JsonSerializer.SerializeToElement(new
                {
                    sourceBindings = new[] { new { sourceId = "parcels", locator = new { serviceId = "service", layerId = 0 } } }
                })
            }
        });
        var version = await Store.CreateVersionAsync(updated!, null, "app-owner");
        await PublishAppAsync(version, "public");
        using var result = await ReadAsync(_anonymous, "?type=app");
        var app = Assert.Single(result.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("service", Assert.Single(app.GetProperty("services").EnumerateArray()).GetString());
        using var services = await ReadAsync(_anonymous, "?type=service");
        var service = Assert.Single(services.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(version.ItemId.ToString("D"), Assert.Single(service.GetProperty("apps").EnumerateArray()).GetString());
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/catalog")]
    public async Task GetCatalog_AppLineage_HiddenServiceContributesNoIdentifierOrFacet()
    {
        var (_, version) = await CreateAppAsync("public");
        await PublishAppAsync(version, "public");
        var graph = Graph();
        _provider.SetGraph(graph with { Publications = [graph.Publications[0] with { Advertised = false }] });
        using var result = await ReadAsync(_anonymous);
        var app = Assert.Single(result.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("app", app.GetProperty("type").GetString());
        Assert.Empty(app.GetProperty("services").EnumerateArray());
        Assert.Equal(1, result.RootElement.GetProperty("total").GetInt32());
        Assert.DoesNotContain("surveyor", result.RootElement.GetProperty("facets").GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("publication", app.GetRawText(), StringComparison.Ordinal);
    }

    [IntegrationTheory]
    [InlineData("public", 1)]
    [InlineData("personal", 0)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items")]
    public async Task GetRecords_PublishedApp_UsesSameVisibilityAndLineage(string visibility, int expectedCount)
    {
        var (_, version) = await CreateAppAsync(visibility);
        await PublishAppAsync(version, visibility);
        using var response = await _anonymous.GetAsync("/ogc/records/collections/honua-catalog/items?type=app");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedCount, result.RootElement.GetProperty("numberMatched").GetInt32());
        Assert.Equal(expectedCount, result.RootElement.GetProperty("features").GetArrayLength());
        if (expectedCount == 1)
        {
            var app = result.RootElement.GetProperty("features")[0];
            Assert.Equal(version.ItemId.ToString("D"), app.GetProperty("id").GetString());
            Assert.Equal("Survey app", app.GetProperty("properties").GetProperty("title").GetString());
            Assert.Equal("service", Assert.Single(app.GetProperty("properties").GetProperty("services").EnumerateArray()).GetString());
        }
        else
        {
            Assert.DoesNotContain(version.ItemId.ToString("D"), result.RootElement.GetRawText(), StringComparison.Ordinal);
        }
    }
}
