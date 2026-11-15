// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Streaming;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Streaming;

public sealed class FeatureDatabaseChangeIdentityTests
{
    [UnitTheory]
    [InlineData(null, "update")]
    [InlineData(null, "delete")]
    [InlineData("places", "update")]
    [InlineData("places", "delete")]
    public async Task DatabaseDelta_PreservesSnapshotPublicationIdentity(string? serviceId, string operation)
    {
        var snapshot = await new TestMetadataV2GraphProvider(CreateGraph()).GetCurrentAsync();
        var guard = new FeatureStreamRoutabilityGuard();
        guard.Update(guard.BeginRefresh(), snapshot);
        var filter = new StreamSubscriptionFilter(resolvedServiceId: serviceId, routabilityGuard: guard);
        var delta = new FeatureStreamEnvelope
        {
            EventId = "sql-edit",
            Cursor = 42,
            Timestamp = DateTimeOffset.UtcNow,
            ServiceId = string.Empty,
            LayerId = 17,
            ObjectId = 3,
            Operation = operation,
            Protocol = "database",
            RequestId = string.Empty
        };
        Assert.True(filter.Matches(delta, null, null));
        var baseline = FeatureStreamEndpoints.ResolveStreamLayer(snapshot, service: null, 17)!;
        var projected = filter.Project(delta);
        Assert.Equal(baseline.Service!.Metadata.Id, projected.ServiceId);
        Assert.Equal(baseline.Service.Metadata.Id, projected.SourceId);
        Assert.Equal(42, projected.Cursor);
        Assert.Equal(operation, projected.Operation);
    }

    [UnitTest]
    public async Task LayerServiceIndex_PreservesPrimaryPublicationAndStorageIdPrecedence()
    {
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("first", "First")
            .AddStorageBinding("first-storage", "first", "test.first", storageLayerId: 17)
            .AddResource("second", "Second")
            .AddStorageBinding("second-storage", "second", "test.second", storageLayerId: 0)
            .AddService("alpha", "Alpha")
            .AddService("zulu", "Zulu")
            .AddPublication("first-secondary", "alpha", "first", layerIndex: 0, storageBindingId: "first-storage")
            .AddPublication("first-primary", "zulu", "first", layerIndex: 0, storageBindingId: "first-storage", isPrimary: true)
            .AddPublication("second-publication", "alpha", "second", layerIndex: 2, storageBindingId: "second-storage")
            .Build();
        var snapshot = await new TestMetadataV2GraphProvider(graph).GetCurrentAsync();
        var serviceIds = FeatureStreamEndpoints.ResolveStreamLayerServiceIds(snapshot);

        foreach (var layerId in new[] { 17, 0, 2 })
        {
            Assert.Equal(FeatureStreamEndpoints.ResolveStreamLayer(snapshot, service: null, layerId)!.Service!.Metadata.Id,
                serviceIds[layerId]);
        }

        Assert.Equal("zulu", serviceIds[17]);
        Assert.Equal("alpha", serviceIds[0]);
    }

    private static MetadataV2Graph CreateGraph() => new TestMetadataV2GraphBuilder()
        .AddResource("places-data", "Places")
        .AddStorageBinding("places-storage", "places-data", "test.layers.places", storageLayerId: 17)
        .AddService("places", "Places")
        .AddPublication("places-publication", "places", "places-data", layerIndex: 0,
            storageBindingId: "places-storage")
        .Build();
}
