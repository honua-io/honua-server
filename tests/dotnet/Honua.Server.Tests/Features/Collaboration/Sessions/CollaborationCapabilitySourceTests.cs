// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Collaboration.Operations;
using Honua.Core.Features.Realtime;
using Honua.Server.Features.Collaboration.Operations;
using Microsoft.Extensions.DependencyInjection;
using Honua.Server.Features.Collaboration;
using Honua.Server.Features.Collaboration.Sessions;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Features.Collaboration.Sessions;

public sealed class CollaborationCapabilitySourceTests
{
    [UnitTest]
    public void RegisteredPostgresProvider_EnablesOrderedMultiReplicaCollaborationWithoutRedis()
    {
        var repository = Substitute.For<ISavedMapOperationLogRepository>();
        repository.SupportsReplicaSharedReplay.Returns(true);
        var fanout = Substitute.For<IRealtimeFanoutProvider>();
        fanout.Capabilities.Returns(new RealtimeProviderCapabilities(true, true, true));
        var services = new ServiceCollection();
        services.AddSingleton(repository);
        services.AddSingleton(fanout);
        services.AddSingleton(SavedMapCollaborationTopology.ForMultiReplica(true));
        services.AddCollaborationSessionTransport();
        using var container = services.BuildServiceProvider();

        container.GetRequiredService<CollaborationCapabilitySource>().Current.Operations.Should().BeTrue();
        container.GetRequiredService<CollaborationCapabilitySource>().Current.Cursors.Should().BeTrue();
        container.GetRequiredService<SavedMapOperationAppendCoordinator>().CanAcceptEdits.Should().BeTrue();
    }

    [UnitTest]
    public void Current_MultiReplicaPubSubWithoutStateOrOrdering_DoesNotOverAdvertise()
    {
        var repository = Substitute.For<ISavedMapOperationLogRepository>();
        repository.SupportsReplicaSharedReplay.Returns(true);
        repository.SupportsRestartDurableReplay.Returns(true);
        repository.SupportsRestartDurableCheckpointCursors.Returns(true);
        repository.SupportsRestartDurableCheckpointing.Returns(true);
        var provider = Substitute.For<IRealtimeFanoutProvider>();
        provider.Capabilities.Returns(new RealtimeProviderCapabilities(true, false, false));

        var capabilities = new CollaborationCapabilitySource(
            SavedMapCollaborationTopology.ForMultiReplica(true), repository, provider).Current;

        capabilities.Replay.Should().BeTrue("the operation log is replica-shared");
        capabilities.Checkpoints.Should().BeTrue("the operation log is also restart-durable");
        capabilities.Operations.Should().BeFalse("pub/sub does not preserve cursor publication order");
        capabilities.Cursors.Should().BeFalse("pub/sub does not retain replica-wide presence");
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
            SavedMapCollaborationTopology.ForMultiReplica(false),
            repository,
            Substitute.For<IRealtimeFanoutProvider>()).Current;

        capabilities.Replay.Should().BeTrue();
        capabilities.Checkpoints.Should().BeFalse();
    }

    [UnitTest]
    public void Current_MultiReplicaWithCompleteProviderGuarantees_AdvertisesLiveFeatures()
    {
        var repository = Substitute.For<ISavedMapOperationLogRepository>();
        repository.SupportsReplicaSharedReplay.Returns(true);
        var provider = Substitute.For<IRealtimeFanoutProvider>();
        provider.Capabilities.Returns(new RealtimeProviderCapabilities(true, true, true));

        var capabilities = new CollaborationCapabilitySource(
            SavedMapCollaborationTopology.ForMultiReplica(true), repository, provider).Current;

        capabilities.Operations.Should().BeTrue();
        capabilities.Cursors.Should().BeTrue();
        capabilities.Selections.Should().BeTrue();
        capabilities.Follow.Should().BeTrue();
    }
}
