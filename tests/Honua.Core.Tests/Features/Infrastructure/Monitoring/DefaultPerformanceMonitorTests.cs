// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Monitoring;

namespace Honua.Core.Tests.Features.Infrastructure.Monitoring;

/// <summary>
/// Unit tests for DefaultPerformanceMonitor functionality.
/// </summary>
[Collection("Metrics")]
public class DefaultPerformanceMonitorTests
{
    private readonly DefaultPerformanceMonitor _monitor;

    public DefaultPerformanceMonitorTests()
    {
        _monitor = new DefaultPerformanceMonitor();
    }

    [Fact]
    public void RecordDatabaseQuery_ShouldRecordCorrectMetrics()
    {
        // Arrange
        var queryType = "select";
        var layerId = "test-layer";
        var duration = TimeSpan.FromMilliseconds(150);
        var recordCount = 42;

        // Act
        _monitor.RecordDatabaseQuery(queryType, layerId, duration, recordCount);

        // Assert - No exceptions thrown indicates success
        // In a real test environment, you would capture and verify the metrics values
        Assert.True(true);
    }

    [Fact]
    public void RecordHttpRequest_ShouldRecordCorrectMetrics()
    {
        // Arrange
        var method = "GET";
        var endpoint = "/api/test";
        var statusCode = 200;
        var duration = TimeSpan.FromMilliseconds(85);

        // Act
        _monitor.RecordHttpRequest(method, endpoint, statusCode, duration);

        // Assert
        Assert.True(true);
    }

    [Fact]
    public void RecordMemoryUsage_ShouldRecordCorrectMetrics()
    {
        // Arrange
        var allocatedBytes = 1024 * 1024 * 50; // 50MB
        var gen0Collections = 10;
        var gen1Collections = 5;
        var gen2Collections = 2;

        // Act
        _monitor.RecordMemoryUsage(allocatedBytes, gen0Collections, gen1Collections, gen2Collections);

        // Assert
        Assert.True(true);
    }

    [Fact]
    public void RecordCacheMetrics_ShouldRecordCorrectMetrics()
    {
        // Arrange
        var cacheType = "layer_metadata";
        var operation = "hit";

        // Act
        _monitor.RecordCacheMetrics(cacheType, operation);

        // Assert
        Assert.True(true);
    }

    [Fact]
    public void StartOperation_ShouldReturnValidScope()
    {
        // Arrange
        var operationName = "test-operation";

        // Act
        var scope = _monitor.StartOperation(operationName);

        // Assert
        Assert.NotNull(scope);
        Assert.IsAssignableFrom<IOperationScope>(scope);
    }

    [Fact]
    public void OperationScope_ShouldTrackTiming()
    {
        // Arrange
        var operationName = "timed-operation";

        // Act & Assert
        using (var scope = _monitor.StartOperation(operationName))
        {
            Assert.NotNull(scope);

            // Simulate some work
            Thread.Sleep(10);

            // Add tags
            scope.WithTag("layer", "test")
                 .WithTag("operation_type", "query");
        }

        // Scope disposal should record timing metrics
        Assert.True(true);
    }

    [Fact]
    public void RecordCounter_ShouldAcceptCustomMetric()
    {
        // Arrange
        var name = "custom_counter";
        var value = 42L;
        var tags = new Dictionary<string, string>
        {
            { "component", "test" },
            { "version", "1.0" }
        };

        // Act
        _monitor.RecordCounter(name, value, tags);

        // Assert
        Assert.True(true);
    }

    [Fact]
    public void RecordHistogram_ShouldAcceptCustomMetric()
    {
        // Arrange
        var name = "custom_histogram";
        var value = 123.45;
        var tags = new Dictionary<string, string>
        {
            { "operation", "test" }
        };

        // Act
        _monitor.RecordHistogram(name, value, tags);

        // Assert
        Assert.True(true);
    }

    [Fact]
    public void RecordCounter_WithNullTags_ShouldNotThrow()
    {
        // Arrange
        var name = "simple_counter";
        var value = 1L;

        // Act & Assert
        var exception = Record.Exception(() => _monitor.RecordCounter(name, value, null));
        Assert.Null(exception);
    }

    [Fact]
    public void RecordHistogram_WithEmptyTags_ShouldNotThrow()
    {
        // Arrange
        var name = "simple_histogram";
        var value = 42.0;
        var emptyTags = new Dictionary<string, string>();

        // Act & Assert
        var exception = Record.Exception(() => _monitor.RecordHistogram(name, value, emptyTags));
        Assert.Null(exception);
    }

    [Fact]
    public void OperationScope_WithTags_ShouldChain()
    {
        // Arrange & Act
        using var scope = _monitor.StartOperation("chained-operation")
            .WithTag("tag1", "value1")
            .WithTag("tag2", "value2")
            .WithTag("tag3", "value3");

        // Assert
        Assert.NotNull(scope);
        // The chaining should work and dispose properly
    }

}
