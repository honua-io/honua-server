// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Console.Collaboration.Domain;
using Honua.Core.Features.Console.Collaboration.Services;
using Honua.Server.Features.Console.Collaboration;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Console.Collaboration;

/// <summary>
/// Fast unit coverage for the durable Studio map collaboration store (#1278, slice 1):
/// comment thread create/list/reply/resolve with anchor metadata and not-found
/// handling, plus the activity-feed projection and presentation mapper.
/// </summary>
public sealed class InMemoryStudioMapCollaborationStoreTests
{
    private const string MapId = "map-draft-1";

    private static (InMemoryStudioMapCollaborationStore Store, FakeTimeProvider Clock) NewStore()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 30, 12, 0, 0, TimeSpan.Zero));
        return (new InMemoryStudioMapCollaborationStore(clock), clock);
    }

    [UnitTest]
    public async Task CreateThread_PersistsAnchorMetadataAndFirstMessage()
    {
        var (store, _) = NewStore();

        var thread = await store.CreateThreadAsync(
            MapId, "Parcel 1042", "layer:parcels", 0.42, 0.73, "Kira Tan", "kira", "Boundary looks off here.");

        thread.MapId.Should().Be(MapId);
        thread.FeatureLabel.Should().Be("Parcel 1042");
        thread.LayerRef.Should().Be("layer:parcels");
        thread.AnchorX.Should().Be(0.42);
        thread.AnchorY.Should().Be(0.73);
        thread.Resolved.Should().BeFalse();
        thread.Messages.Should().ContainSingle();
        thread.Messages[0].Body.Should().Be("Boundary looks off here.");
        thread.Messages[0].AuthorName.Should().Be("Kira Tan");

        var fetched = await store.GetThreadAsync(MapId, thread.ThreadId);
        fetched.Should().NotBeNull();
        fetched!.ThreadId.Should().Be(thread.ThreadId);
    }

    [UnitTest]
    public async Task ListThreads_IsScopedToMap_AndOrderedByRecentActivity()
    {
        var (store, clock) = NewStore();
        var first = await store.CreateThreadAsync(MapId, "A", "layer:a", 0.1, 0.1, "User One", "u1", "first");
        clock.Advance(TimeSpan.FromMinutes(5));
        var second = await store.CreateThreadAsync(MapId, "B", "layer:b", 0.2, 0.2, "User Two", "u2", "second");
        await store.CreateThreadAsync("other-map", "C", "layer:c", 0.3, 0.3, "User Three", "u3", "third");

        // Reply on the older thread to bump it to the top.
        clock.Advance(TimeSpan.FromMinutes(5));
        await store.AddReplyAsync(MapId, first.ThreadId, "User One", "u1", "follow up");

        var threads = await store.ListThreadsAsync(MapId);

        threads.Should().HaveCount(2);
        threads[0].ThreadId.Should().Be(first.ThreadId, "most recent activity sorts first");
        threads[1].ThreadId.Should().Be(second.ThreadId);
    }

    [UnitTest]
    public async Task AddReply_AppendsMessage_AndBumpsUpdatedAt()
    {
        var (store, clock) = NewStore();
        var thread = await store.CreateThreadAsync(MapId, "Parcel", "layer:p", 0.5, 0.5, "Kira", "kira", "hi");
        clock.Advance(TimeSpan.FromMinutes(3));

        var updated = await store.AddReplyAsync(MapId, thread.ThreadId, "Lee", "lee", "agreed");

        updated.Should().NotBeNull();
        updated!.Messages.Should().HaveCount(2);
        updated.Messages[1].AuthorName.Should().Be("Lee");
        updated.UpdatedAt.Should().BeAfter(thread.UpdatedAt);
    }

    [UnitTest]
    public async Task AddReply_UnknownThread_ReturnsNull()
    {
        var (store, _) = NewStore();

        var result = await store.AddReplyAsync(MapId, Guid.NewGuid(), "Lee", "lee", "ghost reply");

        result.Should().BeNull();
    }

    [UnitTest]
    public async Task AddReply_WrongMap_ReturnsNull()
    {
        var (store, _) = NewStore();
        var thread = await store.CreateThreadAsync(MapId, "Parcel", "layer:p", 0.5, 0.5, "Kira", "kira", "hi");

        var result = await store.AddReplyAsync("different-map", thread.ThreadId, "Lee", "lee", "wrong map");

        result.Should().BeNull();
    }

    [UnitTest]
    public async Task SetResolved_TogglesFlag_AndUnknownThreadReturnsNull()
    {
        var (store, _) = NewStore();
        var thread = await store.CreateThreadAsync(MapId, "Parcel", "layer:p", 0.5, 0.5, "Kira", "kira", "hi");

        var resolved = await store.SetResolvedAsync(MapId, thread.ThreadId, true, "Kira", "kira");
        resolved.Should().NotBeNull();
        resolved!.Resolved.Should().BeTrue();

        var reopened = await store.SetResolvedAsync(MapId, thread.ThreadId, false, "Kira", "kira");
        reopened!.Resolved.Should().BeFalse();

        var missing = await store.SetResolvedAsync(MapId, Guid.NewGuid(), true, "Kira", "kira");
        missing.Should().BeNull();
    }

    [UnitTest]
    public async Task ActivityFeed_RecordsLifecycleEvents_NewestFirst()
    {
        var (store, clock) = NewStore();
        var thread = await store.CreateThreadAsync(MapId, "Parcel 7", "layer:p", 0.5, 0.5, "Kira Tan", "kira", "open");
        clock.Advance(TimeSpan.FromMinutes(1));
        await store.AddReplyAsync(MapId, thread.ThreadId, "Lee Ng", "lee", "reply");
        clock.Advance(TimeSpan.FromMinutes(1));
        await store.SetResolvedAsync(MapId, thread.ThreadId, true, "Kira Tan", "kira");

        var activity = await store.ListActivityAsync(MapId, 50);

        activity.Should().HaveCount(3);
        activity[0].Kind.Should().Be(StudioMapActivityKind.ThreadResolved);
        activity[1].Kind.Should().Be(StudioMapActivityKind.CommentPosted);
        activity[2].Kind.Should().Be(StudioMapActivityKind.ThreadOpened);
        activity[0].Action.Should().Contain("Parcel 7");
        activity.Should().OnlyContain(e => e.ThreadId == thread.ThreadId);
    }

    [UnitTest]
    public async Task ActivityFeed_RespectsLimit_AndMapScope()
    {
        var (store, clock) = NewStore();
        for (var i = 0; i < 5; i++)
        {
            await store.CreateThreadAsync(MapId, $"Feature {i}", "layer:p", 0.1, 0.1, "User", "u", "note");
            clock.Advance(TimeSpan.FromSeconds(30));
        }

        await store.CreateThreadAsync("other-map", "Other", "layer:o", 0.1, 0.1, "User", "u", "note");

        var limited = await store.ListActivityAsync(MapId, 3);
        limited.Should().HaveCount(3);
        limited.Should().OnlyContain(e => e.MapId == MapId);
    }

    [UnitTest]
    public void Mapper_DerivesInitials_Color_AndRelativeTime()
    {
        StudioMapCollaborationMapper.Initials("Kira Tan").Should().Be("KT");
        StudioMapCollaborationMapper.Initials("madison").Should().Be("MA");
        StudioMapCollaborationMapper.Initials("").Should().Be("?");

        // Deterministic per name.
        StudioMapCollaborationMapper.Color("Kira Tan").Should().Be(StudioMapCollaborationMapper.Color("Kira Tan"));
        StudioMapCollaborationMapper.Color("Kira Tan").Should().StartWith("#");

        var now = new DateTimeOffset(2026, 5, 30, 12, 0, 0, TimeSpan.Zero);
        StudioMapCollaborationMapper.RelativeTime(now.AddSeconds(-10), now).Should().Be("just now");
        StudioMapCollaborationMapper.RelativeTime(now.AddMinutes(-5), now).Should().Be("5m ago");
        StudioMapCollaborationMapper.RelativeTime(now.AddHours(-3), now).Should().Be("3h ago");
        StudioMapCollaborationMapper.RelativeTime(now.AddDays(-2), now).Should().Be("2d ago");
    }

    [UnitTest]
    public async Task Mapper_ThreadDto_MatchesConsoleShape()
    {
        var (store, _) = NewStore();
        var thread = await store.CreateThreadAsync(MapId, "Parcel 1042", "layer:parcels", 0.42, 0.73, "Kira Tan", "kira", "note");

        var dto = StudioMapCollaborationMapper.ToThreadDto(thread, thread.CreatedAt);

        dto.ThreadId.Should().Be(thread.ThreadId.ToString("D"));
        dto.FeatureLabel.Should().Be("Parcel 1042");
        dto.LayerRef.Should().Be("layer:parcels");
        dto.XFraction.Should().Be(0.42);
        dto.YFraction.Should().Be(0.73);
        dto.CommentCount.Should().Be(1);
        dto.Resolved.Should().BeFalse();
        dto.Messages.Should().ContainSingle();
        dto.Messages[0].AuthorInitials.Should().Be("KT");
        dto.Messages[0].RelativeTime.Should().Be("just now");
    }

    /// <summary>
    /// Minimal controllable clock for deterministic store tests. Mirrors the local
    /// <c>FakeTimeProvider</c> convention used elsewhere in the test suite (no
    /// external test-time package dependency) while supporting <see cref="Advance"/>.
    /// </summary>
    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }
}
