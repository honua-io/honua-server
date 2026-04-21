// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Honua.Core.Tests.Features.Infrastructure.Monitoring;

/// <summary>
/// Tests for DatabaseQueryPerformanceMonitor - critical performance monitoring component
/// </summary>
public sealed class DatabaseQueryPerformanceMonitorTests
{
    private readonly ILogger<DatabaseQueryPerformanceMonitor> _mockLogger;
    private readonly IOptionsMonitor<PerformanceMonitoringOptions> _mockOptions;
    private readonly DatabaseQueryPerformanceMonitor _monitor;

    public DatabaseQueryPerformanceMonitorTests()
    {
        _mockLogger = Substitute.For<ILogger<DatabaseQueryPerformanceMonitor>>();
        _mockOptions = Substitute.For<IOptionsMonitor<PerformanceMonitoringOptions>>();

        var options = new PerformanceMonitoringOptions
        {
            SlowQueryThresholdMs = 1000,
            MaxSlowQueryRecords = 100,
            EnableDetailedMetrics = true
        };

        _mockOptions.CurrentValue.Returns(options);

        _monitor = new DatabaseQueryPerformanceMonitor(_mockLogger, _mockOptions);
    }

    [Fact]
    [UnitTest]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        // Act & Assert
        var act = () => new DatabaseQueryPerformanceMonitor(null!, _mockOptions);
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("logger");
    }

    [Fact]
    [UnitTest]
    public void Constructor_NullOptions_ThrowsArgumentNullException()
    {
        // Act & Assert
        var act = () => new DatabaseQueryPerformanceMonitor(_mockLogger, null!);
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("options");
    }

    [Fact]
    [UnitTest]
    public void StartQueryExecution_ValidParameters_ReturnsContext()
    {
        // Act
        var context = _monitor.StartQueryExecution("SELECT", "correlation-123");

        // Assert
        context.Should().NotBeNull();
        context.Should().BeOfType<QueryExecutionContext>();
    }

    [Theory]
    [UnitTest]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void StartQueryExecution_InvalidQueryType_ThrowsArgumentException(string? invalidQueryType)
    {
        // Act & Assert
        var act = () => _monitor.StartQueryExecution(invalidQueryType!, "correlation-123");
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [UnitTest]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void StartQueryExecution_InvalidCorrelationId_ThrowsArgumentException(string? invalidCorrelationId)
    {
        // Act & Assert
        var act = () => _monitor.StartQueryExecution("SELECT", invalidCorrelationId!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    [UnitTest]
    public void GetStatistics_InitialState_ReturnsZeroStatistics()
    {
        // Act
        var statistics = _monitor.GetStatistics();

        // Assert
        statistics.Should().NotBeNull();
        statistics.TotalQueries.Should().Be(0);
        statistics.SlowQueryCount.Should().Be(0);
        statistics.AverageExecutionTimeMs.Should().Be(0);
        statistics.FailedQueryCount.Should().Be(0);
    }

    [Fact]
    [UnitTest]
    public void GetRecentSlowQueries_InitialState_ReturnsEmptyList()
    {
        // Act
        var slowQueries = _monitor.GetRecentSlowQueries();

        // Assert
        slowQueries.Should().NotBeNull();
        slowQueries.Should().BeEmpty();
    }

    [Fact]
    [UnitTest]
    public void GetRecentSlowQueries_WithMaxCount_RespectsLimit()
    {
        // Act
        var slowQueries = _monitor.GetRecentSlowQueries(50);

        // Assert
        slowQueries.Count.Should().BeLessOrEqualTo(50);
    }

    [Fact]
    [UnitTest]
    public void QueryExecutionContext_Complete_UpdatesStatistics()
    {
        // Arrange
        var context = _monitor.StartQueryExecution("SELECT", "test-correlation");

        // Simulate some execution time
        Thread.Sleep(10);

        // Act
        context.Complete(success: true, rowCount: 100);

        // Assert
        var statistics = _monitor.GetStatistics();
        statistics.TotalQueries.Should().Be(1);
        statistics.SuccessfulQueryCount.Should().Be(1);
        statistics.FailedQueryCount.Should().Be(0);
    }

    [Fact]
    [UnitTest]
    public void QueryExecutionContext_CompleteWithFailure_UpdatesFailureStatistics()
    {
        // Arrange
        var context = _monitor.StartQueryExecution("INSERT", "test-correlation");

        // Act
        context.Complete(success: false);

        // Assert
        var statistics = _monitor.GetStatistics();
        statistics.TotalQueries.Should().Be(1);
        statistics.SuccessfulQueryCount.Should().Be(0);
        statistics.FailedQueryCount.Should().Be(1);
    }

    [Fact]
    [UnitTest]
    public void QueryExecutionContext_RecordException_UpdatesStatistics()
    {
        // Arrange
        var context = _monitor.StartQueryExecution("UPDATE", "test-correlation");
        var exception = new InvalidOperationException("Database connection failed");

        // Act
        context.RecordException(exception);
        context.Complete(success: false);

        // Assert
        var statistics = _monitor.GetStatistics();
        statistics.TotalQueries.Should().Be(1);
        statistics.FailedQueryCount.Should().Be(1);
        statistics.ExceptionCount.Should().Be(1);
    }

    [Fact]
    [UnitTest]
    public void QueryExecutionContext_Dispose_CompletesAutomaticallyIfNotClosed()
    {
        // Arrange
        var context = _monitor.StartQueryExecution("DELETE", "test-correlation");

        // Act
        context.Dispose();

        // Assert
        var statistics = _monitor.GetStatistics();
        statistics.TotalQueries.Should().Be(1);
        // Should be marked as failed since it wasn't explicitly completed
        statistics.FailedQueryCount.Should().Be(1);
    }

    [Fact]
    [UnitTest]
    public void QueryExecutionContext_DoubleDispose_DoesNotThrow()
    {
        // Arrange
        var context = _monitor.StartQueryExecution("SELECT", "test-correlation");
        context.Complete(success: true);

        // Act & Assert
        var act = () =>
        {
            context.Dispose();
            context.Dispose(); // Second dispose should not throw
        };

        act.Should().NotThrow();
    }

    [Fact]
    [UnitTest]
    public void QueryExecutionContext_CompleteAfterDispose_ThrowsObjectDisposedException()
    {
        // Arrange
        var context = _monitor.StartQueryExecution("SELECT", "test-correlation");
        context.Dispose();

        // Act & Assert
        var act = () => context.Complete(success: true);
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    [UnitTest]
    public void QueryExecutionContext_CompleteWithAdditionalMetrics_StoresMetrics()
    {
        // Arrange
        var context = _monitor.StartQueryExecution("SELECT", "test-correlation");
        var metrics = new Dictionary<string, object>
        {
            ["CacheHitRatio"] = 0.85,
            ["IndexesUsed"] = 3,
            ["TableScansRequired"] = false
        };

        // Act
        context.Complete(success: true, rowCount: 1000, additionalMetrics: metrics);

        // Assert - This tests that metrics are accepted without throwing
        var statistics = _monitor.GetStatistics();
        statistics.TotalQueries.Should().Be(1);
        statistics.SuccessfulQueryCount.Should().Be(1);
    }

    [Fact]
    [IntegrationTest]
    public void SlowQueryDetection_QueryExceedsThreshold_RecordsAsSlowQuery()
    {
        // Arrange
        var options = new PerformanceMonitoringOptions
        {
            SlowQueryThresholdMs = 50, // Low threshold for testing
            MaxSlowQueryRecords = 100,
            EnableDetailedMetrics = true
        };

        _mockOptions.CurrentValue.Returns(options);

        var monitor = new DatabaseQueryPerformanceMonitor(_mockLogger, _mockOptions);
        var context = monitor.StartQueryExecution("SLOW_SELECT", "slow-query-test");

        // Act - Simulate slow query
        Thread.Sleep(100); // Exceeds 50ms threshold
        context.Complete(success: true, rowCount: 50000);

        // Assert
        var statistics = monitor.GetStatistics();
        statistics.SlowQueryCount.Should().Be(1);

        var slowQueries = monitor.GetRecentSlowQueries();
        slowQueries.Should().HaveCount(1);

        var slowQuery = slowQueries.First();
        slowQuery.QueryType.Should().Be("SLOW_SELECT");
        slowQuery.ExecutionTimeMs.Should().BeGreaterThan(50);
        slowQuery.RowCount.Should().Be(50000);
        slowQuery.CorrelationId.Should().Be("slow-query-test");
    }

    [Fact]
    [UnitTest]
    public void ConcurrentQueryExecution_MultipleThreads_ThreadSafe()
    {
        // Arrange
        const int numThreads = 10;
        const int queriesPerThread = 100;
        var tasks = new List<Task>();

        // Act
        for (int i = 0; i < numThreads; i++)
        {
            var threadId = i;
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < queriesPerThread; j++)
                {
                    using var context = _monitor.StartQueryExecution(
                        $"THREAD_{threadId}_QUERY",
                        $"correlation-{threadId}-{j}");
                    context.Complete(success: true, rowCount: j);
                }
            }));
        }

        Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(10));

        // Assert
        var statistics = _monitor.GetStatistics();
        statistics.TotalQueries.Should().Be(numThreads * queriesPerThread);
        statistics.SuccessfulQueryCount.Should().Be(numThreads * queriesPerThread);
        statistics.FailedQueryCount.Should().Be(0);
    }

    [Fact]
    [UnitTest]
    public void MaxSlowQueryRecords_ExceedsLimit_PrunesOldRecords()
    {
        // Arrange
        var options = new PerformanceMonitoringOptions
        {
            SlowQueryThresholdMs = 1, // Very low threshold
            MaxSlowQueryRecords = 5, // Small limit for testing
            EnableDetailedMetrics = true
        };

        _mockOptions.CurrentValue.Returns(options);

        var monitor = new DatabaseQueryPerformanceMonitor(_mockLogger, _mockOptions);

        // Act - Generate more slow queries than the limit
        for (int i = 0; i < 10; i++)
        {
            using var context = monitor.StartQueryExecution($"QUERY_{i}", $"correlation-{i}");
            Thread.Sleep(10); // Ensures it's a slow query
            context.Complete(success: true);
        }

        // Assert
        var slowQueries = monitor.GetRecentSlowQueries();
        slowQueries.Count.Should().BeLessOrEqualTo(5);

        var statistics = monitor.GetStatistics();
        statistics.SlowQueryCount.Should().Be(10); // Counter should still reflect total
    }
}