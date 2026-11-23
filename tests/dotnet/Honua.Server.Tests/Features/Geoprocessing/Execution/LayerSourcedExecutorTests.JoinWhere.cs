// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing.Execution;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

public sealed partial class LayerSourcedExecutorTests
{
    private const string JoinFilter = "sfha_tf = 'T'";

    [UnitTest]
    public async Task SpatialJoin_JoinWhere_FiltersOnlyTheJoinLayerRead()
    {
        var source = new FakeTwoLayerDagFeatureSource(HonuaLayerSourceId, new Dictionary<int, IReadOnlyList<DagSourceFeature>>
        {
            [1] = [PointFeature(0, 0)],
            [2] = [BoxFeature(-1, -1, 1, 1)],
        });

        var (status, uri, _) = await RunAsync(
            new LayerSpatialJoinExecutor(ScopeFactory(source), Options(), NullLogger<LayerSpatialJoinExecutor>.Instance),
            LayerSpatialJoinExecutor.HandledProcessId,
            ("layerId", "1"), ("joinLayerId", "2"), ("where", "category = 'drainage'"), ("joinWhere", JoinFilter));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        uri.Should().NotBeNull();
        _allRequestsForAssertions.Single(r => r.LayerId == 2).Where.Should().Be(JoinFilter);
        _allRequestsForAssertions.Single(r => r.LayerId == 1).Where.Should().Be("category = 'drainage'");
    }

    [UnitTest]
    public async Task SpatialJoin_NoJoinWhere_LeavesJoinReadUnfiltered()
    {
        var source = new FakeTwoLayerDagFeatureSource(HonuaLayerSourceId, new Dictionary<int, IReadOnlyList<DagSourceFeature>>
        {
            [1] = [PointFeature(0, 0)],
            [2] = [BoxFeature(-1, -1, 1, 1)],
        });

        var (status, _, _) = await RunAsync(
            new LayerSpatialJoinExecutor(ScopeFactory(source), Options(), NullLogger<LayerSpatialJoinExecutor>.Instance),
            LayerSpatialJoinExecutor.HandledProcessId,
            ("layerId", "1"), ("joinLayerId", "2"), ("where", "category = 'drainage'"), ("joinWhere", "  "));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        _allRequestsForAssertions.Single(r => r.LayerId == 2).Where.Should().BeNull(
            "the target's where must never leak into the join read, and a blank joinWhere means no filter");
    }

    [UnitTest]
    public async Task SpatialJoin_JoinLayerOverVertexBudget_NamesJoinWhereAsTheRemedy()
    {
        var source = new FakeTwoLayerDagFeatureSource(HonuaLayerSourceId, new Dictionary<int, IReadOnlyList<DagSourceFeature>>
        {
            [1] = [PointFeature(0, 0)],
            [2] = [BoxFeature(-1, -1, 1, 1), BoxFeature(2, 2, 3, 3)],
        });

        var (status, uri, _) = await RunAsync(
            new LayerSpatialJoinExecutor(ScopeFactory(source), Options(maxLayerVertices: 6),
                NullLogger<LayerSpatialJoinExecutor>.Instance),
            LayerSpatialJoinExecutor.HandledProcessId,
            ("layerId", "1"), ("joinLayerId", "2"));

        status.Should().Be(ExecutionJobStatus.Failed);
        uri.Should().BeNull();
        _lastErrorForAssertions.Should().Contain("join layer 2 exceeds Geoprocessing:Executors:MaxLayerVertices=6")
            .And.Contain("Filter the join layer with joinWhere.");
        _lastRetryableForAssertions.Should().BeFalse();
    }

    [UnitTest]
    public async Task SpatialJoin_UnsupportedJoinWhere_IsTerminalRefusalNamingJoinWhere()
    {
        const string guidance = "the where clause is not supported; use simple comparisons joined by AND";

        var (status, uri, _) = await RunAsync(
            new LayerSpatialJoinExecutor(ScopeFactory(new JoinRejectingSource(guidance)), Options(),
                NullLogger<LayerSpatialJoinExecutor>.Instance),
            LayerSpatialJoinExecutor.HandledProcessId,
            ("layerId", "1"), ("joinLayerId", "2"), ("joinWhere", "zone LIKE"));

        status.Should().Be(ExecutionJobStatus.Failed);
        uri.Should().BeNull();
        _lastErrorForAssertions.Should().Contain($"joinWhere on join layer 2: {guidance}")
            .And.NotContain("Exception", "a caller error must not collapse to an exception type name");
        _lastRetryableForAssertions.Should().BeFalse("a rejected join filter fails the same way on every attempt");
    }

    // Serves the target layer and rejects the join layer's selection, as the PostGIS source
    // does for a caller-written where its grammar refuses.
    private sealed class JoinRejectingSource(string message) : IDagFeatureSource
    {
        public string SourceId => HonuaLayerSourceId;

        public async IAsyncEnumerable<DagSourceFeature> ReadAsync(
            DagSourceRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (request.LayerId == 2)
            {
                throw new DagSourceSelectionException(message);
            }

            yield return PointFeature(0, 0);
        }
    }
}
