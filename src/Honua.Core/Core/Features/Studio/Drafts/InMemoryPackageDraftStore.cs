// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Studio.Drafts;

// Single-process package draft store for a host with no system database: each instance owns a
// private backing, so two processes do not see each other's drafts. Tests that need the replica
// contract pass one backing into both instances.
public sealed class InMemoryPackageDraftStore : IPackageDraftStore
{
    private readonly PackageDraftLedger _ledger;

    public InMemoryPackageDraftStore(PackageDraftRetentionOptions retention, TimeProvider timeProvider)
        : this(retention, timeProvider, new MemoryPackageDraftBacking(timeProvider))
    {
    }

    internal InMemoryPackageDraftStore(
        PackageDraftRetentionOptions retention,
        TimeProvider timeProvider,
        IPackageDraftBacking backing)
    {
        _ledger = new PackageDraftLedger(backing, retention, timeProvider);
    }

    public Task SaveMapDraftAsync(MapPackage package, string ownerId, CancellationToken cancellationToken = default) =>
        _ledger.SaveMapDraftAsync(package, ownerId, cancellationToken);

    public Task SaveAppDraftAsync(AppPackage package, string ownerId, CancellationToken cancellationToken = default) =>
        _ledger.SaveAppDraftAsync(package, ownerId, cancellationToken);

    public Task<MapPackage?> GetMapDraftAsync(
        string mapPackageId,
        string ownerId,
        CancellationToken cancellationToken = default) =>
        _ledger.GetMapDraftAsync(mapPackageId, ownerId, cancellationToken);

    public Task<AppPackage?> GetAppDraftAsync(
        string appPackageId,
        string ownerId,
        CancellationToken cancellationToken = default) =>
        _ledger.GetAppDraftAsync(appPackageId, ownerId, cancellationToken);
}
