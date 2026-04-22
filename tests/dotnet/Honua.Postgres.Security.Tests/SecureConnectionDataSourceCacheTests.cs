// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Postgres.Features.Security;
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

    [Fact]
    public void GetOrCreate_WithConfiguredDefaultSchema_EmbedsSearchPathInOptions()
    {
        // Arrange — configuration mirrors the production wiring in
        // ServiceCollectionExtensions.AddPostgreSqlServices which propagates
        // Database:Schema into the default NpgsqlDataSource. The secure cache
        // must do the same so background/service callers (ISchemaContext.CurrentSchema == null)
        // still resolve schema-qualified tables.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Schema"] = "honua_tenant_a"
            })
            .Build();

        using var cache = new SecureConnectionDataSourceCache(configuration);

        // Act
        var dataSource = cache.GetOrCreate(SampleConnectionString);

        // Assert — the connection string should include the Options parameter
        // with search_path pinned to the configured schema.
        var builder = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);
        Assert.Contains("search_path=\"honua_tenant_a\",public", builder.Options ?? string.Empty);
    }

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
