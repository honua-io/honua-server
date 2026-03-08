// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.FeatureStore.Domain;

/// <summary>
/// Unit tests for FeatureChange and ReplicaRecord domain types
/// </summary>
public sealed class FeatureChangeTests
{
    private static readonly int[] s_expectedLayerIds = [0, 1, 2];
    [UnitTest]
    public void FeatureChange_Create_ShouldSetAllProperties()
    {
        var now = DateTimeOffset.UtcNow;
        var change = new FeatureChange
        {
            ChangeId = 42,
            Generation = 100,
            LayerId = 0,
            ObjectId = 7,
            Operation = FeatureChangeOperation.Insert,
            ChangedAt = now
        };

        change.ChangeId.Should().Be(42);
        change.Generation.Should().Be(100);
        change.LayerId.Should().Be(0);
        change.ObjectId.Should().Be(7);
        change.Operation.Should().Be(FeatureChangeOperation.Insert);
        change.ChangedAt.Should().Be(now);
    }

    [UnitTest]
    public void FeatureChangeOperation_Values_MatchDatabaseConvention()
    {
        ((short)FeatureChangeOperation.Insert).Should().Be(1);
        ((short)FeatureChangeOperation.Update).Should().Be(2);
        ((short)FeatureChangeOperation.Delete).Should().Be(3);
    }

    [UnitTest]
    public void FeatureChange_Equality_SameValues_AreEqual()
    {
        var now = DateTimeOffset.UtcNow;
        var a = new FeatureChange
        {
            ChangeId = 1,
            Generation = 10,
            LayerId = 0,
            ObjectId = 5,
            Operation = FeatureChangeOperation.Update,
            ChangedAt = now
        };
        var b = new FeatureChange
        {
            ChangeId = 1,
            Generation = 10,
            LayerId = 0,
            ObjectId = 5,
            Operation = FeatureChangeOperation.Update,
            ChangedAt = now
        };

        a.Should().Be(b);
    }

    [UnitTest]
    public void FeatureChange_Equality_DifferentOperation_AreNotEqual()
    {
        var now = DateTimeOffset.UtcNow;
        var a = new FeatureChange
        {
            ChangeId = 1,
            Generation = 10,
            LayerId = 0,
            ObjectId = 5,
            Operation = FeatureChangeOperation.Insert,
            ChangedAt = now
        };
        var b = new FeatureChange
        {
            ChangeId = 1,
            Generation = 10,
            LayerId = 0,
            ObjectId = 5,
            Operation = FeatureChangeOperation.Delete,
            ChangedAt = now
        };

        a.Should().NotBe(b);
    }

    [UnitTest]
    public void ReplicaRecord_Create_ShouldSetAllProperties()
    {
        var now = DateTimeOffset.UtcNow;
        var record = new ReplicaRecord
        {
            ReplicaId = "abc123",
            ReplicaName = "TestReplica",
            ServiceId = "svc-1",
            SyncModel = "perReplica",
            LayerIds = [0, 1, 2],
            CreatedAt = now,
            LastSyncTime = now,
            LastSyncGeneration = 42
        };

        record.ReplicaId.Should().Be("abc123");
        record.ReplicaName.Should().Be("TestReplica");
        record.ServiceId.Should().Be("svc-1");
        record.SyncModel.Should().Be("perReplica");
        record.LayerIds.Should().BeEquivalentTo(s_expectedLayerIds);
        record.CreatedAt.Should().Be(now);
        record.LastSyncTime.Should().Be(now);
        record.LastSyncGeneration.Should().Be(42);
    }

    [UnitTest]
    public void ReplicaRecord_DefaultGeneration_IsZero()
    {
        var record = new ReplicaRecord
        {
            ReplicaId = "x",
            ReplicaName = "x",
            ServiceId = "s",
            SyncModel = "none",
            LayerIds = [],
            CreatedAt = DateTimeOffset.UtcNow,
            LastSyncTime = DateTimeOffset.UtcNow,
            LastSyncGeneration = 0
        };

        record.LastSyncGeneration.Should().Be(0);
    }
}
