// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Admin;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Layer-index configuration writes address one routable feature or table resource.
/// A shared index is a conflict; a raster sidecar and a retired twin are not (honua-server#6111).
/// </summary>
public sealed class AdminLayerConfigurationTargetTests
{
    private static readonly MetadataV2Status Active = new() { Lifecycle = MetadataV2LifecycleStatus.Active };

    [UnitTest]
    public void Resolve_TwoRoutableFeatureResources_IsAmbiguousAndNamesBothServices()
    {
        var snapshot = Graph(
            Layer("res-a", "svc-a", "parcels", layerIndex: 0),
            Layer("res-b", "svc-b", "twin-service-6111", layerIndex: 0));

        var target = AdminLayerConfigurationTargets.Resolve(snapshot, 0);

        target.IsAmbiguous.Should().BeTrue();
        target.Resource.Should().BeNull();
        target.ConflictDetail.Should().Contain("parcels").And.Contain("twin-service-6111");
    }

    [UnitTest]
    public async Task Write_AmbiguousLayerIndex_DoesNotSave()
    {
        var store = new RecordingGraphStore(Graph(
            Layer("res-a", "svc-a", "parcels", layerIndex: 0),
            Layer("res-b", "svc-b", "roads", layerIndex: 0)));

        var target = await AdminLayerConfigurationTargets.WriteAsync(
            store,
            0,
            resource => resource with { PermanentFilter = new MetadataV2PermanentFilter { Expression = "1=1" } },
            CancellationToken.None);

        target.IsAmbiguous.Should().BeTrue();
        store.SaveCount.Should().Be(0);
    }

    [UnitTest]
    public async Task Write_RetiredTwinAndRasterSidecar_UpdatesOnlyTheRoutableFeature()
    {
        var store = new RecordingGraphStore(Graph(
            Layer("res-feature", "svc-a", "parcels", layerIndex: 0),
            Layer("res-twin", "svc-b", "twin", layerIndex: 0, retired: true),
            Layer("res-raster", "svc-image", "image", layerIndex: 0, type: MetadataV2ResourceType.RasterDataset)));

        var target = await AdminLayerConfigurationTargets.WriteAsync(
            store,
            0,
            resource => resource with
            {
                PermanentFilter = new MetadataV2PermanentFilter { Expression = "category = 'test'", Language = "arcgis-sql" }
            },
            CancellationToken.None);

        target.IsAmbiguous.Should().BeFalse();
        target.Resource!.Metadata.Id.Should().Be("res-feature");
        store.SaveCount.Should().Be(1);
        var saved = store.LastSaved!.Resources.ToDictionary(resource => resource.Metadata.Id, StringComparer.Ordinal);
        saved["res-feature"].PermanentFilter!.Expression.Should().Be("category = 'test'");
        saved["res-twin"].PermanentFilter.Should().BeNull();
        saved["res-raster"].PermanentFilter.Should().BeNull();
    }

    [UnitTest]
    public void Resolve_LegacyPublicationWithoutIdentifier_MatchesLayerIndex()
    {
        var snapshot = Graph(Layer("res-legacy", "svc-a", "parcels", layerIndex: 4, legacyIdentifier: true));

        var target = AdminLayerConfigurationTargets.Resolve(snapshot, 4);

        target.IsAmbiguous.Should().BeFalse();
        target.Resource!.Metadata.Id.Should().Be("res-legacy");
        snapshot.Graph.Publications.Single().Identifier.Should().BeNull();
    }

    private static MetadataV2GraphSnapshot Graph(params MetadataV2Graph[] parts)
    {
        return new MetadataV2GraphSnapshot(
            new MetadataV2Graph
            {
                Resources = parts.SelectMany(part => part.Resources).ToArray(),
                Services = parts.SelectMany(part => part.Services).ToArray(),
                Publications = parts.SelectMany(part => part.Publications).ToArray(),
            },
            "etag-1",
            DateTimeOffset.UnixEpoch);
    }

    private static MetadataV2Graph Layer(
        string resourceId,
        string serviceId,
        string serviceName,
        int layerIndex,
        bool retired = false,
        bool legacyIdentifier = false,
        MetadataV2ResourceType type = MetadataV2ResourceType.FeatureDataset)
    {
        return new MetadataV2Graph
        {
            Resources =
            [
                new MetadataV2Resource
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = resourceId, Name = resourceId },
                    Type = type,
                    Status = retired
                        ? new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Retired }
                        : Active,
                    SchemaFields = [new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String }],
                }
            ],
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = serviceId, Name = serviceName },
                    Status = Active,
                }
            ],
            Publications =
            [
                new MetadataV2Publication
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "pub-" + resourceId, Name = resourceId },
                    Status = Active,
                    ServiceId = serviceId,
                    ResourceId = resourceId,
                    Identifier = legacyIdentifier
                        ? null!
                        : new MetadataV2PublicationIdentifier
                        {
                            Value = layerIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            IsNumeric = true,
                        },
                    LayerIndex = layerIndex,
                }
            ],
        };
    }

    private sealed class RecordingGraphStore(MetadataV2GraphSnapshot initial) : IMetadataV2GraphStore
    {
        private MetadataV2GraphSnapshot _current = initial;

        public int SaveCount { get; private set; }

        public MetadataV2Graph? LastSaved { get; private set; }

        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_current);

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<MetadataV2GraphSnapshot?>(null);

        public Task<MetadataV2GraphSnapshot> SaveAsync(
            MetadataV2Graph graph,
            string? expectedEtag,
            CancellationToken cancellationToken = default)
        {
            SaveCount++;
            LastSaved = graph;
            _current = new MetadataV2GraphSnapshot(graph, "etag-2", DateTimeOffset.UnixEpoch);
            return Task.FromResult(_current);
        }

        public Task<MetadataV2GraphSnapshot> ActivateRevisionAsync(
            long revision,
            string? expectedCurrentEtag,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
