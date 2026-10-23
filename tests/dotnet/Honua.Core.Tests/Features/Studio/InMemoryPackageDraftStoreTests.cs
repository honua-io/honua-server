// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Studio.Drafts;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Studio;

/// <summary>
/// Unit tests for <see cref="InMemoryPackageDraftStore"/>, the surface that makes the
/// <c>map_…</c> / <c>app_…</c> identifier the draft factories mint actually resolvable at its
/// <c>honua://map-packages/{id}</c> / <c>honua://app-packages/{id}</c> URI (ADR-0076 amendment,
/// honua-server#3262). They pin round-trip, the two retention bounds, and the kind separation.
/// </summary>
public sealed class InMemoryPackageDraftStoreTests
{
    private const string Owner = "owner-a";
    private const string OtherOwner = "owner-b";
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [UnitTest]
    public async Task SaveThenGet_MapDraft_RoundTripsUnderTheMintedIdentifier()
    {
        var (store, _) = CreateStore();
        var package = NewMapPackage("map_alpha");

        await store.SaveMapDraftAsync(package, Owner);

        Assert.Same(package, await store.GetMapDraftAsync("map_alpha", Owner));
    }

    [UnitTest]
    public async Task SaveThenGet_AppDraft_RoundTripsUnderTheMintedIdentifier()
    {
        var (store, _) = CreateStore();
        var package = NewAppPackage("app_alpha");

        await store.SaveAppDraftAsync(package, Owner);

        Assert.Same(package, await store.GetAppDraftAsync("app_alpha", Owner));
    }

    [UnitTest]
    public async Task Get_UnknownIdentifier_ReturnsNull()
    {
        var (store, _) = CreateStore();

        Assert.Null(await store.GetMapDraftAsync("map_never_created", Owner));
        Assert.Null(await store.GetAppDraftAsync("app_never_created", Owner));
    }

    [UnitTest]
    public async Task Get_MapAndAppIdentifiersAreSeparateNamespaces()
    {
        // The two kinds are separate resource families; a map draft must never satisfy an
        // app-package read even if the identifiers collided.
        var (store, _) = CreateStore();
        await store.SaveMapDraftAsync(NewMapPackage("shared_id"), Owner);

        Assert.NotNull(await store.GetMapDraftAsync("shared_id", Owner));
        Assert.Null(await store.GetAppDraftAsync("shared_id", Owner));
    }

    [UnitTest]
    public async Task Get_AfterTtlElapses_ReturnsNull()
    {
        var (store, clock) = CreateStore(new PackageDraftRetentionOptions { Ttl = TimeSpan.FromHours(1) });
        await store.SaveMapDraftAsync(NewMapPackage("map_aging"), Owner);

        clock.Advance(TimeSpan.FromMinutes(59));
        Assert.NotNull(await store.GetMapDraftAsync("map_aging", Owner));

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(await store.GetMapDraftAsync("map_aging", Owner));
    }

    [UnitTest]
    public async Task Save_BeyondCapacity_EvictsOldestFirst()
    {
        var (store, clock) = CreateStore(new PackageDraftRetentionOptions { Capacity = 2 });

        await store.SaveMapDraftAsync(NewMapPackage("map_1"), Owner);
        clock.Advance(TimeSpan.FromSeconds(1));
        await store.SaveMapDraftAsync(NewMapPackage("map_2"), Owner);
        clock.Advance(TimeSpan.FromSeconds(1));
        await store.SaveMapDraftAsync(NewMapPackage("map_3"), Owner);

        // The draft still being composed is the most recently written, so it survives.
        Assert.Null(await store.GetMapDraftAsync("map_1", Owner));
        Assert.NotNull(await store.GetMapDraftAsync("map_2", Owner));
        Assert.NotNull(await store.GetMapDraftAsync("map_3", Owner));
    }

    [UnitTest]
    public async Task Save_SameIdentifierTwice_KeepsTheLatestDraft()
    {
        var (store, _) = CreateStore();
        await store.SaveMapDraftAsync(NewMapPackage("map_alpha"), Owner);
        var replacement = NewMapPackage("map_alpha") with { TemplateId = "replaced" };

        await store.SaveMapDraftAsync(replacement, Owner);

        Assert.Equal("replaced", (await store.GetMapDraftAsync("map_alpha", Owner))!.TemplateId);
    }

