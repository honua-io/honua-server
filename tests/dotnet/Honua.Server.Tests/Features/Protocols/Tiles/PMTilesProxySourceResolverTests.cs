// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Protocols.Tiles.PMTilesProxy;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Protocols.Tiles;

public sealed class PMTilesProxySourceResolverTests
{
    [Fact]
    public async Task Resolve_StampedPublication_ReturnsThatSource()
    {
        var snapshot = await SnapshotAsync(SampleGraph());

        var source = PMTilesProxySourceResolver.Resolve(snapshot, new Dictionary<string, string>
        {
            ["layerId"] = "0",
            ["serviceId"] = "svc-parcels",
            ["publicationId"] = "pub-parcels",
            ["resourceId"] = "res-parcels",
        });

        source.Should().NotBeNull();
        source!.Service.Metadata.Id.Should().Be("svc-parcels");
        source.Publication.Metadata.Id.Should().Be("pub-parcels");
        source.Resource.Metadata.Id.Should().Be("res-parcels");
    }

    [Fact]
    public async Task Resolve_LowercasedProviderKeys_StillBindsTheSource()
    {
        var snapshot = await SnapshotAsync(SampleGraph());
        var stamped = PMTilesProxySourceResolver.StampPublishMetadata(
            ImmutableDictionary<string, string>.Empty.Add("layerId", "0"),
            PMTilesProxySourceResolver.ResolveForPublish(snapshot, "parcels", 0));
        var lowered = stamped.ToDictionary(pair => pair.Key.ToLowerInvariant(), pair => pair.Value);

        var source = PMTilesProxySourceResolver.Resolve(snapshot, lowered);

        source.Should().NotBeNull();
        source!.Publication.Metadata.Id.Should().Be("pub-parcels");
    }

    [Fact]
    public async Task Resolve_RetiredPublication_ReturnsNull()
    {
        var graph = SampleGraph();
        var retired = graph with
        {
            Publications = graph.Publications.Select(publication => publication with
            {
                Status = publication.Status with { Lifecycle = MetadataV2LifecycleStatus.Retired }
            }).ToArray()
        };
        var snapshot = await SnapshotAsync(retired);

        PMTilesProxySourceResolver.Resolve(snapshot, StampedMetadata()).Should().BeNull();
    }

    [Fact]
    public async Task Resolve_ReboundResource_ReturnsNull()
    {
        var snapshot = await SnapshotAsync(SampleGraph());
        var metadata = StampedMetadata();
        metadata["resourceId"] = "res-other";

        PMTilesProxySourceResolver.Resolve(snapshot, metadata).Should().BeNull();
    }

    [Fact]
    public async Task Resolve_DuplicateMetadataKey_ReturnsNull()
    {
        var snapshot = await SnapshotAsync(SampleGraph());

        PMTilesProxySourceResolver.Resolve(snapshot, new[]
        {
            new KeyValuePair<string, string>("layerId", "0"),
            new KeyValuePair<string, string>("LAYERID", "0"),
            new KeyValuePair<string, string>("publicationId", "pub-parcels"),
        }).Should().BeNull();
    }

    [Fact]
    public async Task Resolve_LegacyLayerIdOnly_StillBindsARoutableSource()
    {
        var snapshot = await SnapshotAsync(SampleGraph());

        var source = PMTilesProxySourceResolver.Resolve(snapshot, new Dictionary<string, string>
        {
            ["layerId"] = "0",
        });

        source.Should().NotBeNull();
        source!.Resource.Metadata.Id.Should().Be("res-parcels");
    }

    [Fact]
    public async Task ResolveForPublish_NamedService_BindsThatServicesLayer()
    {
        var snapshot = await SnapshotAsync(SampleGraph());

        var source = PMTilesProxySourceResolver.ResolveForPublish(snapshot, "parcels", 0);

        source.Should().NotBeNull();
        source!.Service.Metadata.Id.Should().Be("svc-parcels");
        source.Publication.Metadata.Id.Should().Be("pub-parcels");
    }

    private static Dictionary<string, string> StampedMetadata() => new()
    {
        ["layerId"] = "0",
        ["serviceId"] = "svc-parcels",
        ["publicationId"] = "pub-parcels",
        ["resourceId"] = "res-parcels",
    };

    private static MetadataV2Graph SampleGraph()
        => new TestMetadataV2GraphBuilder()
            .AddResource("res-parcels", "parcels")
            .AddStorageBinding("binding-parcels", "res-parcels", "features", storageLayerId: 0)
            .AddService("svc-parcels", "parcels")
            .AddPublication("pub-parcels", "svc-parcels", "res-parcels", layerIndex: 0)
            .Build();

    private static async Task<MetadataV2GraphSnapshot> SnapshotAsync(MetadataV2Graph graph)
        => await new TestMetadataV2GraphProvider(graph).GetCurrentAsync();
}
