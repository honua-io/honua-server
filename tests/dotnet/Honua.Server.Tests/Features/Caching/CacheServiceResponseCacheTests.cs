// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Caching;

[Protocol(Protocols.TestQuality)]
public sealed class CacheServiceResponseCacheTests
{
    [Theory]
    [MemberData(nameof(GetNamespaceCases))]
    public async Task SetAsync_And_RemoveByPatternAsync_UseResponseNamespaceVersions(CacheNamespaceCase testCase)
    {
        var setCache = new RecordingCacheService(testCase.VersionValues);
        var responseCache = new CacheServiceResponseCache(setCache);

        await responseCache.SetAsync(testCase.CacheKey, "payload", TimeSpan.FromMinutes(1));

        setCache.VersionLookups.Should().Equal(testCase.ExpectedVersionLookups);
        setCache.ValueWrites.Should().ContainSingle().Which.Should().Be(testCase.ExpectedStorageKey);
        setCache.ValueWrites.Single().Should().NotStartWith("response:response:");

        var invalidationCache = new RecordingCacheService();
        var invalidationResponseCache = new CacheServiceResponseCache(invalidationCache);

        await invalidationResponseCache.RemoveByPatternAsync(testCase.Pattern);

        invalidationCache.VersionWrites.Should().Equal(testCase.ExpectedVersionWrites);
        invalidationCache.PatternRemovals.Should().BeEmpty();
    }

    public static IEnumerable<object[]> GetNamespaceCases()
    {
        yield return [
            new CacheNamespaceCase(
                Name: "feature-server",
                CacheKey: "response:query:featureserver:service:alpha:layer:7:abc123",
                Pattern: "response:query:featureserver:service:alpha:layer:7:*",
                VersionValues: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["response-version:query:featureserver"] = "g",
                    ["response-version:query:featureserver:layer:7"] = "l7",
                    ["response-version:query:featureserver:service:alpha"] = "sa",
                    ["response-version:query:featureserver:service:alpha:layer:7"] = "sal7"
                },
                ExpectedStorageKey: "response:query:featureserver:service:alpha:layer:7:abc123:v:g:l7:sa:sal7",
                ExpectedVersionLookups: [
                    "response-version:query:featureserver",
                    "response-version:query:featureserver:layer:7",
                    "response-version:query:featureserver:service:alpha",
                    "response-version:query:featureserver:service:alpha:layer:7"
                ],
                ExpectedVersionWrites: [
                    "response-version:query:featureserver:service:alpha:layer:7"
                ])];

        yield return [
            new CacheNamespaceCase(
                Name: "ogc",
                CacheKey: "response:query:ogc:collection:roads:abc123",
                Pattern: "response:query:ogc:collection:roads:*",
                VersionValues: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["response-version:query:ogc"] = "g",
                    ["response-version:query:ogc:collection:roads"] = "c1"
                },
                ExpectedStorageKey: "response:query:ogc:collection:roads:abc123:v:g:c1",
                ExpectedVersionLookups: [
                    "response-version:query:ogc",
                    "response-version:query:ogc:collection:roads"
                ],
                ExpectedVersionWrites: [
                    "response-version:query:ogc:collection:roads"
                ])];

        yield return [
            new CacheNamespaceCase(
                Name: "odata",
                CacheKey: "response:query:odata:layer:42:abc123",
                Pattern: "response:query:odata:layer:42:*",
                VersionValues: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["response-version:query:odata"] = "g",
                    ["response-version:query:odata:layer:42"] = "l42"
                },
                ExpectedStorageKey: "response:query:odata:layer:42:abc123:v:g:l42",
                ExpectedVersionLookups: [
                    "response-version:query:odata",
                    "response-version:query:odata:layer:42"
                ],
                ExpectedVersionWrites: [
                    "response-version:query:odata:layer:42"
                ])];

        yield return [
            new CacheNamespaceCase(
                Name: "static-map",
                CacheKey: "response:render:staticmap:service:alpha:abc123",
                Pattern: "response:render:staticmap:service:alpha:*",
                VersionValues: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["response-version:render:staticmap"] = "g",
                    ["response-version:render:staticmap:service:alpha"] = "s1"
                },
                ExpectedStorageKey: "response:render:staticmap:service:alpha:abc123:v:g:s1",
                ExpectedVersionLookups: [
                    "response-version:render:staticmap",
                    "response-version:render:staticmap:service:alpha"
                ],
                ExpectedVersionWrites: [
                    "response-version:render:staticmap:service:alpha"
                ])];
    }

    public sealed record CacheNamespaceCase(
        string Name,
        string CacheKey,
        string Pattern,
        IReadOnlyDictionary<string, string> VersionValues,
        string ExpectedStorageKey,
        string[] ExpectedVersionLookups,
        string[] ExpectedVersionWrites);

    private sealed class RecordingCacheService : ICacheService
    {
        private readonly Dictionary<string, string> _versionValues;

        public RecordingCacheService(IReadOnlyDictionary<string, string>? versionValues = null)
        {
            _versionValues = versionValues is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(versionValues, StringComparer.Ordinal);
        }

        public List<string> VersionLookups { get; } = [];
        public List<string> VersionWrites { get; } = [];
        public List<string> ValueWrites { get; } = [];
        public List<string> RemovedKeys { get; } = [];
        public List<string> PatternRemovals { get; } = [];

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
        {
            if (_versionValues.TryGetValue(key, out var version))
            {
                VersionLookups.Add(key);
                if (typeof(T) == typeof(string))
                {
                    return Task.FromResult<T?>((T?)(object?)version);
                }
            }

            return Task.FromResult<T?>(null);
        }

        public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default) where T : class
            => SetAsync(key, value, TimeSpan.FromMinutes(1), cancellationToken);

        public Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken = default) where T : class
        {
            if (key.StartsWith("response-version:", StringComparison.Ordinal))
            {
                VersionWrites.Add(key);
                _versionValues[key] = value is string stringValue ? stringValue : value?.ToString() ?? string.Empty;
            }
            else
            {
                ValueWrites.Add(key);
            }

            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            RemovedKeys.Add(key);
            return Task.CompletedTask;
        }

        public Task RemoveByPatternAsync(string pattern, CancellationToken cancellationToken = default)
        {
            PatternRemovals.Add(pattern);
            return Task.CompletedTask;
        }

        public Task<T?> GetOrSetAsync<T>(string key, Func<CancellationToken, Task<T?>> factory, CancellationToken cancellationToken = default) where T : class
            => GetOrSetAsync(key, factory, TimeSpan.FromMinutes(1), cancellationToken);

        public async Task<T?> GetOrSetAsync<T>(
            string key,
            Func<CancellationToken, Task<T?>> factory,
            TimeSpan ttl,
            CancellationToken cancellationToken = default) where T : class
        {
            var value = await GetAsync<T>(key, cancellationToken).ConfigureAwait(false);
            if (value != null)
            {
                return value;
            }

            value = await factory(cancellationToken).ConfigureAwait(false);
            if (value != null)
            {
                await SetAsync(key, value, ttl, cancellationToken).ConfigureAwait(false);
            }

            return value;
        }

        public Task<CacheEntryMetadata<T>> GetWithMetadataAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
            => Task.FromResult(CacheEntryMetadata<T>.Miss());
    }
}
