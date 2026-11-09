// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Infrastructure.Caching;

namespace Honua.Infrastructure.Caching;

internal sealed class CacheServiceResponseCache : IResponseCache
{
    private const int ReservationOverheadBytes = 128;
    private const string KeyPrefix = "response:";
    private const string BoundKeyPrefix = "response-bound:";

    // Standalone writes retain the local-version optimization. Read/compute/fill
    // callers bind their key before querying, so other requests cannot change the
    // generation of an in-flight fill. Unbound reads always fetch shared versions.
    private static readonly TimeSpan VersionLocalCacheTtl = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, (string Version, DateTimeOffset Expiry)> _localVersionCache
        = new(StringComparer.Ordinal);
    private static readonly Regex FeatureServerKeyPattern = new("^(?:response:)?query:featureserver:service:(?<service>[^:]+):layer:(?<layer>\\d+):", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FeatureServerLayerPattern = new("^(?:response:)?query:featureserver:service:\\*:layer:(?<layer>\\d+):\\*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FeatureServerServiceLayerPattern = new("^(?:response:)?query:featureserver:service:(?<service>[^:]+):layer:(?<layer>\\d+):\\*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FeatureServerServicePattern = new("^(?:response:)?query:featureserver:service:(?<service>[^:]+):\\*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FeatureServerPattern = new("^(?:response:)?query:featureserver:service:\\*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ODataKeyPattern = new("^(?:response:)?query:odata:layer:(?<layer>\\d+):", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ODataLayerPattern = new("^(?:response:)?query:odata:layer:(?<layer>\\d+):\\*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ODataPattern = new("^(?:response:)?query:odata:layer:\\*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OgcKeyPattern = new("^(?:response:)?query:ogc:collection:(?<collection>[^:]+):", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OgcCollectionPattern = new("^(?:response:)?query:ogc:collection:(?<collection>[^:]+):\\*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OgcPattern = new("^(?:response:)?query:ogc:collection:\\*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ICacheService _cacheService;
    private readonly int _maxEntryBytes;
    private readonly int _maxBytes;
    private readonly int _maxEntries;
    private long _reservedBytes;
    private long _reservationSequence;
    private string? _observedRecoveryEpoch;
    private readonly SortedSet<ResponseReservation> _expirationOrder = new(Comparer<ResponseReservation>.Create((left, right) =>
    {
        var expiration = left.ExpiresAt.CompareTo(right.ExpiresAt);
        return expiration != 0 ? expiration : StringComparer.Ordinal.Compare(left.Key, right.Key);
    }));
    private readonly object _budgetLock = new();
    private readonly Dictionary<string, ResponseReservation> _reservations = new(StringComparer.Ordinal);

    public CacheServiceResponseCache(ICacheService cacheService, CacheOptions? options = null)
    {
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        options ??= new CacheOptions();
        _maxEntryBytes = options.ResponseCacheMaxEntryBytes;
        _maxBytes = options.ResponseCacheMaxBytes;
        _maxEntries = options.ResponseCacheMaxEntries;
    }

    public async Task<string> BindKeyAsync(string key, CancellationToken cancellationToken = default)
        => BoundKeyPrefix + await BuildStorageKeyAsync(key, allowLocalVersionCache: false, cancellationToken).ConfigureAwait(false);

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
        => await _cacheService.GetAsync<T>(await BuildStorageKeyAsync(key, allowLocalVersionCache: false, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    public async Task SetAsync<T>(string key, T value, TimeSpan expiration, CancellationToken cancellationToken = default) where T : class
    {
        var cached = value as CachedResponse;
        if (cached is not null && cached.Payload.Length > _maxEntryBytes)
        {
            return;
        }
        var storageKey = await BuildStorageKeyAsync(key, allowLocalVersionCache: true, cancellationToken).ConfigureAwait(false);
        var reservation = cached is null ? null : TryReserve(storageKey, cached.Payload.Length, expiration);
        if (cached is not null && reservation is null)
        {
            return;
        }

        try
        {
            await _cacheService.SetAsync(storageKey, value, expiration, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (reservation is not null)
            {
                ReleaseReservation(storageKey, reservation);
            }
            throw;
        }
    }

    public async Task<T> GetOrCreateAsync<T>(
        string key,
        Func<Task<T>> factory,
        TimeSpan expiration,
        CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);

        var storageKey = await BuildStorageKeyAsync(key, allowLocalVersionCache: false, cancellationToken).ConfigureAwait(false);
        var value = await _cacheService.GetOrSetAsync(
            storageKey,
            async ct => await factory().WaitAsync(ct).ConfigureAwait(false),
            expiration,
            cancellationToken).ConfigureAwait(false);

        return value ?? throw new InvalidOperationException("Response cache factory returned null.");
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        var storageKey = await BuildStorageKeyAsync(key, allowLocalVersionCache: false, cancellationToken).ConfigureAwait(false);
        ReleaseReservation(storageKey);
        await _cacheService.RemoveAsync(storageKey, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveByPatternAsync(string pattern, CancellationToken cancellationToken = default)
    {
        if (await TryInvalidateNamespaceAsync(pattern, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await _cacheService.RemoveByPatternAsync(NormalizeKey(pattern), cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> BuildStorageKeyAsync(string key, bool allowLocalVersionCache, CancellationToken cancellationToken)
    {
        if (key.StartsWith(BoundKeyPrefix, StringComparison.Ordinal))
        {
            // A bound key belongs to one read/fill operation. Never re-resolve it
            // using a newer namespace after the query has observed its data.
            return NormalizeKey(key[BoundKeyPrefix.Length..]);
        }

        var namespaceVersions = await ResolveNamespaceVersionsAsync(key, allowLocalVersionCache, cancellationToken).ConfigureAwait(false);
        if (namespaceVersions.Count == 0)
        {
            return NormalizeKey(key);
        }

        var versionSuffix = string.Join(':', namespaceVersions);
        return NormalizeKey($"{key}:v:{versionSuffix}");
    }

    private async Task<IReadOnlyList<string>> ResolveNamespaceVersionsAsync(string key, bool allowLocalVersionCache, CancellationToken cancellationToken)
    {
        var namespaces = GetNamespaceKeysForCacheKey(key);
        if (namespaces.Count == 0)
        {
            return [];
        }
        namespaces.Insert(0, ResponseCacheVersionKeys.RecoveryNamespace);

        var now = DateTimeOffset.UtcNow;
        var resolved = new string[namespaces.Count];
        List<int>? missIndices = null;

        // Only SetAsync may reuse local versions. Every read checks shared invalidations.
        for (var i = 0; i < namespaces.Count; i++)
        {
            if (allowLocalVersionCache && _localVersionCache.TryGetValue(namespaces[i], out var entry) && entry.Expiry > now)
            {
                resolved[i] = entry.Version;
            }
            else
            {
                (missIndices ??= new List<int>(namespaces.Count - i)).Add(i);
            }
        }

        if (missIndices is { Count: > 0 })
        {
            var reservationsBeforeRead = Volatile.Read(ref _reservationSequence);
            // Batch-fetch all missing namespace versions concurrently so that N misses
            // result in a single Redis pipeline round-trip rather than N sequential ones.
            var fetchTasks = new Task<string?>[missIndices.Count];
            for (var j = 0; j < missIndices.Count; j++)
            {
                fetchTasks[j] = _cacheService.GetAsync<string>(ResponseCacheVersionKeys.Prefix + namespaces[missIndices[j]], cancellationToken);
            }

            var versions = await Task.WhenAll(fetchTasks).ConfigureAwait(false);

            for (var j = 0; j < missIndices.Count; j++)
            {
                var i = missIndices[j];
                var version = string.IsNullOrWhiteSpace(versions[j]) ? "0" : versions[j]!;
                resolved[i] = version;
                _localVersionCache[namespaces[i]] = (version, now.Add(VersionLocalCacheTtl));
            }
            if (missIndices[0] == 0)
            {
                ReleaseObsoleteEpochReservations(resolved[0], reservationsBeforeRead);
            }
        }

        return resolved;
    }

    private void ReleaseObsoleteEpochReservations(string epoch, long reservationsBeforeRead)
    {
        lock (_budgetLock)
        {
            if (_observedRecoveryEpoch == epoch)
            {
                return;
            }
            _observedRecoveryEpoch = epoch;
            var epochMarker = $":v:{epoch}:";
            var invalidated = _reservations.Values.Where(reservation =>
                reservation.Sequence <= reservationsBeforeRead &&
                GetNamespaceKeysForCacheKey(reservation.Key).Count > 0 &&
                !reservation.Key.Contains(epochMarker, StringComparison.Ordinal)).ToArray();
            foreach (var reservation in invalidated)
            {
                RemoveReservation(reservation);
            }
        }
    }

    private async Task<bool> TryInvalidateNamespaceAsync(string pattern, CancellationToken cancellationToken)
    {
        var namespaces = GetNamespaceKeysForPattern(pattern);
        if (namespaces.Length == 0)
        {
            return false;
        }

        var reservationsBeforeInvalidation = Volatile.Read(ref _reservationSequence);
        // Each namespace token bumps an independent version key, so the writes have
        // no ordering dependency on one another; issue them concurrently instead of
        // one round trip at a time.
        await Task.WhenAll(namespaces.Select(ns => _cacheService.SetAsync(
                ResponseCacheVersionKeys.Prefix + ns,
                Guid.NewGuid().ToString("N"),
                ResponseCacheVersionKeys.Lifetime,
                cancellationToken)))
            .ConfigureAwait(false);

        // Evict from the local in-process cache so this node sees the new version
        // on the very next write too, without waiting for the 30 s local TTL.
        foreach (var ns in namespaces)
        {
            _localVersionCache.TryRemove(ns, out _);
        }

        lock (_budgetLock)
        {
            var invalidated = _reservations.Values.Where(reservation =>
                reservation.Sequence <= reservationsBeforeInvalidation &&
                GetNamespaceKeysForCacheKey(reservation.Key).Any(namespaces.Contains)).ToArray();
            foreach (var reservation in invalidated)
            {
                RemoveReservation(reservation);
            }
        }
        return true;
    }

    private static List<string> GetNamespaceKeysForCacheKey(string key)
    {
        var namespaces = new List<string>(4);

        var featureServerGlobalMatch = FeatureServerPattern.Match(key);
        if (featureServerGlobalMatch.Success)
        {
            namespaces.Add("query:featureserver");
            return namespaces;
        }

        var featureServerMatch = FeatureServerKeyPattern.Match(key);
        if (featureServerMatch.Success)
        {
            var service = featureServerMatch.Groups["service"].Value;
            var layer = featureServerMatch.Groups["layer"].Value;
            namespaces.Add("query:featureserver");
            namespaces.Add($"query:featureserver:layer:{layer}");
            namespaces.Add($"query:featureserver:service:{service}");
            namespaces.Add($"query:featureserver:service:{service}:layer:{layer}");
            return namespaces;
        }

        var odataMatch = ODataKeyPattern.Match(key);
        if (odataMatch.Success)
        {
            namespaces.Add("query:odata");
            namespaces.Add($"query:odata:layer:{odataMatch.Groups["layer"].Value}");
            return namespaces;
        }

        var ogcMatch = OgcKeyPattern.Match(key);
        if (ogcMatch.Success)
        {
            namespaces.Add("query:ogc");
            namespaces.Add($"query:ogc:collection:{ogcMatch.Groups["collection"].Value}");
            return namespaces;
        }

        return namespaces;
    }

    private static string[] GetNamespaceKeysForPattern(string pattern)
    {
        var featureServerGlobalMatch = FeatureServerPattern.Match(pattern);
        if (featureServerGlobalMatch.Success)
        {
            return ["query:featureserver"];
        }

        var featureServerLayerMatch = FeatureServerLayerPattern.Match(pattern);
        if (featureServerLayerMatch.Success)
        {
            return [$"query:featureserver:layer:{featureServerLayerMatch.Groups["layer"].Value}"];
        }

        var featureServerServiceLayerMatch = FeatureServerServiceLayerPattern.Match(pattern);
        if (featureServerServiceLayerMatch.Success)
        {
            return [$"query:featureserver:service:{featureServerServiceLayerMatch.Groups["service"].Value}:layer:{featureServerServiceLayerMatch.Groups["layer"].Value}"];
        }

        var featureServerServiceMatch = FeatureServerServicePattern.Match(pattern);
        if (featureServerServiceMatch.Success)
        {
            return [$"query:featureserver:service:{featureServerServiceMatch.Groups["service"].Value}"];
        }

        var odataLayerMatch = ODataLayerPattern.Match(pattern);
        if (odataLayerMatch.Success)
        {
            return [$"query:odata:layer:{odataLayerMatch.Groups["layer"].Value}"];
        }

        var odataMatch = ODataPattern.Match(pattern);
        if (odataMatch.Success)
        {
            return ["query:odata"];
        }

        var ogcCollectionMatch = OgcCollectionPattern.Match(pattern);
        if (ogcCollectionMatch.Success)
        {
            return [$"query:ogc:collection:{ogcCollectionMatch.Groups["collection"].Value}"];
        }

        var ogcMatch = OgcPattern.Match(pattern);
        if (ogcMatch.Success)
        {
            return ["query:ogc"];
        }

        return Array.Empty<string>();
    }

    private ResponseReservation? TryReserve(string key, int payloadBytes, TimeSpan expiration)
    {
        var now = DateTimeOffset.UtcNow;
        var bytes = payloadBytes + (long)key.Length * sizeof(char) + ReservationOverheadBytes;
        lock (_budgetLock)
        {
            PruneExpiredReservations(now);
            _reservations.TryGetValue(key, out var current);
            var retainedBytes = _reservedBytes - (current?.Bytes ?? 0);
            if ((current is null && _reservations.Count >= _maxEntries) || bytes > _maxBytes - retainedBytes)
            {
                return null;
            }
            if (current is not null)
            {
                RemoveReservation(current);
            }
            var reservation = new ResponseReservation(key, bytes, now.Add(expiration), Interlocked.Increment(ref _reservationSequence));
            _reservations[key] = reservation;
            _expirationOrder.Add(reservation);
            _reservedBytes += bytes;
            return reservation;
        }
    }

    private void ReleaseReservation(string key, ResponseReservation? expected = null)
    {
        lock (_budgetLock)
        {
            if (_reservations.TryGetValue(key, out var current) && (expected is null || ReferenceEquals(current, expected)))
            {
                RemoveReservation(current);
            }
        }
    }

    private void PruneExpiredReservations(DateTimeOffset now)
    {
        while (_expirationOrder.Min is { } expired && expired.ExpiresAt <= now)
        {
            RemoveReservation(expired);
        }
    }

    private void RemoveReservation(ResponseReservation reservation)
    {
        _reservations.Remove(reservation.Key);
        _expirationOrder.Remove(reservation);
        _reservedBytes -= reservation.Bytes;
    }

    private sealed record ResponseReservation(string Key, long Bytes, DateTimeOffset ExpiresAt, long Sequence);

    private static string NormalizeKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Cache key must be non-empty.", nameof(key));
        }

        return key.StartsWith(KeyPrefix, StringComparison.Ordinal) ? key : $"{KeyPrefix}{key}";
    }
}
