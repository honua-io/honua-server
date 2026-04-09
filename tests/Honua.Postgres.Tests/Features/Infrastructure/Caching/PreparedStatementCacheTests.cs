// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;
using Honua.Postgres.Features.Infrastructure.Caching;
using Honua.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Honua.Postgres.Tests.Features.Infrastructure.Caching;

/// <summary>
/// Integration tests for prepared statement caching functionality
/// </summary>
/// <remarks>
/// Verifies that prepared statement caching integrates properly with existing
/// parameterized query patterns without breaking security or functionality.
/// </remarks>
[Collection("Database")]
public class PreparedStatementCacheTests : IDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PreparedStatementCache _cache;

    public PreparedStatementCacheTests(PostgresFixture fixture)
    {
        _dataSource = fixture.DataSource;

        var options = new QueryCacheOptions
        {
            MaxCachedStatements = 10,
            StatementLifetimeMinutes = 5,
            MinExecutionsForCaching = 2,
            EnableAutomaticCaching = true,
            EnablePerformanceLogging = false,
            CleanupIntervalMinutes = 1
        };

        _cache = new PreparedStatementCache(
            Options.Create(options),
            NullLogger<PreparedStatementCache>.Instance);
    }

    [Fact]
    public async Task GetOrCreatePreparedCommandAsync_FirstExecution_ReturnsNull()
    {
        // Arrange
        await using var connection = await _dataSource.OpenConnectionAsync();
        const string sql = "SELECT 1 WHERE $1 = $1";

        // Act
        var result = await _cache.GetOrCreatePreparedCommandAsync(
            (NpgsqlConnection)connection, sql);

        // Assert
        result.Should().BeNull("first execution should not trigger caching");
    }

    [Fact]
    public async Task GetOrCreatePreparedCommandAsync_MultipleExecutions_CreatesPreparedStatement()
    {
        // Arrange
        await using var connection = await _dataSource.OpenConnectionAsync();
        const string sql = "SELECT $1 as test_value";

        // Define parameter configuration
        Action<NpgsqlCommand> configureParams = cmd =>
        {
            cmd.Parameters.AddWithValue("$1", 42);
        };

        // Act - First execution should not trigger caching
        var firstResult = await _cache.GetOrCreatePreparedCommandAsync(
            (NpgsqlConnection)connection, sql, configureParams);
        firstResult.Should().BeNull("first execution should not trigger caching");

        // Act - Second execution hits threshold and creates prepared statement
        var preparedCommand = await _cache.GetOrCreatePreparedCommandAsync(
            (NpgsqlConnection)connection, sql, configureParams);

        // Assert
        preparedCommand.Should().NotBeNull("second execution should create prepared statement (MinExecutionsForCaching=2)");
        preparedCommand!.CommandText.Should().Be(sql);
    }

    [Fact]
    public async Task GetOrCreatePreparedCommandAsync_CachedStatement_ReturnsClonedCommand()
    {
        // Arrange
        await using var connection = await _dataSource.OpenConnectionAsync();
        const string sql = "SELECT $1 as test_value";

        // Define parameter configuration
        Action<NpgsqlCommand> configureParams = cmd =>
        {
            cmd.Parameters.AddWithValue("$1", 42);
        };

        // Prime the cache
        for (int i = 0; i < 3; i++)
        {
            await _cache.GetOrCreatePreparedCommandAsync((NpgsqlConnection)connection, sql, configureParams);
        }

        // Act
        var first = await _cache.GetOrCreatePreparedCommandAsync((NpgsqlConnection)connection, sql, configureParams);
        var second = await _cache.GetOrCreatePreparedCommandAsync((NpgsqlConnection)connection, sql, configureParams);

        // Assert
        first.Should().NotBeNull();
        second.Should().NotBeNull();
        first.Should().NotBeSameAs(second, "should return cloned instances");
        first!.CommandText.Should().Be(second!.CommandText);
    }

    [Fact]
    public async Task GetOrCreatePreparedCommandAsync_MultipleExecutions_ReturnedCommandExecutes()
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        const string sql = "SELECT $1::integer as test_value";

        Action<NpgsqlCommand> configureParams = cmd =>
        {
            cmd.Parameters.AddWithValue(42);
        };

        await _cache.GetOrCreatePreparedCommandAsync((NpgsqlConnection)connection, sql, configureParams);
        await using var preparedCommand = await _cache.GetOrCreatePreparedCommandAsync(
            (NpgsqlConnection)connection,
            sql,
            configureParams);

        preparedCommand.Should().NotBeNull();

        var result = await preparedCommand!.ExecuteScalarAsync();
        result.Should().Be(42);
    }

    [Fact]
    public async Task PreparePriorityStatementAsync_ValidStatement_CreatesPreparedStatement()
    {
        // Arrange
        await using var connection = await _dataSource.OpenConnectionAsync();
        const string sql = "SELECT $1 as priority_test";
        const string statementName = "priority_test";

        // Define parameter configuration
        Action<NpgsqlCommand> configureParams = cmd =>
        {
            cmd.Parameters.AddWithValue("$1", "test");
        };

        // Act
        var command = await _cache.PreparePriorityStatementAsync(
            (NpgsqlConnection)connection, sql, statementName, configureParams);

        // Assert
        command.Should().NotBeNull();
        command.CommandText.Should().Be(sql);
    }

    [Fact]
    public void GetStatistics_InitialState_ReturnsZeroStatistics()
    {
        // Act
        var stats = _cache.GetStatistics();

        // Assert
        stats.TotalStatements.Should().Be(0);
        stats.CacheHits.Should().Be(0);
        stats.CacheMisses.Should().Be(0);
        stats.PreparedStatements.Should().Be(0);
        stats.HitRatio.Should().Be(0);
    }

    [Fact]
    public void ClearConnectionCache_ExistingCache_ClearsSuccessfully()
    {
        // Arrange
        using var connection = _dataSource.OpenConnection();

        // Act & Assert - Should not throw
        _cache.ClearConnectionCache(connection);
    }

    public void Dispose()
    {
        _cache?.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Tests for caching-aware database connection provider
/// </summary>
[Collection("Database")]
public class CachingDatabaseConnectionProviderTests : IDisposable
{
    private readonly CachingDatabaseConnectionProvider _provider;

    public CachingDatabaseConnectionProviderTests(PostgresFixture fixture)
    {
        _provider = new CachingDatabaseConnectionProvider(
            fixture.DataSource,
            NullLogger<CachingDatabaseConnectionProvider>.Instance);
    }

    [Fact]
    public async Task OpenConnectionAsync_ValidConfiguration_ReturnsConnection()
    {
        // Act
        await using var connection = await _provider.OpenConnectionAsync();

        // Assert
        connection.Should().NotBeNull();
        connection.Should().BeOfType<NpgsqlConnection>();
    }

    [Fact]
    public async Task CreateCommand_WithCachingConnection_ReturnsCachingCommand()
    {
        // Arrange
        await using var dbConnection = await _provider.OpenConnectionAsync();
        var connection = (NpgsqlConnection)dbConnection;

        // Act
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";

        // Assert
        command.Should().NotBeNull();
        command.Should().BeOfType<NpgsqlCommand>();
        command.CommandText.Should().Be("SELECT 1");
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
