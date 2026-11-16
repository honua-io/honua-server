// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Studio.Drafts;

public enum PackageDraftKind
{
    Map,

    App,
}

public sealed record PackageDraftRecord(
    string OwnerId,
    DateTimeOffset WrittenAt,
    MapPackage? Map,
    AppPackage? App);

public readonly record struct PackageDraftLiveEntry(string Id, DateTimeOffset WrittenAt);

public sealed record PackageDraftWrite(PackageDraftKind Kind, string Id, PackageDraftRecord Record, TimeSpan Ttl);

public interface IPackageDraftBacking
{
    Task UpsertAsync(PackageDraftWrite write, CancellationToken cancellationToken);

    Task<PackageDraftRecord?> TryGetAsync(
        PackageDraftKind kind,
        string id,
        TimeSpan ttl,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PackageDraftLiveEntry>> ListLiveOldestFirstAsync(
        PackageDraftKind kind,
        TimeSpan ttl,
        CancellationToken cancellationToken);

    Task RemoveAsync(PackageDraftKind kind, string id, CancellationToken cancellationToken);
}

internal sealed class MemoryPackageDraftBacking : IPackageDraftBacking
{
    private readonly ConcurrentDictionary<string, PackageDraftRecord> _maps = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PackageDraftRecord> _apps = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    public MemoryPackageDraftBacking(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    public Task UpsertAsync(PackageDraftWrite write, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(write.Record);
        Store(write.Kind)[write.Id] = write.Record;
        return Task.CompletedTask;
    }

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
