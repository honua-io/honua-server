// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Postgres.Features.Infrastructure;
using Honua.Postgres.Features.Security;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace Honua.Postgres.Security.Tests;

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
}
