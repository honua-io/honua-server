// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Catalog;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.GetMetadata)]
public sealed class CatalogEndpointLinksTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Enterprise);

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var provider = (TestMetadataV2GraphProvider)_fixture.GetService<IMetadataV2GraphProvider>();
        provider.SetGraph(snapshot.Graph with { Catalogs = [new()
        {
            Metadata = new() { Id = "links", Name = "Links" }, Target = "discovery",
            Configuration = new() { DefaultListing = "automatic", Scope = new() { Kinds = ["service", "layer"] } }
        }] }, schema: _fixture.CurrentSchema);
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/catalogs/{catalogId}/items")]
    public async Task ListItems_ManifestProtocolEndpoints_EveryAdvertisedLinkResolves()
    {
        using var response = await _fixture.Client.GetAsync("/api/v1/catalogs/links/items?kind=service");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var endpoints = document.RootElement.GetProperty("items").EnumerateArray()
            .SelectMany(item => item.GetProperty("endpoints").EnumerateArray()).ToArray();
        Assert.NotEmpty(endpoints);
        Assert.Contains(endpoints, endpoint => endpoint.GetProperty("protocol").GetString() == ServiceProtocols.FeatureServer);
        Assert.Contains(endpoints, endpoint => endpoint.GetProperty("protocol").GetString() == ServiceProtocols.OgcFeatures);
        foreach (var href in endpoints.Select(endpoint => endpoint.GetProperty("href").GetString()!).Distinct(StringComparer.Ordinal))
        {
            using var target = await _fixture.Client.GetAsync(href);
            Assert.Equal(HttpStatusCode.OK, target.StatusCode);
        }
    }
}
