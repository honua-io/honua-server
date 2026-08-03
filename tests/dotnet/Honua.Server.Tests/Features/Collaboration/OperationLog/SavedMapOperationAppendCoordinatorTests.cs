// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Collaboration.Operations;
using Honua.Server.Features.Collaboration;
using Honua.Server.Features.Collaboration.Operations;
using Honua.Server.Features.Collaboration.Sessions;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Features.Collaboration.OperationLog;

/// <summary>
/// The append coordinator owns one <see cref="SemaphoreSlim"/> per map stripe for its whole
/// lifetime, so it must own their disposal too (honua-server#2999 review). It is a DI singleton,
/// which means the container is the disposer — these tests pin the contract that makes that work:
/// the type is disposable, disposal is idempotent, and a disposed coordinator refuses work instead
/// of touching disposed gates.
/// </summary>
public sealed class SavedMapOperationAppendCoordinatorTests
{
    private const string MapId = "11111111-2222-3333-4444-555555555555";

    [UnitTest]
    public void Coordinator_IsDisposable_SoTheContainerReleasesItsStripeGates()
    {
        using var coordinator = CreateCoordinator();

        coordinator.Should().BeAssignableTo<IDisposable>(
            "the stripe semaphores live for the coordinator's lifetime and the DI container " +
            "disposes singletons that declare IDisposable");
    }

    [UnitTest]
    public void Dispose_CalledTwice_IsIdempotent()
    {
        var coordinator = CreateCoordinator();

        coordinator.Dispose();
        var second = () => coordinator.Dispose();

        second.Should().NotThrow("the container may dispose a singleton a test already disposed");
    }

    [UnitTest]
    public async Task AppendAndPublishAsync_AfterDispose_ThrowsObjectDisposed()
    {
        var coordinator = CreateCoordinator();
        coordinator.Dispose();

        var append = async () => await coordinator.AppendAndPublishAsync(
            MapId,
            new SavedMapOperationAppendRequest
            {
                OperationId = new SavedMapOperationId("op-1"),
                MapId = new SavedMapId(MapId),
                ActorId = new SavedMapActorId("actor"),
                BaseCursor = new SavedMapOperationCursor(0),
                Kind = SavedMapOperationKind.SetViewport,
                Payload = default,
            },
            CancellationToken.None);

        // Waiting on a disposed SemaphoreSlim is undefined for callers; fail with the typed
        // lifetime error instead.
        await append.Should().ThrowAsync<ObjectDisposedException>();
    }

    [UnitTest]
    public async Task AppendAndPublishAsync_BeforeDispose_StillPublishesTheCommittedOperation()
    {
        using var coordinator = CreateCoordinator(out var sessions);
        var join = await sessions.JoinAsync(
            MapId,
            new CollaborationJoinRequest { DisplayName = "Ada" },
            Principal("ada"));
        _ = sessions.DrainEvents(join.Response!.SessionId);

        var result = await coordinator.AppendAndPublishAsync(
            MapId,
            new SavedMapOperationAppendRequest
            {
                OperationId = new SavedMapOperationId("op-1"),
                MapId = new SavedMapId(MapId),
                ActorId = new SavedMapActorId("actor"),
                BaseCursor = new SavedMapOperationCursor(0),
                Kind = SavedMapOperationKind.SetViewport,
                Payload = default,
            },
            CancellationToken.None);

        result.Status.Should().Be(SavedMapOperationAppendStatus.Accepted);
        var events = sessions.DrainEvents(join.Response.SessionId);
        events.Should().ContainSingle()
            .Which.Event.Type.Should().Be(CollaborationSessionEventTypes.OperationAppended);
    }

    [UnitTest]
    public void CanAcceptEdits_MultiReplicaSharedLogWithoutDistributedOrdering_IsFalse()
    {
        var repository = Substitute.For<ISavedMapOperationLogRepository>();
        repository.SupportsReplicaSharedReplay.Returns(true);
        var backplane = Substitute.For<ICollaborationSessionBackplane>();
        backplane.SupportsCrossReplicaDelivery.Returns(true);

        using var coordinator = new SavedMapOperationAppendCoordinator(
            repository,
            CreateSessionService(),
            SavedMapCollaborationTopology.ForMultiReplica(true),
            backplane);

        coordinator.CanAcceptEdits.Should().BeFalse(
            "pub/sub delivery cannot order cursor assignment and publication across replicas");
    }

    [UnitTest]
    public void CanAcceptEdits_MultiReplicaWithDistributedOrdering_IsTrue()
    {
        var repository = Substitute.For<ISavedMapOperationLogRepository>();
        repository.SupportsReplicaSharedReplay.Returns(true);
        var backplane = Substitute.For<ICollaborationSessionBackplane>();
        backplane.SupportsCrossReplicaDelivery.Returns(true);
        backplane.SupportsOrderedOperationDelivery.Returns(true);

        using var coordinator = new SavedMapOperationAppendCoordinator(
            repository,
            CreateSessionService(),
            SavedMapCollaborationTopology.ForMultiReplica(true),
            backplane);

        coordinator.CanAcceptEdits.Should().BeTrue();
    }

    private static SavedMapOperationAppendCoordinator CreateCoordinator() => CreateCoordinator(out _);

    private static SavedMapOperationAppendCoordinator CreateCoordinator(
        out InMemoryCollaborationSessionService sessions)
    {
        sessions = CreateSessionService();
        return new SavedMapOperationAppendCoordinator(
            new InMemorySavedMapOperationLogRepository(),
            sessions,
            SavedMapCollaborationTopology.ForMultiReplica(false));
    }

    private static InMemoryCollaborationSessionService CreateSessionService() =>
        new(new AllowSavedMapCollaborationAuthorizer(), new SystemCollaborationSessionClockDouble());

    private static ClaimsPrincipal Principal(string userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], authenticationType: "Test"));

    private sealed class SystemCollaborationSessionClockDouble : ICollaborationSessionClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class AllowSavedMapCollaborationAuthorizer : ISavedMapCollaborationAuthorizer
    {
        public ValueTask<SavedMapCollaborationAuthorizationResult> AuthorizeJoinAsync(
            string mapId,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(SavedMapCollaborationAuthorizationResult.Allow());
    }
}
