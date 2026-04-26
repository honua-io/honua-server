// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Exceptions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Postgres.Features.Infrastructure;
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

    [Theory]
    [InlineData("SELECT 1; DROP TABLE features")]
    [InlineData("SELECT 1 -- comment")]
    [InlineData("WITH deleted AS (DELETE FROM features RETURNING *) SELECT * FROM deleted")]
    [InlineData("UPDATE features SET attributes = '{}'")]
    public async Task GetOrCreatePreparedCommandAsync_UnsafeSql_ReturnsNull(string sql)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();

        var command = await _cache.GetOrCreatePreparedCommandAsync((NpgsqlConnection)connection, sql);

        command.Should().BeNull();
    }

    [Fact]
    public async Task PreparePriorityStatementAsync_UnsafeSql_ReturnsNull()
    {
        await using var connection = await _dataSource.OpenConnectionAsync();

        var command = await _cache.PreparePriorityStatementAsync(
            (NpgsqlConnection)connection,
            "SELECT 1; DROP TABLE features",
            "unsafe_statement");

        command.Should().BeNull();
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
    private readonly NpgsqlDataSource _dataSource;

    public CachingDatabaseConnectionProviderTests(PostgresFixture fixture)
    {
        _dataSource = fixture.DataSource;
        _provider = new CachingDatabaseConnectionProvider(
            _dataSource,
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
        var connection = dbConnection.RequireNpgsqlConnection();

        // Act
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";

        // Assert
        command.Should().NotBeNull();
        command.Should().BeOfType<NpgsqlCommand>();
        command.CommandText.Should().Be("SELECT 1");
    }

    [Fact]
    public async Task OpenConnectionAsync_WithConcurrencyGate_ReturnsWrappedConnection()
    {
        // Arrange — gate active (production default)
        var gate = new QueryConcurrencyGate(new ConnectionLimits { MaxConcurrentQueries = 10 });
        using var provider = new CachingDatabaseConnectionProvider(
            _dataSource,
            NullLogger<CachingDatabaseConnectionProvider>.Instance,
            concurrencyGate: gate);

        // Act
        await using var connection = await provider.OpenConnectionAsync();

        // Assert — the wrapper should preserve Npgsql access while owning slot release.
        connection.Should().NotBeNull();
        connection.Should().BeOfType<SemaphoreReleasingConnection>();
        var npgsql = connection.RequireNpgsqlConnection();
        npgsql.Should().NotBeNull();
        npgsql.Should().BeOfType<NpgsqlConnection>();
    }

    [Fact]
    public async Task OpenConnectionAsync_WithConcurrencyGate_ReleasesSlotWhenConnectionDisposed()
    {
        // Arrange
        var gate = new QueryConcurrencyGate(new ConnectionLimits
        {
            MaxConcurrentQueries = 1,
            ConnectionAcquisitionTimeoutSeconds = 1
        });
        using var provider = new CachingDatabaseConnectionProvider(
            _dataSource,
            NullLogger<CachingDatabaseConnectionProvider>.Instance,
            concurrencyGate: gate);

        // Act
        var connection = await provider.OpenConnectionAsync();
        gate.AvailableSlots.Should().Be(0);

        await connection.DisposeAsync();

        // Assert
        gate.AvailableSlots.Should().Be(1);
    }

    [Fact]
    public async Task OpenNpgsqlConnectionAsync_WithConcurrencyGate_ReleasesSlotWhenLeaseDisposed()
    {
        // Regression — the lease must dispose the wrapper (not just the inner
        // NpgsqlConnection) so the gate slot is released per-operation.
        var gate = new QueryConcurrencyGate(new ConnectionLimits
        {
            MaxConcurrentQueries = 1,
            ConnectionAcquisitionTimeoutSeconds = 1
        });
        using var provider = new CachingDatabaseConnectionProvider(
            _dataSource,
            NullLogger<CachingDatabaseConnectionProvider>.Instance,
            concurrencyGate: gate);

        // Act — acquire via the Npgsql lease helper
        var lease = await provider.OpenNpgsqlConnectionAsync();
        gate.AvailableSlots.Should().Be(0);
        lease.Connection.Should().NotBeNull();

        await lease.DisposeAsync();

        // Assert — slot released; gate should accept a new caller immediately
        gate.AvailableSlots.Should().Be(1);
        await using var next = await provider.OpenNpgsqlConnectionAsync();
        next.Connection.Should().NotBeNull();
    }

    [Fact]
    public async Task OpenConnectionAsync_WhenGateTimesOut_ThrowsWithRetryAfterHint()
    {
        // Regression — the 503 raised on gate timeout must populate
        // RetryAfterSeconds so the middleware can emit a Retry-After header.
        var gate = new QueryConcurrencyGate(new ConnectionLimits
        {
            MaxConcurrentQueries = 1,
            ConnectionAcquisitionTimeoutSeconds = 1
        });
        using var provider = new CachingDatabaseConnectionProvider(
            _dataSource,
            NullLogger<CachingDatabaseConnectionProvider>.Instance,
            concurrencyGate: gate);

        // Exhaust the single slot so the next caller must wait and time out.
        await using var holder = await provider.OpenConnectionAsync();

        var exception = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => provider.OpenConnectionAsync());

        exception.RetryAfterSeconds.Should().NotBeNull();
        exception.RetryAfterSeconds!.Value.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task OpenConnectionAsync_WithConcurrencyGate_ReleasesSlotOnProviderDispose()
    {
        // Arrange
        var gate = new QueryConcurrencyGate(new ConnectionLimits
        {
            MaxConcurrentQueries = 2,
            ConnectionAcquisitionTimeoutSeconds = 1
        });
        var provider = new CachingDatabaseConnectionProvider(
            _dataSource,
            NullLogger<CachingDatabaseConnectionProvider>.Instance,
            concurrencyGate: gate);

        // Act — acquire both slots via the provider
        var conn1 = await provider.OpenConnectionAsync();
        var conn2 = await provider.OpenConnectionAsync();
        gate.AvailableSlots.Should().Be(0);

        // Dispose the provider (simulates DI scope end) — should release both slots
        provider.Dispose();
        gate.AvailableSlots.Should().Be(2);

        // Cleanup
        await conn1.DisposeAsync();
        await conn2.DisposeAsync();
    }

    [Fact]
    public async Task SemaphoreReleasingConnection_RelaysInnerStateChangeToSubscribers()
    {
        // Regression — DbConnectionTracking.Track subscribes to
        // DbConnection.StateChange to drive the active-connection telemetry
        // counter. Without the relay, subscribers on a SemaphoreReleasingConnection
        // wrapper never see the Closed transition and the counter drifts.
        var inner = await _dataSource.OpenConnectionAsync();
        var wrapper = new SemaphoreReleasingConnection(inner, static () => { });

        var observedClose = false;
        wrapper.StateChange += (_, args) =>
        {
            if (args.CurrentState is System.Data.ConnectionState.Closed
                or System.Data.ConnectionState.Broken)
            {
                observedClose = true;
            }
        };

        // Act — dispose the wrapper, which closes the inner connection and
        // should relay the inner's StateChange through to our subscriber.
        await wrapper.DisposeAsync();

        // Assert
        observedClose.Should().BeTrue(
            "wrapper must relay inner StateChange so telemetry decrements on close");
    }

    [Fact]
    public void SemaphoreReleasingConnection_OverridesBeginDbTransactionAsync()
    {
        // Regression — DbConnection.BeginDbTransactionAsync has a default
        // implementation that falls back to the synchronous BeginDbTransaction.
        // Without an explicit override, every gated OpenTransactionAsync call
        // would block the request thread on sync database I/O. This check
        // proves the override is declared on the wrapper itself.
        var method = typeof(SemaphoreReleasingConnection).GetMethod(
            "BeginDbTransactionAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(System.Data.IsolationLevel), typeof(CancellationToken) },
            modifiers: null);

        method.Should().NotBeNull(
            "the wrapper must override BeginDbTransactionAsync to avoid sync fallback");
        method!.DeclaringType.Should().Be<SemaphoreReleasingConnection>(
            "the override must be declared on the wrapper, not inherited from DbConnection");
    }

    [Fact]
    public async Task SemaphoreReleasingConnection_BeginTransactionAsync_ReturnsUsableTransaction()
    {
        // Regression — verify the async transaction path through a gated
        // provider returns a working transaction wrapped around the inner
        // NpgsqlConnection. Calling commit/rollback exercises the async path
        // end-to-end, proving the override forwards correctly.
        var gate = new QueryConcurrencyGate(new ConnectionLimits
        {
            MaxConcurrentQueries = 1,
            ConnectionAcquisitionTimeoutSeconds = 1
        });
        using var provider = new CachingDatabaseConnectionProvider(
            _dataSource,
            NullLogger<CachingDatabaseConnectionProvider>.Instance,
            concurrencyGate: gate);

        await using var connection = await provider.OpenConnectionAsync();
        connection.Should().BeOfType<SemaphoreReleasingConnection>();
        var inner = connection.RequireNpgsqlConnection();

        // Act — open a transaction via the async API the wrapper must support.
        await using var transaction = await connection.BeginTransactionAsync(
            System.Data.IsolationLevel.ReadCommitted);

        // Assert — transaction is bound to the inner Npgsql connection (the
        // wrapper delegates to it) and can be rolled back without throwing.
        transaction.Should().NotBeNull();
        transaction.Connection.Should().BeSameAs(inner,
            "the async override must forward to the inner NpgsqlConnection");
        await transaction.RollbackAsync();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
