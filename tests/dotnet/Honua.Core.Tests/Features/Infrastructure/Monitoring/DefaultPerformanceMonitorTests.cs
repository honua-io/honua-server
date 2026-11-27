// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Core.Features.Infrastructure.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Core.Tests.Features.Infrastructure.Monitoring;

[Collection("Metrics")]
public class DefaultPerformanceMonitorTests
{
    private readonly DefaultPerformanceMonitor _monitor;

    public DefaultPerformanceMonitorTests()
    {
        _monitor = new DefaultPerformanceMonitor();
    }

    [Trait("Tier", "Fast")]
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

    [Trait("Tier", "Fast")]
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

    [Trait("Tier", "Fast")]
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

    [Trait("Tier", "Fast")]
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

    [Trait("Tier", "Fast")]
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

    [Trait("Tier", "Fast")]
    [Fact]
    public void OperationScope_ShouldRecordTagsOnDispose()
    {
        // Arrange
        var operationName = "timed-operation";

        // Act & Assert - scope should be created and disposed without error
        var exception = Record.Exception(() =>
        {
            using (var scope = _monitor.StartOperation(operationName))
            {
                Assert.NotNull(scope);

                scope.WithTag("layer", "test")
                     .WithTag("operation_type", "query");
            }
        });
        Assert.Null(exception);
    }

    [Trait("Tier", "Fast")]
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

    [Trait("Tier", "Fast")]
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

    [Trait("Tier", "Fast")]
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

    [Trait("Tier", "Fast")]
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

    [Trait("Tier", "Fast")]
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

    [Trait("Tier", "Fast")]
    [Fact]
    public void RecordDatabaseQuery_OmitsUnboundedLayerIdTag()
    {
        var recordedTags = new ConcurrentBag<Dictionary<string, object?>>();
        using var listener = CaptureInstrument("honua_database_query_total", recordedTags);

        _monitor.RecordDatabaseQuery("select", "layer-12345", TimeSpan.FromMilliseconds(12), 5);

        Assert.NotEmpty(recordedTags);
        Assert.Contains(recordedTags, tags =>
            tags.TryGetValue("query_type", out var qt) && Equals(qt, "select") &&
            !tags.ContainsKey("layer_id"));
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void RecordTransactionDuration_OmitsUnboundedOperationCountTag()
    {
        var recordedTags = new ConcurrentBag<Dictionary<string, object?>>();
        using var listener = CaptureInstrument("honua_transaction_total", recordedTags);

        _monitor.RecordTransactionDuration(TimeSpan.FromMilliseconds(20), 99, wasCommitted: true);

        Assert.NotEmpty(recordedTags);
        Assert.Contains(recordedTags, tags =>
            tags.TryGetValue("status", out var status) && Equals(status, "committed") &&
            !tags.ContainsKey("operation_count"));
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void RecordGeospatialOperation_OmitsUnboundedCoordinateCountAndSridTags()
    {
        var recordedTags = new ConcurrentBag<Dictionary<string, object?>>();
        using var listener = CaptureInstrument("honua_coordinate_transform_total", recordedTags);

        _monitor.RecordGeospatialOperation("transform", TimeSpan.FromMilliseconds(5), 4096, fromSrid: 4326, toSrid: 3857);

        Assert.NotEmpty(recordedTags);
        Assert.Contains(recordedTags, tags =>
            tags.TryGetValue("operation_type", out var op) && Equals(op, "transform") &&
            !tags.ContainsKey("coordinate_count") &&
            !tags.ContainsKey("from_srid") &&
            !tags.ContainsKey("to_srid"));
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task MonitoredResponseCacheDecorator_OmitsUnboundedTimingAndExpirationTags()
    {
        var recordedTags = new ConcurrentBag<string>();
        var scope = Substitute.For<IOperationScope>();
        scope.WithTag(Arg.Do<string>(key => recordedTags.Add(key)), Arg.Any<string>()).Returns(scope);
        var monitor = Substitute.For<IPerformanceMonitor>();
        monitor.StartOperation(Arg.Any<string>()).Returns(scope);
        var inner = Substitute.For<IResponseCache>();
        inner.BindKeyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<string>());
        inner.GetAsync<string>(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        var decorator = new MonitoredResponseCacheDecorator(inner, monitor, NullLogger<MonitoredResponseCacheDecorator>.Instance);

        await decorator.SetAsync("query:layer:1", "payload", TimeSpan.FromSeconds(42));
        await decorator.GetOrCreateAsync("query:layer:2", () => Task.FromResult("fresh"), TimeSpan.FromSeconds(60));

        Assert.DoesNotContain("expiration_seconds", recordedTags);
        Assert.DoesNotContain("factory_time_ms", recordedTags);
        Assert.Contains("cache_type", recordedTags);
        Assert.Contains("result", recordedTags);
    }

    private static MeterListener CaptureInstrument(string instrumentName, ConcurrentBag<Dictionary<string, object?>> sink)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, active) =>
            {
                if (instrument.Name == instrumentName)
                {
                    active.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) => CaptureTags(tags, sink));
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) => CaptureTags(tags, sink));
        listener.Start();
        return listener;
    }

    private static void CaptureTags(ReadOnlySpan<KeyValuePair<string, object?>> tags, ConcurrentBag<Dictionary<string, object?>> sink)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            map[tag.Key] = tag.Value;
        }

        sink.Add(map);
    }

}
