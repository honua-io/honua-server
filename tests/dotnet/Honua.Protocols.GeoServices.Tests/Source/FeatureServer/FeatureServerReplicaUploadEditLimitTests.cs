// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Protocols.GeoServices.FeatureServer;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// synchronizeReplica validates the whole upload against the shared edit limits before the sync
/// pipeline runs, because under manual review the conflicting rows never reach the shared edits
/// handler and its own limit checks would not see them. That pre-check must use the same semantics
/// the shared handler does, or a valid upload is rejected at the replica boundary (#2430).
/// </summary>
public sealed class FeatureServerReplicaUploadEditLimitTests
{
    private static readonly EditLimits Limits = new()
    {
        MaxFeaturesPerEdit = 500,
        MaxEditsPerTransaction = 2500,
    };

    [Fact]
    public void ValidateUploadEditLimits_CombinedOperationsUnderThePerOperationLimit_IsAccepted()
    {
        // FeatureServerEditsHandler.ValidateEditLimits applies MaxFeaturesPerEdit separately to
        // adds/updates/deletes. 300 updates plus 300 deletes is under the limit on both counts and
        // under MaxEditsPerTransaction, so the replica pre-check must accept it too.
        var upload = Upload(adds: 0, updates: 300, deletes: 300);

        FeatureServerEndpoints.ValidateUploadEditLimits(upload, Limits).Should().BeNull();
    }

    [Fact]
    public void ValidateUploadEditLimits_SingleOperationOverThePerOperationLimit_IsRejected()
    {
        var upload = Upload(adds: 501, updates: 0, deletes: 0);

        FeatureServerEndpoints.ValidateUploadEditLimits(upload, Limits)
            .Should().Contain("500 features per operation");
    }

    [Fact]
    public void ValidateUploadEditLimits_TotalOverTheTransactionLimit_IsRejected()
    {
        // Spread across layers so no single operation trips the per-operation limit.
        var upload = Enumerable.Range(0, 6)
            .Select(layer => new ReplicaUploadLayerEdits(layer, layer, Edits(500, FeatureEditOperationKind.Create)))
            .ToImmutableArray();

        FeatureServerEndpoints.ValidateUploadEditLimits(upload, Limits)
            .Should().Contain("2500 edits per transaction");
    }

    private static ImmutableArray<ReplicaUploadLayerEdits> Upload(int adds, int updates, int deletes)
    {
        var edits = Edits(adds, FeatureEditOperationKind.Create)
            .AddRange(Edits(updates, FeatureEditOperationKind.Update))
            .AddRange(Edits(deletes, FeatureEditOperationKind.Delete));
        return [new ReplicaUploadLayerEdits(0, 0, edits)];
    }

    private static ImmutableArray<ReplicaUploadEdit> Edits(int count, FeatureEditOperationKind kind)
        => Enumerable.Range(1, count)
            .Select(i => new ReplicaUploadEdit(kind, kind == FeatureEditOperationKind.Create ? null : i, null))
            .ToImmutableArray();
}
