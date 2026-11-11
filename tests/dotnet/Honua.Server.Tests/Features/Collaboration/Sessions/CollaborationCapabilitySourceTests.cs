// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Collaboration.Operations;
using Honua.Server.Features.Collaboration;
using Honua.Server.Features.Collaboration.Sessions;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Features.Collaboration.Sessions;

public sealed class CollaborationCapabilitySourceTests
{
    [UnitTest]
    public void Current_MultiReplicaWithSharedDurableLog_AdvertisesReplayButNoLiveDelivery()
    {
        var repository = Substitute.For<ISavedMapOperationLogRepository>();
        repository.SupportsReplicaSharedReplay.Returns(true);
        repository.SupportsRestartDurableReplay.Returns(true);
        repository.SupportsRestartDurableCheckpointCursors.Returns(true);
        repository.SupportsRestartDurableCheckpointing.Returns(true);

        var capabilities = new CollaborationCapabilitySource(
            SavedMapCollaborationTopology.ForMultiReplica(true), repository).Current;

        capabilities.Replay.Should().BeTrue("the operation log is replica-shared");
        capabilities.Checkpoints.Should().BeTrue("the operation log is also restart-durable");
        capabilities.Operations.Should().BeFalse("live operations reach only participants on the same node");
        capabilities.Cursors.Should().BeFalse("presence reaches only participants on the same node");
        capabilities.Selections.Should().BeFalse();
        capabilities.Follow.Should().BeFalse();
    }

    [UnitTest]
    public void Current_RestartDurableReplayWithoutDurableCheckpointCursor_DoesNotAdvertiseCheckpoints()
    {
        var repository = Substitute.For<ISavedMapOperationLogRepository>();
        repository.SupportsReplicaSharedReplay.Returns(true);
        repository.SupportsRestartDurableReplay.Returns(true);
        repository.SupportsRestartDurableCheckpointCursors.Returns(false);
        repository.SupportsRestartDurableCheckpointing.Returns(false);

        var capabilities = new CollaborationCapabilitySource(
            SavedMapCollaborationTopology.ForMultiReplica(false), repository).Current;

        capabilities.Replay.Should().BeTrue();
        capabilities.Checkpoints.Should().BeFalse();
    }

    [UnitTest]
    public void Current_SingleReplica_AdvertisesLiveFeatures()
    {
        var repository = Substitute.For<ISavedMapOperationLogRepository>();

        var capabilities = new CollaborationCapabilitySource(
            SavedMapCollaborationTopology.ForMultiReplica(false), repository).Current;

        capabilities.Operations.Should().BeTrue();
        capabilities.Replay.Should().BeTrue();
        capabilities.Cursors.Should().BeTrue();
        capabilities.Selections.Should().BeTrue();
        capabilities.Follow.Should().BeTrue();
    }
}
