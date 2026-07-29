// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Collaboration.Operations;
using Honua.Server.Features.Collaboration;
using Honua.Server.Features.Collaboration.Checkpoints;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Collaboration.Checkpoints;

public sealed class SavedMapCheckpointOperationLogTests
{
    private const string MapId = "11111111-2222-3333-4444-555555555555";

    [UnitTest]
    public async Task ReplayPendingAsync_RecordedCursorInsideWindow_ReplaysOnlySuffix()
    {
        var repository = new InMemorySavedMapOperationLogRepository(retainedOperationCount: 2);
        var log = new SavedMapCheckpointOperationLog(repository, SavedMapCollaborationTopology.ForMultiReplica(false));
        for (var i = 1; i <= 5; i++)
        {
            await AppendAsync(repository, i);
        }

        // A checkpoint persisted everything up to cursor 4; only cursor 5 is still pending,
        // even though cursors 1-3 have already been pruned out of the retained window.
        log.MarkCheckpointed(MapId, 4);

        var replay = await log.ReplayPendingAsync(MapId, CancellationToken.None);

        replay.Status.Should().Be(SavedMapOperationReplayStatus.Ok);
        replay.HeadCursor.Value.Should().Be(5);
        replay.Operations.Should().ContainSingle().Which.ServerCursor.Value.Should().Be(5);
    }

    [UnitTest]
    public async Task ReplayPendingAsync_NeverCheckpointedOperationsPruned_ReportsResyncRequired()
    {
        var repository = new InMemorySavedMapOperationLogRepository(retainedOperationCount: 2);
        var log = new SavedMapCheckpointOperationLog(repository, SavedMapCollaborationTopology.ForMultiReplica(false));
        for (var i = 1; i <= 5; i++)
        {
            await AppendAsync(repository, i);
        }

        // No checkpoint was ever recorded, so the pending replay must start at cursor 0 —
        // which has been pruned out of the retained window: completeness cannot be proven.
        var replay = await log.ReplayPendingAsync(MapId, CancellationToken.None);

        replay.Status.Should().Be(SavedMapOperationReplayStatus.ResyncRequired);
    }

    [UnitTest]
    public void CanProveReplayContinuity_MatchesTopologyAndLogCapability()
    {
        var repository = new InMemorySavedMapOperationLogRepository();
        repository.SupportsReplicaSharedReplay.Should().BeFalse();

        new SavedMapCheckpointOperationLog(repository, SavedMapCollaborationTopology.ForMultiReplica(false))
            .CanProveReplayContinuity.Should().BeTrue();
        new SavedMapCheckpointOperationLog(repository, SavedMapCollaborationTopology.ForMultiReplica(true))
            .CanProveReplayContinuity.Should().BeFalse();
    }

    [UnitTest]
    public void CanProveRestartContinuity_FollowsTheLogsOwnRestartDurabilitySignal()
    {
        // Restart continuity is a property of the LOG, not of the topology: a single-replica
        // deployment is authoritative over its own log yet still loses acknowledged operations
        // when the process restarts, so the checkpoint gate must not read this off topology
        // (honua-server#2999 review).
        var repository = new InMemorySavedMapOperationLogRepository();
        repository.SupportsRestartDurableReplay.Should().BeFalse();

        new SavedMapCheckpointOperationLog(repository, SavedMapCollaborationTopology.ForMultiReplica(false))
            .CanProveRestartContinuity.Should().BeFalse();
        new SavedMapCheckpointOperationLog(repository, SavedMapCollaborationTopology.ForMultiReplica(true))
            .CanProveRestartContinuity.Should().BeFalse();
    }

    private static async Task AppendAsync(InMemorySavedMapOperationLogRepository repository, int n)
    {
        var result = await repository.AppendAsync(new SavedMapOperationAppendRequest
        {
            OperationId = new SavedMapOperationId($"op-{n}"),
            MapId = new SavedMapId(MapId),
            ActorId = new SavedMapActorId("actor"),
            BaseCursor = new SavedMapOperationCursor(n - 1),
            Kind = SavedMapOperationKind.SetViewport,
            Payload = default,
        });
        result.Status.Should().Be(SavedMapOperationAppendStatus.Accepted);
    }

}
