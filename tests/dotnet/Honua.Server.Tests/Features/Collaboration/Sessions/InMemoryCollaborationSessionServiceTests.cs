// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Server.Features.Collaboration.Sessions;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Collaboration.Sessions;

public sealed class InMemoryCollaborationSessionServiceTests
{
    [UnitTest]
    public async Task JoinAsync_WithAuthorizedUser_ReturnsMapSnapshot()
    {
        var clock = new FakeCollaborationClock(FixedUtcNow());
        var service = CreateService(clock);

        var result = await service.JoinAsync(
            "saved-map:ops",
            new CollaborationJoinRequest { DisplayName = "Ada" },
            Principal("user-1", "Ada"));

        result.Authorization.Authorized.Should().BeTrue();
        result.Response.Should().NotBeNull();
        result.Response!.MapId.Should().Be("saved-map:ops");
        result.Response.Snapshot.MapId.Should().Be("saved-map:ops");
        result.Response.Snapshot.Participants.Should().ContainSingle();
        result.Response.Participant.DisplayName.Should().Be("Ada");
        result.Response.ParticipantId.Should().Be(result.Response.SessionId.ToString("N"));
        result.Response.Capabilities.Operations.Should().BeTrue();
        result.Response.Capabilities.FeatureLocks.Should().BeFalse(
            "feature-lock events are not bridged onto the session stream");
    }

    [UnitTest]
    public async Task UpdateCursor_SameMapParticipants_FansOutOnlyWithinMap()
    {
        var clock = new FakeCollaborationClock(FixedUtcNow());
        var service = CreateService(clock);
        var alice = (await service.JoinAsync("map-a", new CollaborationJoinRequest { DisplayName = "Alice" }, Principal("alice"))).Response!;
        var bob = (await service.JoinAsync("map-a", new CollaborationJoinRequest { DisplayName = "Bob" }, Principal("bob"))).Response!;
        var cara = (await service.JoinAsync("map-b", new CollaborationJoinRequest { DisplayName = "Cara" }, Principal("cara"))).Response!;
        _ = service.DrainEvents(alice.SessionId);
        _ = service.DrainEvents(bob.SessionId);
        _ = service.DrainEvents(cara.SessionId);

        var fanOut = service.UpdateCursor(
            alice.SessionId,
            new CollaborationCursor { X = -157.8583, Y = 21.3069 });

        fanOut.DeliveredCount.Should().Be(1);
        fanOut.Event.EnvelopeVersion.Should().Be(CollaborationEnvelopeContract.Version);
        service.DrainEvents(alice.SessionId).Should().BeEmpty();
        var bobEvents = service.DrainEvents(bob.SessionId);
        bobEvents.Should().ContainSingle();
        bobEvents[0].Event.Type.Should().Be(CollaborationSessionEventTypes.Cursor);
        bobEvents[0].Event.ParticipantId.Should().Be(alice.ParticipantId);
        bobEvents[0].Event.Cursor!.X.Should().Be(-157.8583);
        service.DrainEvents(cara.SessionId).Should().BeEmpty();
    }

