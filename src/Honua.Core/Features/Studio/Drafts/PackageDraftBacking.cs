// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Studio.Drafts;

/// <summary>Which package family a draft record belongs to. The two kinds never share a key space.</summary>
internal enum PackageDraftKind
{
    /// <summary>A <see cref="MapPackage"/> draft.</summary>
    Map,

    /// <summary>An <see cref="AppPackage"/> draft.</summary>
    App,
}

/// <summary>One live draft plus the subject allowed to read it.</summary>
/// <param name="OwnerId">Caller subject recorded at save.</param>
/// <param name="WrittenAt">Utc instant of the write that started the TTL.</param>
/// <param name="Map">Map package when <see cref="PackageDraftKind.Map"/>; otherwise null.</param>
/// <param name="App">App package when <see cref="PackageDraftKind.App"/>; otherwise null.</param>
internal sealed record PackageDraftRecord(
    string OwnerId,
    DateTimeOffset WrittenAt,
    MapPackage? Map,
    AppPackage? App);

/// <summary>Identifier and write time of a draft the backing still considers live.</summary>
/// <param name="Id">Minted package identifier.</param>
/// <param name="WrittenAt">Utc instant of the write that started the TTL.</param>
internal readonly record struct PackageDraftLiveEntry(string Id, DateTimeOffset WrittenAt);

/// <summary>
/// Persistence for <see cref="PackageDraftLedger"/>. A memory instance is private to
/// one process. A shared instance is what makes two store objects agree.
/// </summary>
internal interface IPackageDraftBacking
{
    /// <summary>Inserts or replaces the record and starts its TTL from <paramref name="record"/>'s write time.</summary>
    Task UpsertAsync(
        PackageDraftKind kind,
        string id,
        PackageDraftRecord record,
        TimeSpan ttl,
        CancellationToken cancellationToken);

    /// <summary>Returns the record, or null when it is missing or already expired. Expired records are dropped.</summary>
    Task<PackageDraftRecord?> TryGetAsync(
        PackageDraftKind kind,
        string id,
        TimeSpan ttl,
        CancellationToken cancellationToken);

    /// <summary>Live records of <paramref name="kind"/>, oldest write first. Expired records are omitted.</summary>
    Task<IReadOnlyList<PackageDraftLiveEntry>> ListLiveOldestFirstAsync(
        PackageDraftKind kind,
        TimeSpan ttl,
        CancellationToken cancellationToken);

    /// <summary>Removes one record. Missing identifiers are a no-op.</summary>
    Task RemoveAsync(PackageDraftKind kind, string id, CancellationToken cancellationToken);
}

/// <summary>
/// Process-local <see cref="IPackageDraftBacking"/>. Expiry is evaluated against
/// <see cref="TimeProvider"/> because this backing has no native TTL.
/// </summary>
internal sealed class MemoryPackageDraftBacking : IPackageDraftBacking
{
    private readonly ConcurrentDictionary<string, PackageDraftRecord> _maps = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PackageDraftRecord> _apps = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="MemoryPackageDraftBacking"/> class.</summary>
    /// <param name="timeProvider">Clock used to decide expiry.</param>
    public MemoryPackageDraftBacking(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public Task UpsertAsync(
        PackageDraftKind kind,
        string id,
        PackageDraftRecord record,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(record);
        Store(kind)[id] = record;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<PackageDraftRecord?> TryGetAsync(
        PackageDraftKind kind,
        string id,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var store = Store(kind);
        if (!store.TryGetValue(id, out var record))
        {
            return Task.FromResult<PackageDraftRecord?>(null);
        }

        if (IsExpired(record, ttl))
        {
            store.TryRemove(id, out _);
            return Task.FromResult<PackageDraftRecord?>(null);
        }

        return Task.FromResult<PackageDraftRecord?>(record);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PackageDraftLiveEntry>> ListLiveOldestFirstAsync(
        PackageDraftKind kind,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var store = Store(kind);
        var live = new List<PackageDraftLiveEntry>();

        // codeql[cs/linq/missed-where]: drops expired drafts while enumerating the concurrent store
        foreach (var pair in store)
        {
            if (IsExpired(pair.Value, ttl))
            {
                store.TryRemove(pair.Key, out _);
                continue;
            }

            live.Add(new PackageDraftLiveEntry(pair.Key, pair.Value.WrittenAt));
        }

        live.Sort(static (left, right) =>
        {
            var byTime = left.WrittenAt.CompareTo(right.WrittenAt);
            return byTime != 0 ? byTime : string.CompareOrdinal(left.Id, right.Id);
        });
        return Task.FromResult<IReadOnlyList<PackageDraftLiveEntry>>(live);
    }

    /// <inheritdoc />
    public Task RemoveAsync(PackageDraftKind kind, string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Store(kind).TryRemove(id, out _);
        return Task.CompletedTask;
    }

    private ConcurrentDictionary<string, PackageDraftRecord> Store(PackageDraftKind kind) => kind switch
    {
        PackageDraftKind.Map => _maps,
        PackageDraftKind.App => _apps,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private bool IsExpired(PackageDraftRecord record, TimeSpan ttl) =>
        _timeProvider.GetUtcNow() - record.WrittenAt >= ttl;
}
