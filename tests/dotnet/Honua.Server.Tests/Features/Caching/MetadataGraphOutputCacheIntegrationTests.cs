// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Caching;

[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.Cache)]
public sealed class MetadataGraphOutputCacheIntegrationTests
{
    [UnitTheory]
    [InlineData("ServiceMetadata")]
    [InlineData("MapServerLegend")]
    [InlineData("ServiceDirectory")]
    [InlineData("LayerMetadata")]
    [InlineData("OgcCollections")]
    [InlineData("OgcCollection")]
    [InlineData("OgcTilesCollections")]
    [InlineData("OgcTilesCollection")]
    [InlineData("TileJson")]
    [InlineData("ImageServerMetadata")]
    [InlineData("StacCatalog")]
    [InlineData("StacCollections")]
    [InlineData("StacCollection")]
    public async Task MetadataPolicy_AdvertisingChanges_ReplacesActuallyCachedDiscovery(string policy)
    {
        var graph = new MetadataV2Graph
        {
            Revision = 1,
            Services = [new() { Metadata = new() { Id = "service", Name = "service" } }],
            Resources = [new() { Metadata = new() { Id = "resource", Name = "resource" } }],
            Publications = [new() { Metadata = new() { Id = "publication", Name = "publication" }, ServiceId = "service", ResourceId = "resource" }]
        };
        var provider = new TestMetadataV2GraphProvider(graph);
        var invocations = 0;
        using var host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddSingleton<IMetadataV2GraphProvider>(provider);
                services.AddSingleton<IOptions<LimitsOptions>>(Options.Create(new LimitsOptions { Tiles = new TileLimits() }));
                ObservabilityServiceCollectionExtensions.ConfigureOutputCaching(services, new ConfigurationBuilder().Build());
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseOutputCache();
                app.UseEndpoints(endpoints => endpoints.MapGet("/catalog-cache", async () =>
                {
                    Interlocked.Increment(ref invocations);
                    var snapshot = await provider.GetCurrentAsync();
                    return Results.Json(snapshot.Graph.Publications.Where(snapshot.IsDiscoverable).Select(publication => publication.Metadata.Id).ToArray());
                }).CacheOutput(policy));
            })).StartAsync();
        using var client = host.GetTestClient();
        Assert.Equal("[\"publication\"]", await client.GetStringAsync("/catalog-cache"));
        Assert.Equal("[\"publication\"]", await client.GetStringAsync("/catalog-cache"));
        Assert.Equal(1, invocations);
        provider.SetGraph(graph with { Revision = 2, Publications = [graph.Publications[0] with { Advertised = false }] });
        Assert.Equal("[]", await client.GetStringAsync("/catalog-cache"));
        Assert.Equal("[]", await client.GetStringAsync("/catalog-cache"));
        Assert.Equal(2, invocations);
    }
}
