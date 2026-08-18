// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Studio.Drafts;

/// <summary>
/// Default <see cref="IPackageDraftStore"/>: an in-process, age-bounded map from
/// the minted <c>map_…</c> / <c>app_…</c> identifier to the draft package
/// (ADR-0076, honua-server#3262).
/// </summary>
/// <remarks>
/// <para>
/// In-process is the deliberate default rather than a placeholder. A draft is
/// pre-publish scratch with no cross-request contract beyond the composition
/// session that created it: the moment it is promoted to a deployment it becomes
/// resolvable through the deployment store, which is durable and shared. Adding a
/// schema-bearing table for state whose whole purpose is to be short-lived buys
/// retention nobody has asked for and a migration everybody pays for.
/// </para>
/// <para>
/// The consequence is recorded rather than hidden: on a multi-replica deployment
/// a draft resolves only on the replica that created it, and it does not survive
/// a restart. Both are acceptable for scratch state and both are visible as an
/// ordinary "not found", which is exactly the response an expired draft gives.
/// A durable, shared backing is a separate decision with its own retention and
/// authorization questions.
/// </para>
/// </remarks>
public sealed class InMemoryPackageDraftStore : IPackageDraftStore
{
    private readonly ConcurrentDictionary<string, Entry<MapPackage>> _maps = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Entry<AppPackage>> _apps = new(StringComparer.Ordinal);
    private readonly PackageDraftRetentionOptions _retention;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryPackageDraftStore"/> class.
    /// </summary>
    /// <param name="retention">Age and capacity bounds.</param>
    /// <param name="timeProvider">Clock used for expiry, injected so tests can pin it.</param>
    public InMemoryPackageDraftStore(PackageDraftRetentionOptions retention, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(retention);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _retention = retention;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public Task SaveMapDraftAsync(MapPackage package, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        Save(_maps, package.MapPackageId, package);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SaveAppDraftAsync(AppPackage package, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        Save(_apps, package.AppPackageId, package);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<MapPackage?> GetMapDraftAsync(string mapPackageId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Get(_maps, mapPackageId));

    /// <inheritdoc />
    public Task<AppPackage?> GetAppDraftAsync(string appPackageId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Get(_apps, appPackageId));

    private void Save<T>(ConcurrentDictionary<string, Entry<T>> store, string id, T package)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var now = _timeProvider.GetUtcNow();
        store[id] = new Entry<T>(package, now);
        Trim(store, now);
    }

    private T? Get<T>(ConcurrentDictionary<string, Entry<T>> store, string id)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(id) || !store.TryGetValue(id, out var entry))
        {
            return null;
        }

        if (IsExpired(entry, _timeProvider.GetUtcNow()))
        {
            // Read-time expiry keeps the store honest without a background sweeper:
            // an aged-out draft is removed the first time anybody looks for it.
            store.TryRemove(id, out _);
            return null;
        }

        return entry.Package;
    }

    private void Trim<T>(ConcurrentDictionary<string, Entry<T>> store, DateTimeOffset now)
        where T : class
    {
        foreach (var pair in store)
        {
            if (IsExpired(pair.Value, now))
            {
                store.TryRemove(pair.Key, out _);
            }
        }

        if (store.Count <= _retention.Capacity)
        {
            return;
        }

        // Oldest-first eviction: a draft still being composed was written most
        // recently, so it is the last one dropped.
        foreach (var pair in store.OrderBy(pair => pair.Value.CreatedAt).Take(store.Count - _retention.Capacity))
        {
            store.TryRemove(pair.Key, out _);
        }
    }

    private bool IsExpired<T>(Entry<T> entry, DateTimeOffset now)
        where T : class =>
        now - entry.CreatedAt >= _retention.Ttl;

    private sealed record Entry<T>(T Package, DateTimeOffset CreatedAt)
        where T : class;
}
