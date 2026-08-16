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
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [UnitTest]
    public async Task SaveThenGet_MapDraft_RoundTripsUnderTheMintedIdentifier()
    {
        var (store, _) = CreateStore();
        var package = NewMapPackage("map_alpha");

        await store.SaveMapDraftAsync(package);

        Assert.Same(package, await store.GetMapDraftAsync("map_alpha"));
    }

    [UnitTest]
    public async Task SaveThenGet_AppDraft_RoundTripsUnderTheMintedIdentifier()
    {
        var (store, _) = CreateStore();
        var package = NewAppPackage("app_alpha");

        await store.SaveAppDraftAsync(package);

        Assert.Same(package, await store.GetAppDraftAsync("app_alpha"));
    }

    [UnitTest]
    public async Task Get_UnknownIdentifier_ReturnsNull()
    {
        var (store, _) = CreateStore();

        Assert.Null(await store.GetMapDraftAsync("map_never_created"));
        Assert.Null(await store.GetAppDraftAsync("app_never_created"));
    }

    [UnitTest]
    public async Task Get_MapAndAppIdentifiersAreSeparateNamespaces()
    {
        // The two kinds are separate resource families; a map draft must never satisfy an
        // app-package read even if the identifiers collided.
        var (store, _) = CreateStore();
        await store.SaveMapDraftAsync(NewMapPackage("shared_id"));

        Assert.NotNull(await store.GetMapDraftAsync("shared_id"));
        Assert.Null(await store.GetAppDraftAsync("shared_id"));
    }

    [UnitTest]
    public async Task Get_AfterTtlElapses_ReturnsNull()
    {
        var (store, clock) = CreateStore(new PackageDraftRetentionOptions { Ttl = TimeSpan.FromHours(1) });
        await store.SaveMapDraftAsync(NewMapPackage("map_aging"));

        clock.Advance(TimeSpan.FromMinutes(59));
        Assert.NotNull(await store.GetMapDraftAsync("map_aging"));

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(await store.GetMapDraftAsync("map_aging"));
    }

    [UnitTest]
    public async Task Save_BeyondCapacity_EvictsOldestFirst()
    {
        var (store, clock) = CreateStore(new PackageDraftRetentionOptions { Capacity = 2 });

        await store.SaveMapDraftAsync(NewMapPackage("map_1"));
        clock.Advance(TimeSpan.FromSeconds(1));
        await store.SaveMapDraftAsync(NewMapPackage("map_2"));
        clock.Advance(TimeSpan.FromSeconds(1));
        await store.SaveMapDraftAsync(NewMapPackage("map_3"));

        // The draft still being composed is the most recently written, so it survives.
        Assert.Null(await store.GetMapDraftAsync("map_1"));
        Assert.NotNull(await store.GetMapDraftAsync("map_2"));
        Assert.NotNull(await store.GetMapDraftAsync("map_3"));
    }

    [UnitTest]
    public async Task Save_SameIdentifierTwice_KeepsTheLatestDraft()
    {
        var (store, _) = CreateStore();
        await store.SaveMapDraftAsync(NewMapPackage("map_alpha"));
        var replacement = NewMapPackage("map_alpha") with { TemplateId = "replaced" };

        await store.SaveMapDraftAsync(replacement);

        Assert.Equal("replaced", (await store.GetMapDraftAsync("map_alpha"))!.TemplateId);
    }

    private static (InMemoryPackageDraftStore Store, MutableClock Clock) CreateStore(
        PackageDraftRetentionOptions? retention = null)
    {
        var clock = new MutableClock(FixedNow);
        return (new InMemoryPackageDraftStore(retention ?? new PackageDraftRetentionOptions(), clock), clock);
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
