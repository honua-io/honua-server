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
    public void RecordDatabaseQuery_ShouldNotThrow()
    {
        // Arrange
        var queryType = "select";
        var layerId = "test-layer";
        var duration = TimeSpan.FromMilliseconds(150);
        var recordCount = 42;

        // Act & Assert
        var exception = Record.Exception(() => _monitor.RecordDatabaseQuery(queryType, layerId, duration, recordCount));
        Assert.Null(exception);
    }

    [Fact]
    public void RecordHttpRequest_ShouldNotThrow()
    {
        // Arrange
        var method = "GET";
        var endpoint = "/api/test";
        var statusCode = 200;
        var duration = TimeSpan.FromMilliseconds(85);

        // Act & Assert
        var exception = Record.Exception(() => _monitor.RecordHttpRequest(method, endpoint, statusCode, duration));
        Assert.Null(exception);
    }

    [Fact]
    public void RecordMemoryUsage_ShouldNotThrow()
    {
        // Arrange
        var allocatedBytes = 1024 * 1024 * 50; // 50MB
        var gen0Collections = 10;
        var gen1Collections = 5;
        var gen2Collections = 2;

        // Act & Assert
        var exception = Record.Exception(() => _monitor.RecordMemoryUsage(allocatedBytes, gen0Collections, gen1Collections, gen2Collections));
        Assert.Null(exception);
    }

    [Fact]
    public void RecordCacheMetrics_ShouldNotThrow()
    {
        // Arrange
        var cacheType = "layer_metadata";
        var operation = "hit";

        // Act & Assert
        var exception = Record.Exception(() => _monitor.RecordCacheMetrics(cacheType, operation));
        Assert.Null(exception);
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

        // Act & Assert - scope should be created and disposed without error
        var exception = Record.Exception(() =>
        {
            using (var scope = _monitor.StartOperation(operationName))
            {
                Assert.NotNull(scope);

                // Simulate some work
                Thread.Sleep(10);

                // Add tags
                scope.WithTag("layer", "test")
                     .WithTag("operation_type", "query");
            }
        });
        Assert.Null(exception);
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

        // Act & Assert
        var exception = Record.Exception(() => _monitor.RecordCounter(name, value, tags));
        Assert.Null(exception);
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

        // Act & Assert
        var exception = Record.Exception(() => _monitor.RecordHistogram(name, value, tags));
        Assert.Null(exception);
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
