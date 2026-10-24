// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Studio.Drafts;

/// <summary>
/// Owner check, TTL read, and per-kind capacity shared by every
/// <see cref="IPackageDraftStore"/>. The backing decides whether the bytes are
/// process-local or shared; this type does not.
/// </summary>
internal sealed class PackageDraftLedger : IPackageDraftStore
{
    private readonly IPackageDraftBacking _backing;
    private readonly PackageDraftRetentionOptions _retention;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="PackageDraftLedger"/> class.</summary>
    /// <param name="backing">Where records are held.</param>
    /// <param name="retention">TTL and per-kind capacity.</param>
    /// <param name="timeProvider">Clock stamped onto each write.</param>
    public PackageDraftLedger(
        IPackageDraftBacking backing,
        PackageDraftRetentionOptions retention,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(backing);
        ArgumentNullException.ThrowIfNull(retention);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _backing = backing;
        _retention = retention;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public Task SaveMapDraftAsync(MapPackage package, string ownerId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        return SaveAsync(PackageDraftKind.Map, package.MapPackageId, ownerId, package, app: null, cancellationToken);
    }

    /// <inheritdoc />
    public Task SaveAppDraftAsync(AppPackage package, string ownerId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        return SaveAsync(PackageDraftKind.App, package.AppPackageId, ownerId, map: null, package, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<MapPackage?> GetMapDraftAsync(
        string mapPackageId,
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        var record = await ReadOwnedAsync(PackageDraftKind.Map, mapPackageId, ownerId, cancellationToken)
            .ConfigureAwait(false);
        return record?.Map;
    }

    /// <inheritdoc />
    public async Task<AppPackage?> GetAppDraftAsync(
        string appPackageId,
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        var record = await ReadOwnedAsync(PackageDraftKind.App, appPackageId, ownerId, cancellationToken)
            .ConfigureAwait(false);
        return record?.App;
    }

    private async Task SaveAsync(
        PackageDraftKind kind,
        string id,
        string ownerId,
        MapPackage? map,
        AppPackage? app,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            throw new ArgumentException("A package draft requires an owner.", nameof(ownerId));
        }

        var existing = await _backing.TryGetAsync(kind, id, _retention.Ttl, cancellationToken).ConfigureAwait(false);
        if (existing is not null && !string.Equals(existing.OwnerId, ownerId, StringComparison.Ordinal))
        {
            // Same failure family as a missing owner: the previous owner's draft stays,
            // and the message does not name that owner or confirm who can read it.
            throw new ArgumentException("Package draft was not saved.", nameof(ownerId));
        }

        var record = new PackageDraftRecord(ownerId, _timeProvider.GetUtcNow(), map, app);
        await _backing.UpsertAsync(kind, id, record, _retention.Ttl, cancellationToken).ConfigureAwait(false);
        await TrimAsync(kind, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PackageDraftRecord?> ReadOwnedAsync(
        PackageDraftKind kind,
        string id,
        string ownerId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(ownerId))
        {
            return null;
        }

        var record = await _backing.TryGetAsync(kind, id, _retention.Ttl, cancellationToken).ConfigureAwait(false);
        if (record is null || !string.Equals(record.OwnerId, ownerId, StringComparison.Ordinal))
        {
            return null;
        }

        return record;
    }

    private async Task TrimAsync(PackageDraftKind kind, CancellationToken cancellationToken)
    {
        var live = await _backing
            .ListLiveOldestFirstAsync(kind, _retention.Ttl, cancellationToken)
            .ConfigureAwait(false);
        if (live.Count <= _retention.Capacity)
        {
            return;
        }

        var overflow = live.Count - _retention.Capacity;
        for (var index = 0; index < overflow; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _backing.RemoveAsync(kind, live[index].Id, cancellationToken).ConfigureAwait(false);
        }
    }
}
