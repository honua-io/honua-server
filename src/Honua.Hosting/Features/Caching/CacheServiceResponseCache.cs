// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Infrastructure.Caching;

namespace Honua.Infrastructure.Caching;

internal sealed class CacheServiceResponseCache : IResponseCache
{
    private const string KeyPrefix = "response:";
    private const string VersionPrefix = "response-version:";
    private static readonly TimeSpan VersionTtl = TimeSpan.FromDays(30);

    // Short-lived in-process cache for namespace version strings (PA-052).
    // Reduces per-cache-operation Redis reads from O(N namespaces) sequential
    // calls to zero for warm paths. A 30-second TTL is well within VersionTtl
    // (30 days) and ensures invalidations from this node propagate immediately
    // (eviction in TryInvalidateNamespaceAsync). Invalidations from remote nodes
    // are visible within 30 s, which is safe because a stale version causes a
    // cache miss (DB fetch) rather than stale data.
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

    public CacheServiceResponseCache(ICacheService cacheService)
    {
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
        => await _cacheService.GetAsync<T>(await BuildStorageKeyAsync(key, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    public async Task SetAsync<T>(string key, T value, TimeSpan expiration, CancellationToken cancellationToken = default) where T : class
        => await _cacheService.SetAsync(await BuildStorageKeyAsync(key, cancellationToken).ConfigureAwait(false), value, expiration, cancellationToken).ConfigureAwait(false);

    public async Task<T> GetOrCreateAsync<T>(
        string key,
        Func<Task<T>> factory,
        TimeSpan expiration,
        CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);

        var storageKey = await BuildStorageKeyAsync(key, cancellationToken).ConfigureAwait(false);
        var value = await _cacheService.GetOrSetAsync(
            storageKey,
            async ct => await factory().WaitAsync(ct).ConfigureAwait(false),
            expiration,
            cancellationToken).ConfigureAwait(false);

        return value ?? throw new InvalidOperationException("Response cache factory returned null.");
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        => await _cacheService.RemoveAsync(await BuildStorageKeyAsync(key, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    public async Task RemoveByPatternAsync(string pattern, CancellationToken cancellationToken = default)
    {
        if (await TryInvalidateNamespaceAsync(pattern, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await _cacheService.RemoveByPatternAsync(NormalizeKey(pattern), cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> BuildStorageKeyAsync(string key, CancellationToken cancellationToken)
    {
        var namespaceVersions = await ResolveNamespaceVersionsAsync(key, cancellationToken).ConfigureAwait(false);
        if (namespaceVersions.Count == 0)
        {
            return NormalizeKey(key);
        }

        var versionSuffix = string.Join(':', namespaceVersions);
        return NormalizeKey($"{key}:v:{versionSuffix}");
    }

    private async Task<IReadOnlyList<string>> ResolveNamespaceVersionsAsync(string key, CancellationToken cancellationToken)
    {
        var namespaces = GetNamespaceKeysForCacheKey(key);
        if (namespaces.Count == 0)
        {
            return [];
        }

        var now = DateTimeOffset.UtcNow;
        var resolved = new string[namespaces.Count];
        List<int>? missIndices = null;

        // Local-cache pass: O(N) dictionary lookups, zero Redis round-trips for warm namespaces.
        for (var i = 0; i < namespaces.Count; i++)
        {
            if (_localVersionCache.TryGetValue(namespaces[i], out var entry) && entry.Expiry > now)
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
            // Batch-fetch all missing namespace versions concurrently so that N misses
            // result in a single Redis pipeline round-trip rather than N sequential ones.
            var fetchTasks = new Task<string?>[missIndices.Count];
            for (var j = 0; j < missIndices.Count; j++)
            {
                fetchTasks[j] = _cacheService.GetAsync<string>(VersionPrefix + namespaces[missIndices[j]], cancellationToken);
            }

            var versions = await Task.WhenAll(fetchTasks).ConfigureAwait(false);

            for (var j = 0; j < missIndices.Count; j++)
            {
                var i = missIndices[j];
                var version = string.IsNullOrWhiteSpace(versions[j]) ? "0" : versions[j]!;
                resolved[i] = version;
                _localVersionCache[namespaces[i]] = (version, now.Add(VersionLocalCacheTtl));
            }
        }

        return resolved;
    }

    private async Task<bool> TryInvalidateNamespaceAsync(string pattern, CancellationToken cancellationToken)
    {
        var namespaces = GetNamespaceKeysForPattern(pattern);
        if (namespaces.Length == 0)
        {
            return false;
        }

        foreach (var ns in namespaces)
        {
            await _cacheService.SetAsync(
                    VersionPrefix + ns,
                    Guid.NewGuid().ToString("N"),
                    VersionTtl,
                    cancellationToken)
                .ConfigureAwait(false);

            // Evict from the local in-process cache so this node sees the new version
            // on the very next cache operation, without waiting for the 30 s local TTL.
            _localVersionCache.TryRemove(ns, out _);
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

    private static string NormalizeKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Cache key must be non-empty.", nameof(key));
        }

        return key.StartsWith(KeyPrefix, StringComparison.Ordinal) ? key : $"{KeyPrefix}{key}";
    }
}