    [UnitTest]
    public async Task Save_WithoutOwner_WritesNothing()
    {
        var (store, _) = CreateStore();
        var package = NewMapPackage("map_unowned");

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveMapDraftAsync(package, " "));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAppDraftAsync(NewAppPackage("app_unowned"), ""));

        Assert.Null(await store.GetMapDraftAsync(package.MapPackageId, Owner));
        Assert.Null(await store.GetAppDraftAsync("app_unowned", Owner));
    }

    [UnitTest]
    public async Task Get_DifferentOwner_MatchesAMissingDraft()
    {
        var (store, _) = CreateStore();
        await store.SaveMapDraftAsync(NewMapPackage("map_owned"), Owner);
        await store.SaveAppDraftAsync(NewAppPackage("app_owned"), Owner);

        Assert.Null(await store.GetMapDraftAsync("map_owned", OtherOwner));
        Assert.Null(await store.GetAppDraftAsync("app_owned", OtherOwner));
        Assert.Null(await store.GetMapDraftAsync("map_owned", " "));

        // The miss must not delete or replace the owner's draft.
        Assert.NotNull(await store.GetMapDraftAsync("map_owned", Owner));
        Assert.NotNull(await store.GetAppDraftAsync("app_owned", Owner));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.SaveMapDraftAsync(NewMapPackage("map_owned") with { TemplateId = "stolen" }, OtherOwner));
        Assert.Null((await store.GetMapDraftAsync("map_owned", Owner))!.TemplateId);
    }

    [UnitTest]
    public async Task TwoInstances_OverOneBacking_ReadWhatTheOtherWrote()
    {
        var (writer, reader, _) = CreateSharedPair();
        var map = NewMapPackage("map_shared");
        var app = NewAppPackage("app_shared");

        await writer.SaveMapDraftAsync(map, Owner);
        await writer.SaveAppDraftAsync(app, Owner);

        Assert.Same(map, await reader.GetMapDraftAsync("map_shared", Owner));
        Assert.Same(app, await reader.GetAppDraftAsync("app_shared", Owner));
        Assert.Null(await reader.GetMapDraftAsync("map_shared", OtherOwner));
        Assert.Null(await reader.GetAppDraftAsync("app_shared", OtherOwner));
        Assert.Null(await reader.GetAppDraftAsync("map_shared", Owner));
    }

    [UnitTest]
    public async Task SharedBacking_AfterTtlElapses_ReadsAsNotFound()
    {
        var (writer, reader, clock) = CreateSharedPair(new PackageDraftRetentionOptions { Ttl = TimeSpan.FromHours(1) });
        await writer.SaveMapDraftAsync(NewMapPackage("map_aging"), Owner);
        await writer.SaveAppDraftAsync(NewAppPackage("app_aging"), Owner);

        clock.Advance(TimeSpan.FromMinutes(59));
        Assert.NotNull(await reader.GetMapDraftAsync("map_aging", Owner));

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(await reader.GetMapDraftAsync("map_aging", Owner));
        Assert.Null(await writer.GetAppDraftAsync("app_aging", Owner));
    }

    [UnitTest]
    public async Task SharedBacking_BeyondCapacity_EvictsOldestOfThatKind()
    {
        var (writer, reader, clock) = CreateSharedPair(new PackageDraftRetentionOptions { Capacity = 2 });

        await writer.SaveMapDraftAsync(NewMapPackage("map_1"), Owner);
        clock.Advance(TimeSpan.FromSeconds(1));
        await writer.SaveMapDraftAsync(NewMapPackage("map_2"), Owner);
        clock.Advance(TimeSpan.FromSeconds(1));
        await writer.SaveAppDraftAsync(NewAppPackage("app_kept"), Owner);
        clock.Advance(TimeSpan.FromSeconds(1));
        await writer.SaveMapDraftAsync(NewMapPackage("map_3"), Owner);

        Assert.Null(await reader.GetMapDraftAsync("map_1", Owner));
        Assert.NotNull(await reader.GetMapDraftAsync("map_2", Owner));
        Assert.NotNull(await reader.GetMapDraftAsync("map_3", Owner));
        Assert.NotNull(await reader.GetAppDraftAsync("app_kept", Owner));
    }

    [UnitTest]
    public async Task TwoDefaultInstances_DoNotShareDrafts()
    {
        var clock = new MutableClock(FixedNow);
        var options = new PackageDraftRetentionOptions();
        var first = new InMemoryPackageDraftStore(options, clock);
        var second = new InMemoryPackageDraftStore(options, clock);

        await first.SaveMapDraftAsync(NewMapPackage("map_local"), Owner);

        Assert.NotNull(await first.GetMapDraftAsync("map_local", Owner));
        Assert.Null(await second.GetMapDraftAsync("map_local", Owner));
    }

    private static (InMemoryPackageDraftStore Store, MutableClock Clock) CreateStore(
        PackageDraftRetentionOptions? retention = null)
    {
        var clock = new MutableClock(FixedNow);
        return (new InMemoryPackageDraftStore(retention ?? new PackageDraftRetentionOptions(), clock), clock);
    }

    private static (InMemoryPackageDraftStore Writer, InMemoryPackageDraftStore Reader, MutableClock Clock) CreateSharedPair(
        PackageDraftRetentionOptions? retention = null)
    {
        var clock = new MutableClock(FixedNow);
        var options = retention ?? new PackageDraftRetentionOptions();
        var backing = new MemoryPackageDraftBacking(clock);
        return (
            new InMemoryPackageDraftStore(options, clock, backing),
            new InMemoryPackageDraftStore(options, clock, backing),
            clock);
    }

    /// <summary>Hand-rolled advanceable clock; retention is an age rule, so time must be pinned.</summary>
    private sealed class MutableClock(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public void Advance(TimeSpan delta) => _utcNow += delta;

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    private static MapPackage NewMapPackage(string id) => new()
    {
        MapPackageId = id,
        Format = MapPackageDraftFactory.MapPackageFormat,
        Status = PackageStatus.Draft,
        CreatedAt = FixedNow,
    };

    private static AppPackage NewAppPackage(string id) => new()
    {
        AppPackageId = id,
        TargetSdk = AppPackageDraftFactory.DefaultTargetSdk,
        Format = AppPackageDraftFactory.AppPackageFormat,
        Status = PackageStatus.Draft,
        CreatedAt = FixedNow,
    };
}
