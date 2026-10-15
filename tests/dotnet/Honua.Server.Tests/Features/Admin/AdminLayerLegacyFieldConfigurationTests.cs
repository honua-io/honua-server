// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Admin;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Legacy snapshots omit <c>identifier</c> and keep only <c>layerIndex</c>. Field and filter
/// writes must still succeed, and a fields PUT must not commit the field store when the graph
/// write fails (honua-server#6179).
/// </summary>
public sealed class AdminLayerLegacyFieldConfigurationTests
{
    private static readonly MetadataV2Status Active = new() { Lifecycle = MetadataV2LifecycleStatus.Active };

    [UnitTest]
    public async Task ApplyFields_LegacySnapshotWithoutIdentifier_WritesGraphThenStore()
    {
        var store = new RecordingGraphStore(LegacySnapshot(layerIndex: 4));
        var fields = new RecordingFieldStore(store)
        {
            Existing =
            [
                new LayerFieldConfiguration("name", Alias: null, Domain: null, Hidden: false),
                new LayerFieldConfiguration("notes", Alias: "Notes", Domain: null, Hidden: false),
            ],
        };
        var updates = new LayerFieldConfigurationUpdate[]
        {
            new("name", "Display name", Domain: null, Hidden: true),
        };

        var applied = await AdminLayerFieldConfigurationEndpoints.ApplyUpdatedFieldConfigurationsAsync(
            store, fields, 4, updates, CancellationToken.None);

        applied.IsAmbiguous.Should().BeFalse();
        store.SaveCount.Should().Be(1);
        fields.GetCount.Should().Be(1);
        fields.UpdateCount.Should().Be(1);
        fields.UpdateStartedAfterGraphSave.Should().BeTrue();
        var saved = store.LastSaved!.Resources.Single().SchemaFields.Single(field => field.Name == "name");
        saved.Alias.Should().Be("Display name");
        saved.Hidden.Should().BeTrue();
        store.LastSaved.Resources.Single().SchemaFields.Single(field => field.Name == "notes").Alias.Should().Be("Notes");
        applied.Configurations.Should().ContainSingle(field => field.Name == "name" && field.Alias == "Display name" && field.Hidden);
        store.LastSaved.Publications.Single().Identifier.Should().BeNull();
    }

    [UnitTest]
    public async Task ApplyFields_GraphWriteFails_DoesNotCommitTheFieldStore()
    {
        var store = new RecordingGraphStore(LegacySnapshot(layerIndex: 4))
        {
            SaveError = new InvalidOperationException("graph failed"),
        };
        var fields = new RecordingFieldStore(store)
        {
            Existing = [new LayerFieldConfiguration("name", null, null, false)],
        };

        var apply = () => AdminLayerFieldConfigurationEndpoints.ApplyUpdatedFieldConfigurationsAsync(
            store,
            fields,
            4,
            [new LayerFieldConfigurationUpdate("name", "Display name", null, true)],
            CancellationToken.None);

        await apply.Should().ThrowAsync<InvalidOperationException>().WithMessage("graph failed");
        fields.GetCount.Should().Be(1);
        fields.UpdateCount.Should().Be(0);
    }

    [UnitTest]
    public async Task ApplyFields_AmbiguousLayerIndex_DoesNotTouchTheFieldStore()
    {
        var store = new RecordingGraphStore(new MetadataV2GraphSnapshot(
            new MetadataV2Graph
            {
                Resources =
                [
                    Feature("res-a"),
                    Feature("res-b"),
                ],
                Services =
                [
                    Service("svc-a", "parcels"),
                    Service("svc-b", "roads"),
                ],
                Publications =
                [
                    Publication("res-a", "svc-a", 0, legacyIdentifier: true),
                    Publication("res-b", "svc-b", 0, legacyIdentifier: true),
                ],
            },
            "etag-1",
            DateTimeOffset.UnixEpoch));
        var fields = new RecordingFieldStore(store);

        var applied = await AdminLayerFieldConfigurationEndpoints.ApplyUpdatedFieldConfigurationsAsync(
            store,
            fields,
            0,
            [new LayerFieldConfigurationUpdate("name", "Display name", null, false)],
            CancellationToken.None);

        applied.IsAmbiguous.Should().BeTrue();
        applied.ConflictDetail.Should().Contain("parcels").And.Contain("roads");
        fields.GetCount.Should().Be(0);
        fields.UpdateCount.Should().Be(0);
        store.SaveCount.Should().Be(0);
    }

