// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Studio.Drafts;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresPackageDraftBackingTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    private const string Owner = "owner-a";
    private static readonly TimeSpan _ttl = TimeSpan.FromHours(24);

    [IntegrationTest]
    public async Task TryGetAsync_AfterTtl_ReturnsNull()
    {
        await using var harness = await CreateHarnessAsync();
        var backing = harness.CreatePackageDraftBacking();
        await backing.UpsertAsync(new PackageDraftWrite(PackageDraftKind.Map, "map_aging", MapRecord("map_aging"), _ttl), CancellationToken.None);
        (await backing.TryGetAsync(PackageDraftKind.Map, "map_aging", _ttl, CancellationToken.None)).Should().NotBeNull();

        await harness.ExecuteAsync($"UPDATE {harness.Table("package_drafts")} SET expires_at = now() - interval '1 second'");

        (await backing.TryGetAsync(PackageDraftKind.Map, "map_aging", _ttl, CancellationToken.None)).Should().BeNull();
        (await backing.DeleteExpiredAsync(100, CancellationToken.None)).Should().Be(1);
    }

    [IntegrationTest]
    public async Task ListLiveOldestFirstAsync_ReturnsOnlyLiveDraftsOldestFirst()
    {
        await using var harness = await CreateHarnessAsync();
        var backing = harness.CreatePackageDraftBacking();
        var start = DateTimeOffset.UtcNow;
        await backing.UpsertAsync(new PackageDraftWrite(PackageDraftKind.Map, "map_new", MapRecord("map_new", start.AddMinutes(2)), _ttl), CancellationToken.None);
        await backing.UpsertAsync(new PackageDraftWrite(PackageDraftKind.Map, "map_old", MapRecord("map_old", start), _ttl), CancellationToken.None);
        await backing.UpsertAsync(new PackageDraftWrite(PackageDraftKind.Map, "map_gone", MapRecord("map_gone", start.AddMinutes(1)), _ttl), CancellationToken.None);
        await backing.UpsertAsync(new PackageDraftWrite(PackageDraftKind.App, "app_other", AppRecord("app_other"), _ttl), CancellationToken.None);
        await harness.ExecuteAsync(
            $"UPDATE {harness.Table("package_drafts")} SET expires_at = now() - interval '1 second' WHERE draft_id = 'map_gone'");

        var live = await backing.ListLiveOldestFirstAsync(PackageDraftKind.Map, _ttl), CancellationToken.None);

        live.Select(entry => entry.Id).Should().Equal("map_old", "map_new");
    }

    [IntegrationTest]
    public async Task SaveMapDraftAsync_OnOneNode_IsReadOnAnotherByItsOwnerOnly()
    {
        await using var harness = await CreateHarnessAsync();
        var writer = new PackageDraftLedger(harness.CreatePackageDraftBacking(), new PackageDraftRetentionOptions(), TimeProvider.System);
        var reader = new PackageDraftLedger(harness.CreatePackageDraftBacking(), new PackageDraftRetentionOptions(), TimeProvider.System);
        await writer.SaveMapDraftAsync(MapPackageNamed("map_shared"), Owner);
        await writer.SaveAppDraftAsync(AppPackageNamed("app_shared"), Owner);

        (await reader.GetMapDraftAsync("map_shared", Owner))!.MapPackageId.Should().Be("map_shared");
        (await reader.GetAppDraftAsync("app_shared", Owner))!.TargetSdk.Should().Be(AppPackageDraftFactory.DefaultTargetSdk);
        (await reader.GetMapDraftAsync("map_shared", "owner-b")).Should().BeNull();
        (await reader.GetAppDraftAsync("map_shared", Owner)).Should().BeNull();
    }

    private Task<ControlPlaneStateHarness> CreateHarnessAsync()
        => ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresPackageDraftBackingTests));

    private static PackageDraftRecord MapRecord(string id, DateTimeOffset? writtenAt = null)
        => new(Owner, writtenAt ?? DateTimeOffset.UtcNow, MapPackageNamed(id), null);

    private static PackageDraftRecord AppRecord(string id) => new(Owner, DateTimeOffset.UtcNow, null, AppPackageNamed(id));

    private static MapPackage MapPackageNamed(string id) => new()
    {
        MapPackageId = id,
        Format = MapPackageDraftFactory.MapPackageFormat,
        Status = PackageStatus.Draft,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static AppPackage AppPackageNamed(string id) => new()
    {
        AppPackageId = id,
        TargetSdk = AppPackageDraftFactory.DefaultTargetSdk,
        Format = AppPackageDraftFactory.AppPackageFormat,
        Status = PackageStatus.Draft,
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