    [UnitTest]
    public async Task Envelopes_ForOneMap_CarryStrictlyMonotonicSequences()
    {
        var clock = new FakeCollaborationClock(FixedUtcNow());
        var service = CreateService(clock);
        var alice = (await service.JoinAsync("map-a", new CollaborationJoinRequest { DisplayName = "Alice" }, Principal("alice"))).Response!;
        var bob = (await service.JoinAsync("map-a", new CollaborationJoinRequest { DisplayName = "Bob" }, Principal("bob"))).Response!;
        _ = service.DrainEvents(alice.SessionId);

        service.UpdateCursor(bob.SessionId, new CollaborationCursor { X = 1, Y = 2 });
        service.UpdateSelection(bob.SessionId, new CollaborationSelection { Ids = ["f-1"] });
        service.UpdateCursor(bob.SessionId, new CollaborationCursor { X = 3, Y = 4 });

        var envelopes = service.DrainEvents(alice.SessionId);
        envelopes.Should().HaveCount(3);
        envelopes.Select(static e => e.Sequence).Should().BeInAscendingOrder();
        envelopes.Select(static e => e.Sequence).Distinct().Should().HaveCount(3);
        envelopes.Should().OnlyContain(static e =>
            e.Cursor == e.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // The join snapshot sequence gates the reducer: every later envelope must be above it.
        envelopes.Should().OnlyContain(e => e.Sequence > alice.Snapshot.Sequence);
    }

    [UnitTest]
    public async Task FollowAndUnfollow_SameMapParticipant_FansOutStateChanges()
    {
        var clock = new FakeCollaborationClock(FixedUtcNow());
        var service = CreateService(clock);
        var leader = (await service.JoinAsync("map-a", new CollaborationJoinRequest { DisplayName = "Leader" }, Principal("leader"))).Response!;
        var follower = (await service.JoinAsync("map-a", new CollaborationJoinRequest { DisplayName = "Follower" }, Principal("follower"))).Response!;
        _ = service.DrainEvents(leader.SessionId);
        _ = service.DrainEvents(follower.SessionId);

        service.Follow(follower.SessionId, leader.SessionId);

        var followEvents = service.DrainEvents(leader.SessionId);
        followEvents.Should().ContainSingle();
        followEvents[0].Event.Type.Should().Be(CollaborationSessionEventTypes.Follow);
        followEvents[0].Event.Follow!.Following.Should().BeTrue();
        followEvents[0].Event.Follow!.TargetParticipantId.Should().Be(leader.ParticipantId);

        service.Unfollow(follower.SessionId);

        var unfollowEvents = service.DrainEvents(leader.SessionId);
        unfollowEvents.Should().ContainSingle();
        unfollowEvents[0].Event.Type.Should().Be(CollaborationSessionEventTypes.Follow);
        unfollowEvents[0].Event.Follow!.Following.Should().BeFalse();
    }

    [UnitTest]
    public async Task Follow_TargetFromDifferentMap_FailsWithoutFanOut()
    {
        var clock = new FakeCollaborationClock(FixedUtcNow());
        var service = CreateService(clock);
        var follower = (await service.JoinAsync("map-a", new CollaborationJoinRequest { DisplayName = "Follower" }, Principal("follower"))).Response!;
        var otherMapLeader = (await service.JoinAsync("map-b", new CollaborationJoinRequest { DisplayName = "Leader" }, Principal("leader"))).Response!;
        _ = service.DrainEvents(follower.SessionId);
        _ = service.DrainEvents(otherMapLeader.SessionId);

        var act = () => service.Follow(follower.SessionId, otherMapLeader.SessionId);

        act.Should().Throw<KeyNotFoundException>();
        service.GetSnapshot("map-a").FollowTargets.Should().BeEmpty();
        service.DrainEvents(follower.SessionId).Should().BeEmpty();
        service.DrainEvents(otherMapLeader.SessionId).Should().BeEmpty();
    }

    [UnitTest]
    public async Task Leave_JoinedParticipant_RemovesPresenceAndFansOutLeave()
    {
        var clock = new FakeCollaborationClock(FixedUtcNow());
        var service = CreateService(clock);
        var alice = (await service.JoinAsync("map-a", new CollaborationJoinRequest { DisplayName = "Alice" }, Principal("alice"))).Response!;
        var bob = (await service.JoinAsync("map-a", new CollaborationJoinRequest { DisplayName = "Bob" }, Principal("bob"))).Response!;
        _ = service.DrainEvents(alice.SessionId);
        _ = service.DrainEvents(bob.SessionId);

        service.Leave(bob.SessionId).Should().BeTrue();

        service.GetSnapshot("map-a").Participants.Should().ContainSingle(p => p.Id == alice.ParticipantId);
        service.DrainEvents(alice.SessionId).Should().ContainSingle(e =>
            e.Event.Type == CollaborationSessionEventTypes.ParticipantLeft &&
            e.Event.SessionId == bob.SessionId.ToString("N"));
        service.DrainEvents(bob.SessionId).Should().BeEmpty();
    }

    [UnitTest]
    public async Task Leave_FollowedParticipant_ClearsFollowerState()
    {
        var clock = new FakeCollaborationClock(FixedUtcNow());
        var service = CreateService(clock);
        var leader = (await service.JoinAsync("map-a", new CollaborationJoinRequest { DisplayName = "Leader" }, Principal("leader"))).Response!;
        var follower = (await service.JoinAsync("map-a", new CollaborationJoinRequest { DisplayName = "Follower" }, Principal("follower"))).Response!;
        service.Follow(follower.SessionId, leader.SessionId);
        _ = service.DrainEvents(leader.SessionId);
        _ = service.DrainEvents(follower.SessionId);

        service.Leave(leader.SessionId).Should().BeTrue();

        service.GetSnapshot("map-a").FollowTargets.Should().BeEmpty();
        service.DrainEvents(follower.SessionId).Should().Contain(e =>
            e.Event.Type == CollaborationSessionEventTypes.Follow &&
            e.Event.ParticipantId == follower.ParticipantId &&
            !e.Event.Follow!.Following);
    }

    [UnitTest]
    public async Task PruneStaleParticipants_RemovesOnlyExpiredHeartbeatSessions()
    {
        var clock = new FakeCollaborationClock(FixedUtcNow());
        var service = CreateService(clock);
        var active = (await service.JoinAsync("map-a", new CollaborationJoinRequest { DisplayName = "Active" }, Principal("active"))).Response!;
        var stale = (await service.JoinAsync("map-a", new CollaborationJoinRequest { DisplayName = "Stale" }, Principal("stale"))).Response!;
        _ = service.DrainEvents(active.SessionId);
        _ = service.DrainEvents(stale.SessionId);

        clock.Advance(TimeSpan.FromMinutes(10));
        service.Heartbeat(active.SessionId);
        clock.Advance(TimeSpan.FromMinutes(1));

        var removed = service.PruneStaleParticipants(TimeSpan.FromMinutes(5));

        removed.Should().Be(1);
        var snapshot = service.GetSnapshot("map-a");
        snapshot.Participants.Should().ContainSingle(p => p.Id == active.ParticipantId);
        snapshot.Participants.Should().NotContain(p => p.Id == stale.ParticipantId);
        service.DrainEvents(active.SessionId).Should().ContainSingle(e =>
            e.Event.Type == CollaborationSessionEventTypes.ParticipantLeft &&
            e.Event.SessionId == stale.SessionId.ToString("N"));
    }

    [UnitTest]
    public async Task JoinAsync_WhenAuthorizerDenies_ReturnsFailureWithoutSession()
    {
        var service = new InMemoryCollaborationSessionService(
            new DenySavedMapCollaborationAuthorizer(),
            new FakeCollaborationClock(FixedUtcNow()));

        var result = await service.JoinAsync(
            "saved-map:ops",
            new CollaborationJoinRequest { DisplayName = "Nope" },
            new ClaimsPrincipal(new ClaimsIdentity()));

        result.Response.Should().BeNull();
        result.Authorization.Status.Should().Be(SavedMapCollaborationAuthorizationStatus.Forbidden);
    }

    [UnitTest]
    public async Task PublishOperation_DeliversToEveryParticipantIncludingSubmitter()
    {
        var clock = new FakeCollaborationClock(FixedUtcNow());
        var backplane = new RecordingBackplane();
        var service = new InMemoryCollaborationSessionService(
            new AllowSavedMapCollaborationAuthorizer(), clock, backplane);
        var alice = (await service.JoinAsync("map-a", new CollaborationJoinRequest { DisplayName = "Alice" }, Principal("alice"))).Response!;
        var bob = (await service.JoinAsync("map-a", new CollaborationJoinRequest { DisplayName = "Bob" }, Principal("bob"))).Response!;
        _ = service.DrainEvents(alice.SessionId);
        _ = service.DrainEvents(bob.SessionId);
        backplane.Published.Clear();

        var operation = new CollaborationOperationWire
        {
            Id = "op-1",
            MapId = "map-a",
            Kind = "view",
            Revision = 1,
            Sequence = 1,
            Cursor = "1",
            AuthorId = "alice",
            SubmittedAt = clock.UtcNow
        };
        var envelope = service.PublishOperation("map-a", operation);

        envelope.Event.Type.Should().Be(CollaborationSessionEventTypes.OperationAppended);
        service.DrainEvents(alice.SessionId).Should().ContainSingle(e =>
            e.Event.Type == CollaborationSessionEventTypes.OperationAppended);
        service.DrainEvents(bob.SessionId).Should().ContainSingle(e =>
            e.Event.Operation != null && e.Event.Operation.Id == "op-1");
        backplane.Published.Should().ContainSingle(e =>
            e.Event.Type == CollaborationSessionEventTypes.OperationAppended);
    }

    [UnitTest]
    public async Task UpdateCursor_PublishesEventToBackplane_ForCrossNodeFanOut()
    {
        var clock = new FakeCollaborationClock(FixedUtcNow());
        var backplane = new RecordingBackplane();
        var service = new InMemoryCollaborationSessionService(
            new AllowSavedMapCollaborationAuthorizer(), clock, backplane);
        var alice = (await service.JoinAsync("map-a", new CollaborationJoinRequest { DisplayName = "Alice" }, Principal("alice"))).Response!;
        backplane.Published.Clear();

        service.UpdateCursor(alice.SessionId, new CollaborationCursor { X = 1, Y = 2 });

        backplane.Published.Should().ContainSingle(e => e.Event.Type == CollaborationSessionEventTypes.Cursor);
    }

    [UnitTest]
    public async Task ApplyRemoteEvent_FromPeerNode_DeliversToLocalParticipantsExcludingOrigin()
    {
        var clock = new FakeCollaborationClock(FixedUtcNow());
        var service = CreateService(clock);
        var local = (await service.JoinAsync("map-a", new CollaborationJoinRequest { DisplayName = "Local" }, Principal("local"))).Response!;
        _ = service.DrainEvents(local.SessionId);

        // Simulate a cursor event produced by a participant on another node.
        var remoteSession = Guid.NewGuid();
        var remote = new CollaborationEventEnvelope
        {
            MapId = "map-a",
            EventId = Guid.NewGuid().ToString("N"),
            Sequence = 500,
            Cursor = "500",
            ServerTime = clock.UtcNow,
            SessionId = remoteSession.ToString("N"),
            ActorId = remoteSession.ToString("N"),
            Event = new CollaborationSessionEvent
            {
                Type = CollaborationSessionEventTypes.Cursor,
                ParticipantId = remoteSession.ToString("N"),
                Cursor = new CollaborationCursor { X = -122.4, Y = 37.7 }
            }
        };
        service.ApplyRemoteEvent(remote);

        service.DrainEvents(local.SessionId).Should().ContainSingle(e =>
            e.Event.Type == CollaborationSessionEventTypes.Cursor &&
            e.Event.Cursor != null &&
            Math.Abs(e.Event.Cursor.X - -122.4) < 1e-9);

        // The local per-map sequence advances past the remote sequence so later local envelopes
        // stay ahead of everything already observed by clients.
        var next = service.UpdateCursor(local.SessionId, new CollaborationCursor { X = 0, Y = 0 });
        next.Event.Sequence.Should().BeGreaterThan(500);
    }

    [UnitTest]
    public async Task WaitForEventsAsync_SignalsWhenPeerEventArrives()
    {
        var clock = new FakeCollaborationClock(FixedUtcNow());
        var service = CreateService(clock);
        var alice = (await service.JoinAsync("map-a", new CollaborationJoinRequest { DisplayName = "Alice" }, Principal("alice"))).Response!;
        var bob = (await service.JoinAsync("map-a", new CollaborationJoinRequest { DisplayName = "Bob" }, Principal("bob"))).Response!;
        // Drain the join events so the wait below blocks until a new event arrives.
        _ = service.DrainEvents(alice.SessionId);
        _ = service.DrainEvents(bob.SessionId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var wait = service.WaitForEventsAsync(alice.SessionId, cts.Token);
        wait.IsCompleted.Should().BeFalse();

        service.UpdateCursor(bob.SessionId, new CollaborationCursor { X = 1, Y = 2 });

        (await wait).Should().BeTrue();
    }

    private static InMemoryCollaborationSessionService CreateService(FakeCollaborationClock clock) =>
        new(new AllowSavedMapCollaborationAuthorizer(), clock);

    private sealed class RecordingBackplane : ICollaborationSessionBackplane
    {
        public List<CollaborationEventEnvelope> Published { get; } = [];

        public void Publish(CollaborationEventEnvelope ev) => Published.Add(ev);
    }

    [UnitTest]
    public async Task ApplyRemoteEvent_LowerOriginSequence_RestampsDestinationMonotonic()
    {
        var clock = new FakeCollaborationClock(FixedUtcNow());
        var service = CreateService(clock);
        var local = (await service.JoinAsync("map-a", new CollaborationJoinRequest { DisplayName = "Local" }, Principal("local"))).Response!;
        _ = service.DrainEvents(local.SessionId);
        var lastLocalSequence = service.GetSnapshot("map-a").Sequence;

        // A remote node broadcast this operation with ITS node-local sequence (1), which is
        // already far behind this node's stream; the v1 reducer would drop it un-restamped.
        var remote = new CollaborationEventEnvelope
        {
            MapId = "map-a",
            EventId = Guid.NewGuid().ToString("N"),
            Sequence = 1,
            Cursor = "1",
            ServerTime = FixedUtcNow(),
            ActorId = "peer",
            Event = new CollaborationSessionEvent
            {
                Type = CollaborationSessionEventTypes.OperationAppended,
                Operation = CreateWireOperation("map-a", cursor: 1)
            }
        };

        service.ApplyRemoteEvent(remote);

        var delivered = service.DrainEvents(local.SessionId).Should().ContainSingle().Subject;
        delivered.Event.Type.Should().Be(CollaborationSessionEventTypes.OperationAppended);
        delivered.Sequence.Should().BeGreaterThan(lastLocalSequence);
        // The authoritative op-log cursor inside the event payload is preserved unchanged.
        delivered.Event.Operation!.Cursor.Should().Be("1");
    }

    [UnitTest]
    public async Task PublishOperation_OperationEvictedFromFullOutbox_EmitsResyncRequired()
    {
        var clock = new FakeCollaborationClock(FixedUtcNow());
        var service = CreateService(clock);
        var local = (await service.JoinAsync("map-a", new CollaborationJoinRequest { DisplayName = "Local" }, Principal("local"))).Response!;
        _ = service.DrainEvents(local.SessionId);

        // Never drain while more operations than the bounded outbox holds are committed: the
        // oldest operation frames must not vanish silently.
        var total = InMemoryCollaborationSessionService.MaxOutboxDepth + 5;
        for (var i = 1; i <= total; i++)
        {
            service.PublishOperation("map-a", CreateWireOperation("map-a", cursor: i));
        }

        var drained = service.DrainEvents(local.SessionId);
        drained.Should().Contain(e =>
            e.Event.Type == CollaborationSessionEventTypes.Error &&
            e.Event.Code == CollaborationErrorCodes.ResyncRequired &&
            e.Event.ResyncRequired == true);
        drained.Length.Should().BeLessThanOrEqualTo(InMemoryCollaborationSessionService.MaxOutboxDepth);

        // The newest committed operation is still delivered, and the resync notice follows it:
        // the notice necessarily draws a higher sequence than the event that triggered it, so
        // emitting it first would make a monotonic reducer discard that event.
        var newest = drained.Should().ContainSingle(e =>
            e.Event.Operation != null &&
            e.Event.Operation.Cursor == total.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Subject;
        var lastNotice = drained.Last(e => e.Event.Code == CollaborationErrorCodes.ResyncRequired);
        lastNotice.Sequence.Should().BeGreaterThan(newest.Sequence);
    }

    [UnitTest]
    public async Task PublishOperation_OutboxEviction_KeepsQueueInSequenceOrder()
    {
        var clock = new FakeCollaborationClock(FixedUtcNow());
        var service = CreateService(clock);
        var local = (await service.JoinAsync("map-a", new CollaborationJoinRequest { DisplayName = "Local" }, Principal("local"))).Response!;
        _ = service.DrainEvents(local.SessionId);

        // Overflow the outbox so the eviction/resync path runs, then keep publishing presence
        // frames through the same path.
        for (var i = 1; i <= InMemoryCollaborationSessionService.MaxOutboxDepth + 5; i++)
        {
            service.PublishOperation("map-a", CreateWireOperation("map-a", cursor: i));
        }

        var drained = service.DrainEvents(local.SessionId);

        // A monotonic reducer must be able to apply every delivered frame: queue order has to be
        // sequence order, otherwise the newer event is discarded and a cursor/presence update
        // would be lost with no op-log replay able to recover it.
        drained.Select(static e => e.Sequence).Should().BeInAscendingOrder();
        drained.Should().Contain(e => e.Event.Code == CollaborationErrorCodes.ResyncRequired);
    }

    [UnitTest]
    public async Task Leave_ForeignPrincipalOrWrongMap_DoesNotEvictParticipant()
    {
        var clock = new FakeCollaborationClock(FixedUtcNow());
        var service = CreateService(clock);
        var victim = (await service.JoinAsync("map-a", new CollaborationJoinRequest { DisplayName = "Victim" }, Principal("victim"))).Response!;

        // Participant ids equal session ids and are visible to every session member, so another
        // authenticated caller must not be able to eject a peer with a borrowed id.
        service.Leave(victim.SessionId, reason: "left", requiredMapId: "map-a", requiredOwner: Principal("attacker"))
            .Should().BeFalse();
        service.GetSnapshot("map-a").Participants.Should().ContainSingle();

        // A session id submitted against the wrong map route is rejected too.
        service.Leave(victim.SessionId, reason: "left", requiredMapId: "map-b", requiredOwner: Principal("victim"))
            .Should().BeFalse();
        service.GetSnapshot("map-a").Participants.Should().ContainSingle();

        // The owner on the correct map still leaves normally.
        service.Leave(victim.SessionId, reason: "left", requiredMapId: "map-a", requiredOwner: Principal("victim"))
            .Should().BeTrue();
        service.GetSnapshot("map-a").Participants.Should().BeEmpty();
    }

    [UnitTest]
    public async Task Leave_TwoDistinctAdminApiKeys_CannotEvictEachOther()
    {
        // ApiKeyAuthenticationHandler gives EVERY full-admin API key the literal name "admin"
        // and no NameIdentifier/sub, distinguishing keys only by api_key_id. Resolving identity
        // from the name therefore collapsed all admin collaborators onto one id, and the leave
        // ownership check then let any admin eject any other (honua-server#2999 review).
        var clock = new FakeCollaborationClock(FixedUtcNow());
        var service = CreateService(clock);
        var victimKey = Guid.NewGuid().ToString("D");
        var attackerKey = Guid.NewGuid().ToString("D");

        var victim = (await service.JoinAsync(
            "map-a",
            new CollaborationJoinRequest { DisplayName = "Victim" },
            AdminApiKeyPrincipal(victimKey))).Response!;

        service.Leave(
                victim.SessionId,
                reason: "left",
                requiredMapId: "map-a",
                requiredOwner: AdminApiKeyPrincipal(attackerKey))
            .Should().BeFalse("a different admin API key is a different collaborator");
        service.GetSnapshot("map-a").Participants.Should().ContainSingle();

        service.Leave(
                victim.SessionId,
                reason: "left",
                requiredMapId: "map-a",
                requiredOwner: AdminApiKeyPrincipal(victimKey))
            .Should().BeTrue("the owning key still leaves normally");
        service.GetSnapshot("map-a").Participants.Should().BeEmpty();
    }

    /// <summary>
    /// Reproduces the claim shape <c>ApiKeyAuthenticationHandler</c> emits for a full-admin key:
    /// a shared <see cref="ClaimTypes.Name"/> of "admin", no
    /// <see cref="ClaimTypes.NameIdentifier"/> and no <c>sub</c>, and a per-key
    /// <c>api_key_id</c>.
    /// </summary>
    private static ClaimsPrincipal AdminApiKeyPrincipal(string apiKeyId) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "admin"),
                new Claim(ClaimTypes.Role, "admin"),
                new Claim("api_key_id", apiKeyId),
            ],
            authenticationType: "Test"));

    private static CollaborationOperationWire CreateWireOperation(string mapId, long cursor) => new()
    {
        Id = $"op-{cursor}",
        MapId = mapId,
        Kind = "SetViewport",
        Revision = cursor,
        Sequence = cursor,
        Cursor = cursor.ToString(System.Globalization.CultureInfo.InvariantCulture),
        AuthorId = "peer",
        SubmittedAt = FixedUtcNow()
    };

    private static DateTimeOffset FixedUtcNow() =>
        new(2026, 5, 12, 0, 0, 0, TimeSpan.Zero);

    private static ClaimsPrincipal Principal(string userId, string? name = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId)
        };

        if (!string.IsNullOrWhiteSpace(name))
        {
            claims.Add(new Claim(ClaimTypes.Name, name));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
    }

    private sealed class FakeCollaborationClock : ICollaborationSessionClock
    {
        public FakeCollaborationClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; private set; }

        public void Advance(TimeSpan value)
        {
            UtcNow += value;
        }
    }

    private sealed class AllowSavedMapCollaborationAuthorizer : ISavedMapCollaborationAuthorizer
    {
        public ValueTask<SavedMapCollaborationAuthorizationResult> AuthorizeJoinAsync(
            string mapId,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(SavedMapCollaborationAuthorizationResult.Allow());
    }

    private sealed class DenySavedMapCollaborationAuthorizer : ISavedMapCollaborationAuthorizer
    {
        public ValueTask<SavedMapCollaborationAuthorizationResult> AuthorizeJoinAsync(
            string mapId,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(SavedMapCollaborationAuthorizationResult.Forbid(
                "Denied by test authorizer."));
    }
}