    [UnitTest]
    public async Task WriteFilter_LegacySnapshotWithoutIdentifier_UpdatesOnlyThatResource()
    {
        var store = new RecordingGraphStore(LegacySnapshot(layerIndex: 4));

        var written = await AdminLayerConfigurationTargets.WriteAsync(
            store,
            4,
            resource => resource with
            {
                PermanentFilter = new MetadataV2PermanentFilter
                {
                    Expression = "category = 'test'",
                    Language = "arcgis-sql",
                },
            },
            CancellationToken.None);

        written.IsAmbiguous.Should().BeFalse();
        store.SaveCount.Should().Be(1);
        store.LastSaved!.Resources.Single().PermanentFilter!.Expression.Should().Be("category = 'test'");
        store.LastSaved.Publications.Single().Identifier.Should().BeNull();
    }

    private static MetadataV2GraphSnapshot LegacySnapshot(int layerIndex)
    {
        return new MetadataV2GraphSnapshot(
            new MetadataV2Graph
            {
                Resources = [Feature("res-legacy")],
                Services = [Service("svc-legacy", "parcels")],
                Publications = [Publication("res-legacy", "svc-legacy", layerIndex, legacyIdentifier: true)],
            },
            "etag-1",
            DateTimeOffset.UnixEpoch);
    }

    private static MetadataV2Resource Feature(string id) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = id, Name = id },
        Type = MetadataV2ResourceType.FeatureDataset,
        Status = Active,
        SchemaFields =
        [
            new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String },
            new MetadataV2Field { Name = "notes", Type = MetadataV2FieldType.String, Alias = "Notes" },
        ],
    };

    private static MetadataV2Service Service(string id, string name) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = id, Name = name },
        Status = Active,
    };

    private static MetadataV2Publication Publication(string resourceId, string serviceId, int layerIndex, bool legacyIdentifier)
        => new()
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
        };

    private sealed class RecordingGraphStore(MetadataV2GraphSnapshot initial) : IMetadataV2GraphStore
    {
        private MetadataV2GraphSnapshot _current = initial;

        public int SaveCount { get; private set; }

        public MetadataV2Graph? LastSaved { get; private set; }

        public InvalidOperationException? SaveError { get; init; }

        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_current);

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<MetadataV2GraphSnapshot?>(null);

        public Task<MetadataV2GraphSnapshot> SaveAsync(
            MetadataV2Graph graph,
            string? expectedEtag,
            CancellationToken cancellationToken = default)
        {
            if (SaveError is not null)
            {
                throw SaveError;
            }

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

    private sealed class RecordingFieldStore(RecordingGraphStore graph) : ILayerFieldConfigurationStore
    {
        public int GetCount { get; private set; }

        public int UpdateCount { get; private set; }

        public bool UpdateStartedAfterGraphSave { get; private set; }

        public IReadOnlyList<LayerFieldConfiguration> Existing { get; init; } = [];

        public Task<IReadOnlyList<LayerFieldConfiguration>> GetFieldConfigurationsAsync(
            int layerId,
            CancellationToken cancellationToken = default)
        {
            GetCount++;
            return Task.FromResult(Existing);
        }

        public Task<IReadOnlyList<LayerFieldConfiguration>> UpdateFieldConfigurationsAsync(
            int layerId,
            IReadOnlyList<LayerFieldConfigurationUpdate> updates,
            CancellationToken cancellationToken = default)
        {
            UpdateCount++;
            UpdateStartedAfterGraphSave = graph.SaveCount > 0;
            var updated = Existing.Select(field =>
            {
                var update = updates.FirstOrDefault(candidate =>
                    candidate.Name.Equals(field.Name, StringComparison.OrdinalIgnoreCase));
                return update is null
                    ? field
                    : field with
                    {
                        Alias = update.Alias,
                        Domain = update.Domain,
                        Hidden = update.Hidden ?? field.Hidden,
                    };
            }).ToArray();
            return Task.FromResult<IReadOnlyList<LayerFieldConfiguration>>(updated);
        }
    }
}
