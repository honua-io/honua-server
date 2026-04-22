// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.FeatureStore.Internal;
using Honua.Postgres.Features.FeatureStore.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Postgres.Tests.Features.FeatureStore;

public sealed class FeatureCacheManagerTests
{
    [Fact]
    public void InvalidateLayerCache_RemovesLayerAndSchemaScopedEntries()
    {
        var layerSridCache = GetField<ConcurrentDictionary<(string Identity, int LayerId), LayerSridCacheEntry>>("_layerSridCache");
        var geometryStorageCache = GetField<ConcurrentDictionary<string, int>>("_geometryStorageTypeCache");
        var layerCatalogCache = GetField<ConcurrentDictionary<string, int>>("_hasLayerCatalogCache");

        layerSridCache.Clear();
        geometryStorageCache.Clear();
        layerCatalogCache.Clear();

        try
        {
            var manager = new FeatureCacheManager(
                new StubDatabaseConnectionProvider(),
                NullLogger<FeatureCacheManager>.Instance,
                "tenant_a");

            layerSridCache[("tenant_a", 12)] = new LayerSridCacheEntry(4326, DateTimeOffset.UtcNow);
            geometryStorageCache["tenant_a"] = 1;
            layerCatalogCache["tenant_a"] = 1;

            manager.InvalidateLayerCache(12);

            layerSridCache.ContainsKey(("tenant_a", 12)).Should().BeFalse();
            geometryStorageCache.ContainsKey("tenant_a").Should().BeFalse();
            layerCatalogCache.ContainsKey("tenant_a").Should().BeFalse();
        }
        finally
        {
            layerSridCache.Clear();
            geometryStorageCache.Clear();
            layerCatalogCache.Clear();
        }
    }

    [Fact]
    public void CleanupExpiredCacheEntries_UsesBoundedExpiryScan()
    {
        var layerSridCache = GetField<ConcurrentDictionary<(string Identity, int LayerId), LayerSridCacheEntry>>("_layerSridCache");
        var expiryScanLimit = GetIntField("MaxLayerSridExpiryScansPerCleanup");

        layerSridCache.Clear();

        try
        {
            var manager = new FeatureCacheManager(
                new StubDatabaseConnectionProvider(),
                NullLogger<FeatureCacheManager>.Instance,
                "tenant_a");

            for (var layerId = 1; layerId <= expiryScanLimit + 25; layerId++)
            {
                layerSridCache[("tenant_a", layerId)] = new LayerSridCacheEntry(4326, DateTimeOffset.UtcNow.AddDays(-2));
            }

            manager.CleanupExpiredCacheEntries();

            layerSridCache.Count.Should().Be(25);
        }
        finally
        {
            layerSridCache.Clear();
        }
    }

    [Fact]
    public void CleanupExpiredCacheEntries_UsesBoundedOverflowTrim()
    {
        var layerSridCache = GetField<ConcurrentDictionary<(string Identity, int LayerId), LayerSridCacheEntry>>("_layerSridCache");
        var maxEntries = GetIntField("MaxLayerSridCacheEntries");
        var overflowTrimLimit = GetIntField("MaxLayerSridOverflowRemovalsPerCleanup");

        layerSridCache.Clear();

        try
        {
            var manager = new FeatureCacheManager(
                new StubDatabaseConnectionProvider(),
                NullLogger<FeatureCacheManager>.Instance,
                "tenant_a");

            for (var layerId = 1; layerId <= maxEntries + overflowTrimLimit + 10; layerId++)
            {
                layerSridCache[("tenant_a", layerId)] = new LayerSridCacheEntry(4326, DateTimeOffset.UtcNow);
            }

            manager.CleanupExpiredCacheEntries();

            layerSridCache.Count.Should().Be(maxEntries + 10);
        }
        finally
        {
            layerSridCache.Clear();
        }
    }

    private static T GetField<T>(string fieldName)
        where T : class
    {
        var field = typeof(FeatureCacheManager).GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        field.Should().NotBeNull();
        var value = field!.GetValue(null) as T;
        value.Should().NotBeNull();
        return value!;
    }

    private static int GetIntField(string fieldName)
    {
        var field = typeof(FeatureCacheManager).GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        field.Should().NotBeNull();
        return (int)field!.GetRawConstantValue()!;
    }

    private sealed class StubDatabaseConnectionProvider : IDatabaseConnectionProvider
    {
        public string GetConnectionString()
            => "Host=localhost;Database=test;";

        public Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(
            Func<Task<T>> operation,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ExecuteWithDeadlockRetryAsync(
            Func<Task> operation,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
