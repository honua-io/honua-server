// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Catalog;

public sealed partial class PublicCatalogEndpointsTests
{
    [IntegrationTheory]
    [InlineData("service/with/slash")]
    [InlineData("literal%2Fidentity")]
    [InlineData("reserved #?%+")]
    [InlineData("service-ʻāina")]
    [Endpoint("GET /api/v1/catalog/{id}")]
    public async Task GetCatalog_EncodedIdentity_RoundTripsListedIdentifier(string identity)
    {
        var graph = Graph();
        _provider.SetGraph(graph with
        {
            Services = [graph.Services[0] with { Metadata = graph.Services[0].Metadata with { Id = identity } }],
            Publications = [graph.Publications[0] with { ServiceId = identity }]
        });
        using var listed = await ReadAsync(_anonymous, "?type=service");
        Assert.Equal(identity, Assert.Single(listed.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetString());
        using var detail = await _anonymous.GetAsync("/api/v1/catalog/" + Uri.EscapeDataString(identity));
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using var document = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        Assert.Equal(identity, document.RootElement.GetProperty("id").GetString());
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/catalog/{id}")]
    public async Task GetCatalog_EncodedSlashAndLiteralPercent_KeepDistinctIdentities()
    {
        var graph = Graph();
        string[] identities = ["service/one", "service%2Fone"];
        _provider.SetGraph(graph with
        {
            Services = identities.Select((id, index) => graph.Services[0] with
            {
                Metadata = graph.Services[0].Metadata with { Id = id, Name = "service-" + index },
                PublicName = "service-" + index
            }).ToArray(),
            Publications = identities.Select((id, index) => graph.Publications[0] with
            {
                Metadata = graph.Publications[0].Metadata with { Id = "publication-" + index }, ServiceId = id
            }).ToArray()
        });
        foreach (var identity in identities)
        {
            using var response = await _anonymous.GetAsync("/api/v1/catalog/" + Uri.EscapeDataString(identity));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(identity, document.RootElement.GetProperty("id").GetString());
        }
    }
}
