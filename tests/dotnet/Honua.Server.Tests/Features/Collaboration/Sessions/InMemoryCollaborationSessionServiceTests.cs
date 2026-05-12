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
        result.Response!.Snapshot.MapId.Should().Be("saved-map:ops");
        result.Response.Snapshot.Participants.Should().ContainSingle();
        result.Response.Participant.DisplayName.Should().Be("Ada");
        result.Response.Participant.UserId.Should().Be("user-1");
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
            new CollaborationCursor { Longitude = -157.8583, Latitude = 21.3069, Zoom = 12 });

        fanOut.DeliveredCount.Should().Be(1);
        service.DrainEvents(alice.SessionId).Should().BeEmpty();
        var bobEvents = service.DrainEvents(bob.SessionId);
        bobEvents.Should().ContainSingle();
        bobEvents[0].Type.Should().Be(CollaborationSessionEventTypes.CursorUpdated);
        bobEvents[0].Cursor!.Longitude.Should().Be(-157.8583);
        service.DrainEvents(cara.SessionId).Should().BeEmpty();
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
        followEvents[0].Type.Should().Be(CollaborationSessionEventTypes.FollowUpdated);
        followEvents[0].Follow!.FollowingSessionId.Should().Be(leader.SessionId);

        service.Unfollow(follower.SessionId);

        var unfollowEvents = service.DrainEvents(leader.SessionId);
        unfollowEvents.Should().ContainSingle();
        unfollowEvents[0].Type.Should().Be(CollaborationSessionEventTypes.FollowCleared);
        unfollowEvents[0].Follow!.IsFollowing.Should().BeFalse();
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
        service.GetSnapshot("map-a").Participants.Single().Follow.IsFollowing.Should().BeFalse();
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

        service.GetSnapshot("map-a").Participants.Should().ContainSingle(p => p.SessionId == alice.SessionId);
        service.DrainEvents(alice.SessionId).Should().ContainSingle(e =>
            e.Type == CollaborationSessionEventTypes.ParticipantLeft &&
            e.SessionId == bob.SessionId);
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

        service.GetSnapshot("map-a").Participants.Single().Follow.IsFollowing.Should().BeFalse();
        service.DrainEvents(follower.SessionId).Should().Contain(e =>
            e.Type == CollaborationSessionEventTypes.FollowCleared &&
            e.SessionId == follower.SessionId);
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
        snapshot.Participants.Should().ContainSingle(p => p.SessionId == active.SessionId);
        snapshot.Participants.Should().NotContain(p => p.SessionId == stale.SessionId);
        service.DrainEvents(active.SessionId).Should().ContainSingle(e =>
            e.Type == CollaborationSessionEventTypes.ParticipantLeft &&
            e.SessionId == stale.SessionId);
    }

    [UnitTest]
    public async Task JoinAsync_WithoutSavedMapAuthorizer_FailsClosed()
    {
        var service = new InMemoryCollaborationSessionService(
            new FailClosedSavedMapCollaborationAuthorizer(),
            new FakeCollaborationClock(FixedUtcNow()));

        var result = await service.JoinAsync(
            "saved-map:ops",
            new CollaborationJoinRequest { DisplayName = "Nope" },
            new ClaimsPrincipal(new ClaimsIdentity()));

        result.Response.Should().BeNull();
        result.Authorization.Status.Should().Be(SavedMapCollaborationAuthorizationStatus.RequiresAuthentication);
    }

    private static InMemoryCollaborationSessionService CreateService(FakeCollaborationClock clock) =>
        new(new AllowSavedMapCollaborationAuthorizer(), clock);

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
}
