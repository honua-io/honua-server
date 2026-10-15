// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Protocols.OData;
using Honua.Protocols.OData.Services;

namespace Honua.Protocols.OData.UnitTests.Services;

public sealed class ODataQuerySnapshotQuotaTests
{
    [Fact]
    public void Ensure_LiveCountAtCap_Throws()
    {
        var act = () => QuerySnapshotQuota.Ensure(QuerySnapshotQuota.MaxLiveSnapshots, 0, 0);

        act.Should().Throw<QuerySnapshotQuotaExceededException>();
    }

    [Fact]
    public void Ensure_IncomingBytesPastLiveByteCap_Throws()
    {
        var act = () => QuerySnapshotQuota.Ensure(0, QuerySnapshotQuota.MaxLiveBytes, 1);

        act.Should().Throw<QuerySnapshotQuotaExceededException>();
    }

    [Fact]
    public void Ensure_UnderLiveCaps_AllowsInsert()
    {
        var act = () => QuerySnapshotQuota.Ensure(QuerySnapshotQuota.MaxLiveSnapshots - 1, QuerySnapshotQuota.MaxLiveBytes - 1, 1);

        act.Should().NotThrow();
    }

    [Fact]
    public void SnapshotRows_BaselinePagesItems_DeltaPagesChanges()
    {
        using var items = JsonDocument.Parse("[{\"ObjectId\":1}]");
        using var changes = JsonDocument.Parse("[{\"ObjectId\":2}]");
        var itemRows = new[] { items.RootElement[0].Clone() };
        var changeRows = new[] { changes.RootElement[0].Clone() };
        var state = new ODataDeltaService.DeltaQueryState { Timestamp = DateTimeOffset.UtcNow, LayerId = 7 };
        var baseline = new ODataQuerySnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, state, "binding", 10, itemRows, [], IsDelta: false);
        var delta = baseline with { Changes = changeRows, IsDelta = true };

        ODataStreamingQueryHandler.SnapshotRows(baseline).Should().BeSameAs(itemRows);
        ODataStreamingQueryHandler.SnapshotRows(delta).Should().BeSameAs(changeRows);
    }
}
