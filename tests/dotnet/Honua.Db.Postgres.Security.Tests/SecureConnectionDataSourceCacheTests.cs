// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Db.Postgres.Features.Security;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace Honua.Db.Postgres.Security.Tests;

/// <summary>
/// Regression tests for <see cref="SecureConnectionDataSourceCache"/> to
/// ensure named secure connections honour the same data-source configuration
/// — most importantly the embedded <c>search_path</c> — as the default
/// <see cref="NpgsqlDataSource"/> built in <c>ServiceCollectionExtensions</c>.
/// </summary>
public sealed class SecureConnectionDataSourceCacheTests
{
    private const string SampleConnectionString =
        "Host=example.com;Port=5432;Database=honua_test;Username=app;Password=secret;SslMode=Disable";

    [SecurityTest]
    [Fact]
    public void GetOrCreate_WithConfiguredDefaultSchema_MatchesDefaultDataSourceSessionWiring()
    {
        // Arrange — configuration mirrors the production wiring in
        // ServiceCollectionExtensions.AddPostgreSqlServices which propagates
        // Database:Schema into the default NpgsqlDataSource. The secure cache
        // must do the same so background/service callers (ISchemaContext.CurrentSchema == null)
        // still resolve schema-qualified tables (honua-server#2949).
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Schema"] = "honua_tenant_a"
            })
            .Build();

        using var cache = new SecureConnectionDataSourceCache(configuration);

        // Act
        var dataSource = cache.GetOrCreate(SampleConnectionString);

        // Assert — the cache must produce the exact same connection-string wiring as
        // calling PostgresDataSourceFactory.Create directly with the same inputs, i.e.
        // the same schema plumbing as the default NpgsqlDataSource singleton. Note this
        // does NOT mean the search_path text appears in the connection string's Options:
        // for the default (non-multiplexing, non-schema-header) path, the factory applies
        // search_path via a physical-connection-initializer SET statement instead of the
        // libpq `options` startup parameter, because AWS RDS Proxy rejects that startup
        // parameter outright (0A000) — see PostgresDataSourceFactory.Configure and
        // honua-server#1638. Parity with the default data source is exactly the point:
        // the secure cache must stay RDS-Proxy-safe too, not diverge onto the parameter
        // the default path deliberately avoids.
        var connectionLimits = PostgresDataSourceFactory.ResolveConnectionLimits(configuration);
        using var expected = PostgresDataSourceFactory.Create(
            SampleConnectionString,
            schemaHeadersEnabled: false,
            connectionLimits,
            defaultSchema: "honua_tenant_a");

        Assert.Equal(expected.ConnectionString, dataSource.ConnectionString);

        var builder = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);
        Assert.True(string.IsNullOrEmpty(builder.Options));
    }

    [SecurityTest]
    [Fact]
    public void GetOrCreate_WithoutConfiguredDefaultSchema_DoesNotEmbedSearchPath()
    {
        // Arrange — when no default schema is configured, the factory omits
        // the search_path entry and relies on the database default.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        using var cache = new SecureConnectionDataSourceCache(configuration);

        // Act
        var dataSource = cache.GetOrCreate(SampleConnectionString);

        // Assert
        var builder = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);
        Assert.DoesNotContain("search_path=", builder.Options ?? string.Empty);
    }

    [SecurityTest]
    [Fact]
    public void GetOrCreate_SameConnectionString_ReturnsCachedInstance()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Schema"] = "honua"
            })
            .Build();

        using var cache = new SecureConnectionDataSourceCache(configuration);

        // Act
        var first = cache.GetOrCreate(SampleConnectionString);
        var second = cache.GetOrCreate(SampleConnectionString);

        // Assert — the cache must dedupe identical strings to avoid leaking
        // NpgsqlDataSource instances on every connection open.
        Assert.Same(first, second);
    }

    [SecurityTest]
    [Fact]
    public void GetOrCreate_WithConfiguredPoolLimits_PreservesSettings()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Limits:Connections:MaxConnectionPoolSize"] = "12",
                ["Limits:Connections:MinConnectionPoolSize"] = "2",
                ["Limits:Connections:ConnectionIdleLifetimeSeconds"] = "90",
                ["Limits:Connections:ConnectionPruningIntervalSeconds"] = "15"
            })
            .Build();
        using var cache = new SecureConnectionDataSourceCache(configuration);

        var settings = new NpgsqlConnectionStringBuilder(cache.GetOrCreate("source", SampleConnectionString).ConnectionString);

        Assert.Equal(12, settings.MaxPoolSize);
        Assert.Equal(2, settings.MinPoolSize);
        Assert.Equal(90, settings.ConnectionIdleLifetime);
        Assert.Equal(15, settings.ConnectionPruningInterval);
    }

    [SecurityTest]
    [Fact]
    public async Task GetOrCreate_RotatedConnection_DisposesOldPoolAndCachesReplacement()
    {
        using var cache = new SecureConnectionDataSourceCache(new ConfigurationBuilder().Build());
        var old = cache.GetOrCreate("source", SampleConnectionString);
        var rotatedConnectionString = SampleConnectionString.Replace("Password=secret", "Password=rotated", StringComparison.Ordinal);

        var replacement = cache.GetOrCreate("source", rotatedConnectionString);

        Assert.NotSame(old, replacement);
        Assert.Same(replacement, cache.GetOrCreate("source", rotatedConnectionString));
        await AssertDisposedAsync(old);

        cache.Dispose();
        await AssertDisposedAsync(replacement);
    }

    [SecurityTest]
    [Fact]
    public async Task GetOrCreate_FailedRotation_PreservesExistingPoolAndAllowsRetry()
    {
        using var original = NpgsqlDataSource.Create(SampleConnectionString);
        using var replacement = NpgsqlDataSource.Create(SampleConnectionString);
        var attempts = 0;
        using var cache = new SecureConnectionDataSourceCache(new ConfigurationBuilder().Build(), _ =>
            ++attempts switch
            {
                1 => original,
                2 => throw new InvalidOperationException("Creation failed"),
                _ => replacement
            });
        Assert.Same(original, cache.GetOrCreate("source", "original"));

        Assert.Throws<InvalidOperationException>(() => cache.GetOrCreate("source", "replacement"));

        Assert.Same(original, cache.GetOrCreate("source", "original"));
        // A cached reference alone is insufficient: the failed replacement must
        // leave its pool usable, rather than returning an already-disposed source.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await using var connection = await original.OpenConnectionAsync(new CancellationToken(canceled: true));
        });
        Assert.Same(replacement, cache.GetOrCreate("source", "replacement"));
        Assert.Equal(3, attempts);
    }

    [SecurityTest]
    [Fact]
    public async Task Dispose_WithMultiplePools_DisposesAllAndRejectsFurtherCreation()
    {
        using var first = NpgsqlDataSource.Create(SampleConnectionString);
        using var second = NpgsqlDataSource.Create(SampleConnectionString);
        var attempts = 0;
        using var cache = new SecureConnectionDataSourceCache(new ConfigurationBuilder().Build(), _ =>
            ++attempts == 1 ? first : second);
        cache.GetOrCreate("first", SampleConnectionString);
        cache.GetOrCreate("second", SampleConnectionString);

        cache.Dispose();
        cache.Dispose();

        await AssertDisposedAsync(first);
        await AssertDisposedAsync(second);
        Assert.Throws<ObjectDisposedException>(() => cache.GetOrCreate("first", SampleConnectionString));
        Assert.Throws<ObjectDisposedException>(() => cache.GetOrCreate("new", SampleConnectionString));
        Assert.Throws<ObjectDisposedException>(() => cache.GetOrCreate(SampleConnectionString));
        Assert.Equal(2, attempts);
    }

    [SecurityTest]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetOrCreate_WhilePoolIsBeingCreated_RotationOrShutdownCannotOrphanIt(bool rotate)
    {
        using var original = NpgsqlDataSource.Create(SampleConnectionString);
        using var replacement = NpgsqlDataSource.Create(SampleConnectionString);
        using var releaseCreation = new ManualResetEventSlim();
        var creationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cache = new SecureConnectionDataSourceCache(new ConfigurationBuilder().Build(), key =>
        {
            if (key == "original")
            {
                creationEntered.SetResult();
                Assert.True(releaseCreation.Wait(TimeSpan.FromSeconds(10)), "Creation was not released.");
                return original;
            }

            return replacement;
        });
        var creation = Task.Run(() => cache.GetOrCreate("source", "original"));
        var lifecycleFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifecycleThread = new Thread(() =>
        {
            try
            {
                if (rotate)
                {
                    Assert.Same(replacement, cache.GetOrCreate("source", "replacement"));
                }
                else
                {
                    cache.Dispose();
                }

                lifecycleFinished.SetResult();
            }
            catch (Exception exception)
            {
                lifecycleFinished.SetException(exception);
            }
        })
        {
            IsBackground = true
        };

        try
        {
            await creationEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            lifecycleThread.Start();

            // Observe the competing operation blocked on the lifecycle lock while
            // creation is paused. A dedicated thread makes this deterministic: a
            // broken implementation finishes instead of waiting, without sleep-based
            // assumptions about thread-pool scheduling.
            Assert.True(SpinWait.SpinUntil(
                () => (lifecycleThread.ThreadState & (ThreadState.WaitSleepJoin | ThreadState.Stopped)) != 0,
                TimeSpan.FromSeconds(10)));
            Assert.True((lifecycleThread.ThreadState & ThreadState.WaitSleepJoin) != 0,
                "Rotation or shutdown completed before pool construction finished.");
        }
        finally
        {
            releaseCreation.Set();
            await creation.WaitAsync(TimeSpan.FromSeconds(10));
            if ((lifecycleThread.ThreadState & ThreadState.Unstarted) == 0)
            {
                await lifecycleFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        Assert.Same(original, await creation);
        await AssertDisposedAsync(original);
        if (rotate)
        {
            Assert.Same(replacement, cache.GetOrCreate("source", "replacement"));
            cache.Dispose();
            await AssertDisposedAsync(replacement);
        }
        else
        {
            Assert.Throws<ObjectDisposedException>(() => cache.GetOrCreate("source", "replacement"));
        }
    }

    private static async Task AssertDisposedAsync(NpgsqlDataSource dataSource)
    {
        // Disposed pools reject acquisition before cancellation is considered. The
        // already-canceled token also prevents network access if disposal regresses.
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await using var connection = await dataSource.OpenConnectionAsync(new CancellationToken(canceled: true));
        });
    }
}
