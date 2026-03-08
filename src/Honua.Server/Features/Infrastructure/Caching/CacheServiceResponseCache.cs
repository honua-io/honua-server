// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Infrastructure.Caching;

namespace Honua.Server.Features.Infrastructure.Caching;

internal sealed class CacheServiceResponseCache : IResponseCache
{
    private const string KeyPrefix = "response:";
    private const string VersionPrefix = "response-version:";
    private static readonly TimeSpan VersionTtl = TimeSpan.FromDays(30);
    private static readonly Regex FeatureServerKeyPattern = new("^query:featureserver:service:(?<service>[^:]+):layer:(?<layer>\\d+):", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FeatureServerServicePattern = new("^query:featureserver:service:(?<service>[^:]+):layer:(?<layer>\\d+):\\*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FeatureServerLayerPattern = new("^query:featureserver:service:\\*:layer:(?<layer>\\d+):\\*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ODataKeyPattern = new("^query:odata:layer:(?<layer>\\d+):", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ODataPattern = new("^query:odata:layer:(?<layer>\\d+):\\*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OgcKeyPattern = new("^query:ogc:collection:(?<collection>[^:]+):", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OgcPattern = new("^query:ogc:collection:(?<collection>[^:]+):\\*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
        var namespaces = await ResolveNamespacesAsync(key, cancellationToken).ConfigureAwait(false);
        if (namespaces.Count == 0)
        {
            return NormalizeKey(key);
        }

        var versionSuffix = string.Join(':', namespaces.Values);
        return NormalizeKey($"{key}:v:{versionSuffix}");
    }

    private async Task<Dictionary<string, string>> ResolveNamespacesAsync(string key, CancellationToken cancellationToken)
    {
        var namespaces = GetNamespaceKeysForCacheKey(key);
        var resolved = new Dictionary<string, string>(namespaces.Count, StringComparer.Ordinal);

        foreach (var ns in namespaces)
        {
            var version = await _cacheService.GetAsync<string>(VersionPrefix + ns, cancellationToken).ConfigureAwait(false);
            resolved[ns] = string.IsNullOrWhiteSpace(version) ? "0" : version;
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
        }

        return true;
    }

    private static List<string> GetNamespaceKeysForCacheKey(string key)
    {
        var namespaces = new List<string>(2);

        var featureServerMatch = FeatureServerKeyPattern.Match(key);
        if (featureServerMatch.Success)
        {
            var service = featureServerMatch.Groups["service"].Value;
            var layer = featureServerMatch.Groups["layer"].Value;
            namespaces.Add($"featureserver:layer:{layer}");
            namespaces.Add($"featureserver:service:{service}:layer:{layer}");
            return namespaces;
        }

        var odataMatch = ODataKeyPattern.Match(key);
        if (odataMatch.Success)
        {
            namespaces.Add($"odata:layer:{odataMatch.Groups["layer"].Value}");
            return namespaces;
        }

        var ogcMatch = OgcKeyPattern.Match(key);
        if (ogcMatch.Success)
        {
            namespaces.Add($"ogc:collection:{ogcMatch.Groups["collection"].Value}");
        }

        return namespaces;
    }

    private static string[] GetNamespaceKeysForPattern(string pattern)
    {
        var featureServerLayerMatch = FeatureServerLayerPattern.Match(pattern);
        if (featureServerLayerMatch.Success)
        {
            return [$"featureserver:layer:{featureServerLayerMatch.Groups["layer"].Value}"];
        }

        var featureServerServiceMatch = FeatureServerServicePattern.Match(pattern);
        if (featureServerServiceMatch.Success)
        {
            return [$"featureserver:service:{featureServerServiceMatch.Groups["service"].Value}:layer:{featureServerServiceMatch.Groups["layer"].Value}"];
        }

        var odataMatch = ODataPattern.Match(pattern);
        if (odataMatch.Success)
        {
            return [$"odata:layer:{odataMatch.Groups["layer"].Value}"];
        }

        var ogcMatch = OgcPattern.Match(pattern);
        if (ogcMatch.Success)
        {
            return [$"ogc:collection:{ogcMatch.Groups["collection"].Value}"];
        }

        return Array.Empty<string>();
    }

    private static string NormalizeKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Cache key must be non-empty.", nameof(key));
        }

        return $"{KeyPrefix}{key}";
    }
}
