// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
    public void Current_MultiReplicaPubSubWithoutStateOrOrdering_DoesNotOverAdvertise()
    {
        var repository = Substitute.For<ISavedMapOperationLogRepository>();
        repository.SupportsReplicaSharedReplay.Returns(true);
        repository.SupportsRestartDurableReplay.Returns(true);
        var backplane = Substitute.For<ICollaborationSessionBackplane>();
        backplane.SupportsCrossReplicaDelivery.Returns(true);

        var capabilities = new CollaborationCapabilitySource(
            SavedMapCollaborationTopology.ForMultiReplica(true), repository, backplane).Current;

        capabilities.Replay.Should().BeTrue("the operation log is replica-shared");
        capabilities.Checkpoints.Should().BeTrue("the operation log is also restart-durable");
        capabilities.Operations.Should().BeFalse("pub/sub does not preserve cursor publication order");
        capabilities.Cursors.Should().BeFalse("pub/sub does not retain replica-wide presence");
        capabilities.Selections.Should().BeFalse();
        capabilities.Follow.Should().BeFalse();
    }

    [UnitTest]
    public void Current_MultiReplicaWithCompleteBackplaneGuarantees_AdvertisesLiveFeatures()
    {
        var repository = Substitute.For<ISavedMapOperationLogRepository>();
        repository.SupportsReplicaSharedReplay.Returns(true);
        var backplane = Substitute.For<ICollaborationSessionBackplane>();
        backplane.SupportsCrossReplicaDelivery.Returns(true);
        backplane.SupportsOrderedOperationDelivery.Returns(true);
        backplane.SupportsReplicaWidePresence.Returns(true);

        var capabilities = new CollaborationCapabilitySource(
            SavedMapCollaborationTopology.ForMultiReplica(true), repository, backplane).Current;

        capabilities.Operations.Should().BeTrue();
        capabilities.Cursors.Should().BeTrue();
        capabilities.Selections.Should().BeTrue();
        capabilities.Follow.Should().BeTrue();
    }
}
