// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Tests.Features.Admin;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Catalog;

public sealed partial class CatalogAudienceEndpointsTests
{
    [IntegrationTheory]
    [InlineData("scope", false, 1)]
    [InlineData("include", false, 2)]
    [InlineData("include", true, 1)]
    [InlineData("exclude", false, 1)]
    [Endpoint("GET /api/v1/catalogs/{catalogId}/items")]
    public async Task ListItems_ManualMembership_OverridesOnlyScope(string inclusion, bool hidden, int count)
    {
        var graph = Graph();
        var catalog = graph.Catalogs[1];
        _provider.SetGraph(graph with { Catalogs = [catalog with { Configuration = catalog.Configuration! with
        {
            Scope = new() { Kinds = ["service"], Tags = ["public-tag"] },
            Memberships = [new() { Item = new() { Kind = "service", Id = "staff" }, Inclusion = inclusion, HiddenHere = hidden }]
        } }] });
        using var response = await _member.GetAsync("/api/v1/catalogs/staff/items?limit=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(count, document.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(count, document.RootElement.GetProperty("facets").GetProperty("owner").GetArrayLength());
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/catalogs/{catalogId}/items")]
    public async Task ListItems_ManualInclusionOfRestrictedItem_DoesNotWidenPublicAudience()
    {
        var graph = Graph();
        var catalog = graph.Catalogs[0];
        _provider.SetGraph(graph with { Catalogs = [catalog with { Configuration = catalog.Configuration! with
        {
            Memberships = [new() { Item = new() { Kind = "service", Id = "secret" }, Inclusion = "include" }]
        } }] });
        _member.DefaultRequestHeaders.Add(RlsClaimsTestAuthHandler.RolesHeader, "responder");
        using var response = await _member.GetAsync("/api/v1/catalogs/public/items?kind=service");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("public", Assert.Single(document.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetString());
        Assert.DoesNotContain("secret-tag", document.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/catalogs/{catalogId}/items")]
    public async Task ListItems_HiddenOnlyService_RemovesServiceAndAllFacets()
    {
        var graph = Graph();
        _provider.SetGraph(graph with { Publications = graph.Publications.Select(layer => layer with { Advertised = false }).ToArray() });
        using var response = await _anonymous.GetAsync("/api/v1/catalogs/public/items");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, document.RootElement.GetProperty("total").GetInt32());
        Assert.All(document.RootElement.GetProperty("facets").EnumerateObject(), facet => Assert.Empty(facet.Value.EnumerateArray()));
    }

    [IntegrationTheory]
    [InlineData("slash/id")]
    [InlineData("literal%2Fid")]
    [InlineData("reserved?x=a&b=#")]
    [InlineData("mālama")]
    [Endpoint("GET /api/v1/catalogs/{catalogId}/items/{kind}")]
    public async Task GetItem_OpaqueQueryIdentity_RoundTripsExactly(string id)
    {
        var graph = Graph();
        _provider.SetGraph(graph with
        {
            Services = graph.Services.Select(service => service.Metadata.Id == "public"
                ? service with { Metadata = service.Metadata with { Id = id } } : service).ToArray(),
            Publications = graph.Publications.Select(layer => layer.ServiceId == "public" ? layer with { ServiceId = id } : layer).ToArray()
        });
        using var response = await _anonymous.GetAsync("/api/v1/catalogs/public/items/service?id=" + Uri.EscapeDataString(id));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(id, document.RootElement.GetProperty("id").GetString());
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/catalogs/{catalogId}/items")]
    public async Task ListItems_AccessRevokedAfterRead_DoesNotReturnCachedItemsOrFacets()
    {
        using var first = await _anonymous.GetAsync("/api/v1/catalogs/public/items?kind=service");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var graph = Graph();
        _provider.SetGraph(graph with { Revision = 2, Resources = graph.Resources.Select(dataset => dataset with
        { AccessPolicy = new() { AllowedRoles = ["responder"] } }).ToArray() });
        using var revoked = await _anonymous.GetAsync("/api/v1/catalogs/public/items?kind=service");
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        using var document = JsonDocument.Parse(await revoked.Content.ReadAsStringAsync());
        Assert.Equal(0, document.RootElement.GetProperty("total").GetInt32());
        Assert.Empty(document.RootElement.GetProperty("facets").GetProperty("owner").EnumerateArray());
    }

    [IntegrationTheory]
    [InlineData("?limit=0")]
    [InlineData("?limit=201")]
    [InlineData("?offset=-1")]
    [InlineData("?offset=2147483648")]
    [InlineData("?sort=unknown")]
    [InlineData("?bbox=NaN,0,1,1")]
    [InlineData("?datetime=unknown")]
    [Endpoint("GET /api/v1/catalogs/{catalogId}/items")]
    public async Task ListItems_InvalidQuery_ReturnsBadRequest(string query)
    {
        using var response = await _anonymous.GetAsync("/api/v1/catalogs/public/items" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/catalogs")]
    [Endpoint("GET /api/v1/catalogs/{catalogId}")]
    public async Task GetCatalog_OtherTenant_ConcealsConfigurationAndItems()
    {
        var graph = Graph();
        _provider.SetGraph(graph with { Catalogs = graph.Catalogs.Select(catalog => catalog with
        { Metadata = catalog.Metadata with { Tenant = "other" } }).ToArray() });
        using var listed = await _anonymous.GetAsync("/api/v1/catalogs");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        using var document = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        Assert.Empty(document.RootElement.EnumerateArray());
        using var named = await _anonymous.GetAsync("/api/v1/catalogs/public");
        Assert.Equal(HttpStatusCode.NotFound, named.StatusCode);
    }
    [IntegrationTest]
    [Endpoint("GET /api/v1/catalogs/{catalogId}/items")]
    public async Task ListItems_MixedServiceAccess_DerivesExtentOnlyFromReadableLayers()
    {
        var graph = Graph();
        _provider.SetGraph(graph with
        {
            Services = [graph.Services[0]],
            Resources = graph.Resources.Select(dataset => dataset with
            {
                AccessPolicy = dataset.Metadata.Id == "public-dataset" ? new() { AllowAnonymous = true } : new() { AllowedRoles = ["responder"] },
                Spatial = new()
                {
                    GeometryType = MetadataV2GeometryType.Polygon, SpatialReference = MetadataV2SpatialReference.Wgs84,
                    Bbox = dataset.Metadata.Id == "public-dataset" ? new() { West = -158, South = 20, East = -155, North = 23 }
                        : new() { West = 10, South = 10, East = 20, North = 20 }
                }
            }).ToArray(),
            Publications = graph.Publications.Select(layer => layer with { ServiceId = "public" }).ToArray()
        });
        using var response = await _anonymous.GetAsync("/api/v1/catalogs/public/items?kind=service&bbox=10,10,20,20");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, document.RootElement.GetProperty("total").GetInt32());
        Assert.Empty(document.RootElement.GetProperty("facets").GetProperty("owner").EnumerateArray());
        using var visible = await _anonymous.GetAsync("/api/v1/catalogs/public/items?kind=service&bbox=-158,20,-155,23");
        Assert.Equal(HttpStatusCode.OK, visible.StatusCode);
        using var visibleDocument = JsonDocument.Parse(await visible.Content.ReadAsStringAsync());
        Assert.Equal(1, visibleDocument.RootElement.GetProperty("total").GetInt32());
        var extent = Assert.Single(visibleDocument.RootElement.GetProperty("items").EnumerateArray()).GetProperty("extent");
        Assert.Equal(new[] { -158.0, 20.0, -155.0, 23.0 }, extent.EnumerateArray().Select(value => value.GetDouble()));
    }

}
