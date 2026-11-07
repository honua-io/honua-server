// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Catalog;

public sealed partial class PublicCatalogEndpointsTests
{
    [IntegrationTest]
    [Endpoint("GET /api/v1/catalog")]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task GetCatalog_WfsLinks_RequireTheSameStorageBindingAsFeatureTypes()
    {
        var graph = Graph();
        graph = graph with { Services = [graph.Services[0] with { Protocols = [ServiceProtocols.Wfs20], PrimaryProtocol = ServiceProtocols.Wfs20 }] };
        _provider.SetGraph(graph);
        using var response = await _anonymous.GetAsync("/api/v1/capabilities/manifest");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var manifest = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains(manifest.RootElement.GetProperty("protocolEndpoints").EnumerateArray(),
            endpoint => endpoint.GetProperty("protocol").GetString() == ServiceProtocols.Wfs20);
        using var unmaterialized = await ReadAsync(_anonymous, "?type=service");
        var service = Assert.Single(unmaterialized.RootElement.GetProperty("items").EnumerateArray());
        Assert.DoesNotContain(service.GetProperty("endpoints").EnumerateArray(),
            endpoint => endpoint.GetProperty("protocol").GetString() == ServiceProtocols.Wfs20);
        _provider.SetGraph(graph with
        {
            Resources = [graph.Resources[0] with { StorageBindingIds = ["binding"] }],
            StorageBindings = [new()
            {
                Metadata = new() { Id = "binding", Name = "binding" }, ResourceId = "resource",
                StorageType = MetadataV2StorageType.RelationalTable, Locator = "public.parcels", StorageLayerId = 0
            }],
            Publications = [graph.Publications[0] with { StorageBindingId = "binding" }]
        });
        using var materialized = await ReadAsync(_anonymous, "?type=service");
        var available = Assert.Single(materialized.RootElement.GetProperty("items").EnumerateArray());
        Assert.Contains(available.GetProperty("endpoints").EnumerateArray(),
            endpoint => endpoint.GetProperty("protocol").GetString() == ServiceProtocols.Wfs20);
    }
}
