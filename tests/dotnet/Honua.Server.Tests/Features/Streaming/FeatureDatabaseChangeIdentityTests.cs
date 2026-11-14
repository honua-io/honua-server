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
            EventId = "sql-edit", Cursor = 42, Timestamp = DateTimeOffset.UtcNow,
            ServiceId = string.Empty, LayerId = 17, ObjectId = 3,
            Operation = operation, Protocol = "database", RequestId = string.Empty
        };
        Assert.True(filter.Matches(delta, null, null));
        var baseline = FeatureStreamEndpoints.ResolveStreamLayer(snapshot, service: null, 17)!;
        var projected = filter.Project(delta);
        Assert.Equal(baseline.Service!.Metadata.Id, projected.ServiceId);
        Assert.Equal(baseline.Service.Metadata.Id, projected.SourceId);
        Assert.Equal(42, projected.Cursor);
        Assert.Equal(operation, projected.Operation);
    }

    private static MetadataV2Graph CreateGraph() => new TestMetadataV2GraphBuilder()
        .AddResource("places-data", "Places", MetadataV2ResourceType.FeatureDataset)
        .AddStorageBinding("places-storage", "places-data", "test.layers.places", storageLayerId: 17)
        .AddService("places", "Places")
        .AddPublication("places-publication", "places", "places-data", layerIndex: 0,
            storageBindingId: "places-storage")
        .Build();
}
