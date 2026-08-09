// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Diagnostics;
using FluentAssertions;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Caching;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Postgres.Features.Metadata;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit.Abstractions;

namespace Honua.Postgres.Tests.Features.Metadata;

/// <summary>
/// Integration tests for the Metadata v2 hot-path snapshot cache (MCP A2). Proves, against the real
/// Postgres store, that the caching provider decorator serves repeated catalog reads from one
/// materialized snapshot, that a catalog write through the store invalidates that cache so read
/// surfaces observe the mutation immediately, and measures the per-call wall-clock delta the cache
/// removes from the hot path.
/// </summary>
[Collection("Database")]
public sealed class PostgresMetadataV2GraphCacheTests(PostgresFixture fixture, ITestOutputHelper output)
{
    private const string Environment = "Test";

    [IntegrationTest]
    public async Task CachedProvider_RepeatedReads_ServeOneSnapshot_AndSaveInvalidates()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresMetadataV2GraphCacheTests));
        try
        {
            var connectionProvider = new TestConnectionProvider(fixture.DataSource, schema);
            var cache = new MetadataV2GraphSnapshotCache(
                Options.Create(new CacheOptions { MetadataGraphCacheEnabled = true, MetadataGraphTtlSeconds = 3600 }));

            // Read surface: the store wired to invalidate the shared cache on write.
            var store = new PostgresMetadataV2GraphStore(connectionProvider, Environment, schema, cache);
            var provider = new CachingMetadataV2GraphProvider(store, cache, Environment);

            // A second store WITHOUT the invalidator simulates a mutation from another node — it
            // updates the database but does not touch this node's in-process cache.
            var otherNodeStore = new PostgresMetadataV2GraphStore(connectionProvider, Environment, schema);

            var snap1 = await store.SaveAsync(BuildGraph(revision: 1, serviceCount: 3), expectedEtag: null);

            // First read materializes and caches the snapshot.
            (await provider.GetCurrentAsync()).Graph.Revision.Should().Be(1);

            // Out-of-band mutation (another node) advances the database to revision 2 but leaves this
            // node's cache untouched — the cached read must still return revision 1 within the TTL.
            var snap2 = await otherNodeStore.SaveAsync(BuildGraph(revision: 2, serviceCount: 3), expectedEtag: snap1.Etag);
            (await provider.GetCurrentAsync()).Graph.Revision.Should()
                .Be(1, "reads are served from the per-instance cache until the TTL expires or a local write invalidates");

            // A write through the invalidator-wired store (the canonical SaveAsync seam) drops the
            // cache so the very next read observes the fresh catalog immediately.
            await store.SaveAsync(BuildGraph(revision: 3, serviceCount: 3), expectedEtag: snap2.Etag);
            (await provider.GetCurrentAsync()).Graph.Revision.Should()
                .Be(3, "SaveAsync must invalidate the snapshot cache so read surfaces never serve a stale catalog after a local write");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task CachedProvider_IsFasterThanPerCallStoreReads()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresMetadataV2GraphCacheTests));
        try
        {
            var connectionProvider = new TestConnectionProvider(fixture.DataSource, schema);
            var seedStore = new PostgresMetadataV2GraphStore(connectionProvider, Environment, schema);
            await seedStore.SaveAsync(BuildGraph(revision: 1, serviceCount: 60), expectedEtag: null);

            const int iterations = 200;

            // Baseline: a fresh scoped store per call (what happens today — every request/tool call
            // resolves a new scoped store that re-reads the full catalog document + deserializes).
            var uncached = Stopwatch.StartNew();
            for (var i = 0; i < iterations; i++)
            {
                var perCallStore = new PostgresMetadataV2GraphStore(connectionProvider, Environment, schema);
                _ = await perCallStore.GetCurrentAsync();
            }

            uncached.Stop();

            // Cached: one materialization, then in-process object-reference hits.
            var cache = new MetadataV2GraphSnapshotCache(
                Options.Create(new CacheOptions { MetadataGraphCacheEnabled = true, MetadataGraphTtlSeconds = 3600 }));
            var provider = new CachingMetadataV2GraphProvider(
                new PostgresMetadataV2GraphStore(connectionProvider, Environment, schema, cache), cache, Environment);

            var cached = Stopwatch.StartNew();
            for (var i = 0; i < iterations; i++)
            {
                _ = await provider.GetCurrentAsync();
            }

            cached.Stop();

            var uncachedPerCall = uncached.Elapsed.TotalMilliseconds / iterations;
            var cachedPerCall = cached.Elapsed.TotalMilliseconds / iterations;
            output.WriteLine($"Metadata v2 GetCurrentAsync x{iterations} (60-service catalog):");
            output.WriteLine($"  uncached (per-call store read): {uncached.Elapsed.TotalMilliseconds:F1} ms total, {uncachedPerCall:F3} ms/call");
            output.WriteLine($"  cached   (decorator):           {cached.Elapsed.TotalMilliseconds:F1} ms total, {cachedPerCall:F3} ms/call");
            output.WriteLine($"  speedup: {uncached.Elapsed.TotalMilliseconds / Math.Max(cached.Elapsed.TotalMilliseconds, 0.001):F1}x");

            cached.Elapsed.Should().BeLessThan(uncached.Elapsed,
                "the in-process snapshot cache must remove the per-call catalog read+deserialize from the hot path");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private static MetadataV2Graph BuildGraph(long revision, int serviceCount)
    {
        var builder = new TestMetadataV2GraphBuilder()
            .WithEnvironment(Environment)
            .WithRevision(revision)
            .AddConnection("conn-1", "Primary");

        for (var i = 0; i < serviceCount; i++)
        {
            var resourceId = $"res-{i}";
            var bindingId = $"sb-{i}";
            var serviceId = $"svc-{i}";
            builder
                .AddResource(resourceId, $"Resource {i}")
                .AddStorageBinding(bindingId, resourceId, locator: $"public.layer_{i}", connectionId: "conn-1", storageLayerId: i)
                .AddService(serviceId, $"Service {i}", route: $"service-{i}", protocols: ["ogc-api-features"])
                .AddPublication($"pub-{i}", serviceId, resourceId, storageBindingId: bindingId, layerIndex: 0);
        }

        return builder.Build();
    }

    private sealed class TestConnectionProvider(NpgsqlDataSource dataSource, string schemaName) : IAdoNetDatabaseConnectionProvider
    {
        public string GetConnectionString() => dataSource.ConnectionString;

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            var conn = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SET search_path TO \"{schemaName}\", public;";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            return conn;
        }

        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
        {
            var conn = await OpenConnectionAsync(cancellationToken);
            try
            {
                var tx = await conn.BeginTransactionAsync(isolationLevel, cancellationToken);
                return (conn, tx);
            }
            catch
            {
                await conn.DisposeAsync();
                throw;
            }
        }

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(
            Func<Task<T>> operation,
            CancellationToken cancellationToken = default)
            => operation();

        public Task ExecuteWithDeadlockRetryAsync(
            Func<Task> operation,
            CancellationToken cancellationToken = default)
            => operation();
    }
}
