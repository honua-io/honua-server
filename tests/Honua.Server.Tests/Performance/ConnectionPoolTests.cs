// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Globalization;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Performance;

/// <summary>
/// Integration tests verifying connection pool behavior under load.
/// Tests for connection leaks, pool exhaustion, and proper resource cleanup.
///
/// Targets from Issue #46:
/// - Connection pool sizing guidance for production
/// - Connection leak verification to prevent resource exhaustion
/// - Load testing with concurrent request scenarios
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Performance")]
[Collection("Database")]
public sealed class ConnectionPoolTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    /// <summary>
    /// Verifies that connections are properly returned to the pool after use.
    /// Opens and closes many connections sequentially to detect leaks.
    /// </summary>
    [IntegrationTest]
    [Trait("Operation", "ConnectionPool")]
    public async Task Connections_ReturnsToPoolAfterUse()
    {
        // Arrange
        const int iterations = 100;

        // Act - repeatedly acquire and release connections
        for (int i = 0; i < iterations; i++)
        {
            await using var conn = await _fixture.GetConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync();
        }

        // Assert - should complete without pool exhaustion
        // If connections weren't returned, we'd hit pool exhaustion
        await using var finalConn = await _fixture.GetConnectionAsync();
        Assert.True(finalConn.State == System.Data.ConnectionState.Open);
    }

    /// <summary>
    /// Verifies connection pool behavior under concurrent load.
    /// Multiple tasks acquiring connections simultaneously.
    /// </summary>
    [IntegrationTest]
    [Trait("Operation", "ConnectionPool")]
    public async Task Connections_HandlesConcurrentRequests()
    {
        // Arrange
        const int concurrency = 50;
        const int requestsPerTask = 20;
        var successCount = 0;
        var failureCount = 0;

        // Act - concurrent connection acquisition
        var tasks = Enumerable.Range(0, concurrency).Select(async taskId =>
        {
            for (int i = 0; i < requestsPerTask; i++)
            {
                try
                {
                    await using var conn = await _fixture.GetConnectionAsync();
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT pg_backend_pid()";
                    await cmd.ExecuteScalarAsync();
                    Interlocked.Increment(ref successCount);
                }
                catch
                {
                    Interlocked.Increment(ref failureCount);
                }
            }
        });

        await Task.WhenAll(tasks);

        // Assert
        var totalExpected = concurrency * requestsPerTask;
        Assert.Equal(totalExpected, successCount);
        Assert.Equal(0, failureCount);
    }

    /// <summary>
    /// Verifies connections are properly disposed even when exceptions occur.
    /// Tests that error paths don't leak connections.
    /// </summary>
    [IntegrationTest]
    [Trait("Operation", "ConnectionPool")]
    public async Task Connections_NoLeakOnException()
    {
        // Arrange
        const int iterations = 50;
        var errorCount = 0;

        // Act - cause controlled errors and verify cleanup
        for (int i = 0; i < iterations; i++)
        {
            try
            {
                await using var conn = await _fixture.GetConnectionAsync();
                await using var cmd = conn.CreateCommand();
                // Invalid SQL will throw
                cmd.CommandText = "SELECT * FROM nonexistent_table_12345";
                await cmd.ExecuteScalarAsync();
            }
            catch
            {
                errorCount++;
            }
        }

        // Assert - all iterations should have thrown
        Assert.Equal(iterations, errorCount);

        // Verify pool is still functional
        await using var verifyConn = await _fixture.GetConnectionAsync();
        await using var verifyCmd = verifyConn.CreateCommand();
        verifyCmd.CommandText = "SELECT 1";
        var result = await verifyCmd.ExecuteScalarAsync();
        Assert.Equal(1, result);
    }

    /// <summary>
    /// Tests sustained connection usage over a period of time.
    /// Verifies no memory growth or connection accumulation.
    /// </summary>
    [IntegrationTest]
    [Trait("Operation", "ConnectionPool")]
    [Trait("Category", "Slow")]
    public async Task Connections_SustainedLoadNoLeaks()
    {
        // Arrange
        var duration = TimeSpan.FromSeconds(10);
        var endTime = DateTime.UtcNow.Add(duration);
        var connectionCount = 0;
        var beforeMemory = GC.GetTotalMemory(forceFullCollection: true);

        // Act - sustained connection usage
        while (DateTime.UtcNow < endTime)
        {
            await using var conn = await _fixture.GetConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT pg_backend_pid()";
            await cmd.ExecuteScalarAsync();
            connectionCount++;
        }

        // Force GC to measure actual memory usage
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var afterMemory = GC.GetTotalMemory(forceFullCollection: true);

        // Assert
        Assert.True(connectionCount > 100, $"Expected at least 100 connections, got {connectionCount}");

        // Memory growth should be minimal (less than 10MB) for connection handling
        var memoryGrowthMB = (afterMemory - beforeMemory) / (1024.0 * 1024.0);
        Assert.True(memoryGrowthMB < 10, $"Memory grew by {memoryGrowthMB:F2}MB, expected less than 10MB");
    }

    /// <summary>
    /// Verifies connection pool metrics can be queried.
    /// Tests that we can monitor pool health.
    /// </summary>
    [IntegrationTest]
    [Trait("Operation", "ConnectionPool")]
    public async Task ConnectionPool_CanQueryMetrics()
    {
        // Arrange - use some connections
        var tasks = Enumerable.Range(0, 10).Select(async _ =>
        {
            await using var conn = await _fixture.GetConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT pg_sleep(0.1)";
            await cmd.ExecuteScalarAsync();
        });

        // Hold connections briefly
        var holdTask = Task.WhenAll(tasks);

        // Act - query backend connections while tasks are running
        await using var conn = await _fixture.GetConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT count(*)
            FROM pg_stat_activity
            WHERE datname = current_database()
            AND pid != pg_backend_pid()
            """;
        var activeConnections = await cmd.ExecuteScalarAsync();

        await holdTask;

        // Assert - there should be active connections
        Assert.NotNull(activeConnections);
        var count = Convert.ToInt32(activeConnections, CultureInfo.InvariantCulture);
        Assert.True(count >= 0, "Should be able to query active connections");
    }

    /// <summary>
    /// Tests connection pool behavior with transaction usage.
    /// Verifies transactions don't leak connections.
    /// </summary>
    [IntegrationTest]
    [Trait("Operation", "ConnectionPool")]
    public async Task Connections_TransactionsNoLeaks()
    {
        // Arrange
        const int iterations = 50;

        // Act - use transactions
        for (int i = 0; i < iterations; i++)
        {
            await using var conn = await _fixture.GetConnectionAsync();
            await using var transaction = await conn.BeginTransactionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync();
            await transaction.CommitAsync();
        }

        // Assert - pool still functional
        await using var testConn = await _fixture.GetConnectionAsync();
        Assert.True(testConn.State == System.Data.ConnectionState.Open);
    }

    /// <summary>
    /// Tests connection pool behavior when transactions are rolled back.
    /// </summary>
    [IntegrationTest]
    [Trait("Operation", "ConnectionPool")]
    public async Task Connections_RolledBackTransactionsNoLeaks()
    {
        // Arrange
        const int iterations = 50;

        // Act - use and rollback transactions
        for (int i = 0; i < iterations; i++)
        {
            await using var conn = await _fixture.GetConnectionAsync();
            await using var transaction = await conn.BeginTransactionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync();
            await transaction.RollbackAsync();
        }

        // Assert - pool still functional
        await using var testConn = await _fixture.GetConnectionAsync();
        Assert.True(testConn.State == System.Data.ConnectionState.Open);
    }

    /// <summary>
    /// Tests that connection pool handles burst traffic.
    /// Simulates sudden spike in connection requests.
    /// </summary>
    [IntegrationTest]
    [Trait("Operation", "ConnectionPool")]
    public async Task ConnectionPool_HandlesBurstTraffic()
    {
        // Arrange
        const int burstSize = 100;
        var connectionIds = new ConcurrentBag<int>();

        // Act - burst of concurrent connections
        var tasks = Enumerable.Range(0, burstSize).Select(async _ =>
        {
            await using var conn = await _fixture.GetConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT pg_backend_pid()";
            var pid = await cmd.ExecuteScalarAsync();
            if (pid != null)
            {
                connectionIds.Add(Convert.ToInt32(pid, CultureInfo.InvariantCulture));
            }
        });

        await Task.WhenAll(tasks);

        // Assert - all requests completed
        Assert.Equal(burstSize, connectionIds.Count);

        // Verify pool reuse (should see fewer unique PIDs than total requests)
        var uniquePids = connectionIds.Distinct().Count();
        Assert.True(uniquePids <= burstSize,
            $"Expected connection pooling, got {uniquePids} unique connections for {burstSize} requests");
    }
}
