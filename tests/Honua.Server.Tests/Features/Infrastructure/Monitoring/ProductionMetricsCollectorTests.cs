// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Server.Features.Infrastructure.Monitoring;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

/// <summary>
/// Tests for the ProductionMetricsCollector.
/// </summary>
public class ProductionMetricsCollectorTests
{
    private readonly IMemoryCache _memoryCache;
    private readonly ConnectionPoolMetrics _connectionPoolMetrics;
    private readonly IActiveDbConnectionTracker _connectionTracker;
    private readonly CacheOptions _cacheOptions;

    public ProductionMetricsCollectorTests()
    {
        _memoryCache = new MemoryCache(new MemoryCacheOptions());
        _connectionTracker = new ActiveDbConnectionTracker();
        _connectionPoolMetrics = new ConnectionPoolMetrics(_connectionTracker);
        _cacheOptions = new CacheOptions();
    }

    [Fact]
    public void RecordQuery_ShouldIncrementQueryCount()
    {
        // Arrange
        using var metricsCollector = CreateMetricsCollector();

        // Act
        metricsCollector.RecordQuery(
            TimeSpan.FromMilliseconds(100),
            "FeatureServer",
            "Query",
            true);

        // Assert
        var healthMetrics = metricsCollector.GetHealthMetrics();
        Assert.Equal(1, healthMetrics.TotalQueries);
        Assert.Equal(0, healthMetrics.TotalErrors);
    }

    [Fact]
    public void RecordQuery_WithError_ShouldIncrementErrorCount()
    {
        // Arrange
        using var metricsCollector = CreateMetricsCollector();

        // Act
        metricsCollector.RecordQuery(
            TimeSpan.FromMilliseconds(5000),
            "FeatureServer",
            "Query",
            false);

        // Assert
        var healthMetrics = metricsCollector.GetHealthMetrics();
        Assert.Equal(1, healthMetrics.TotalQueries);
        Assert.Equal(1, healthMetrics.TotalErrors);
        Assert.Equal(1.0, healthMetrics.ErrorRate);
    }

    [Fact]
    public void RecordCacheHit_ShouldUpdateCacheHitRatio()
    {
        // Arrange
        using var metricsCollector = CreateMetricsCollector();

        // Act
        metricsCollector.RecordCacheHit("Layer");
        metricsCollector.RecordCacheHit("Service");
        metricsCollector.RecordCacheMiss("Query");

        // Assert
        var healthMetrics = metricsCollector.GetHealthMetrics();
        Assert.Equal(2.0 / 3.0, healthMetrics.CacheHitRatio, precision: 2);
    }

    [Fact]
    public void RecordRateLimitViolation_ShouldIncrementViolationCount()
    {
        // Arrange
        using var metricsCollector = CreateMetricsCollector();

        // Act
        metricsCollector.RecordRateLimitViolation("TooManyRequests", "127.0.0.1");
        metricsCollector.RecordRateLimitViolation("TooManyRequests", "192.168.1.1");

        // Assert
        var healthMetrics = metricsCollector.GetHealthMetrics();
        Assert.Equal(2, healthMetrics.RateLimitViolations);
    }

    [Fact]
    public void GetHealthMetrics_ShouldReturnCurrentTimestamp()
    {
        // Arrange
        using var metricsCollector = CreateMetricsCollector();
        var beforeCall = DateTimeOffset.UtcNow;

        // Act
        var healthMetrics = metricsCollector.GetHealthMetrics();

        // Assert
        var afterCall = DateTimeOffset.UtcNow;
        Assert.True(healthMetrics.Timestamp >= beforeCall);
        Assert.True(healthMetrics.Timestamp <= afterCall);
    }

    [Fact]
    public void IsHealthy_WithGoodMetrics_ShouldReturnTrue()
    {
        // Arrange
        using var metricsCollector = CreateMetricsCollector();

        // Act - simulate good metrics
        metricsCollector.RecordCacheHit("Layer");
        metricsCollector.RecordCacheHit("Service");
        metricsCollector.RecordQuery(TimeSpan.FromMilliseconds(100), "FeatureServer", "Query", true);

        // Assert
        var healthMetrics = metricsCollector.GetHealthMetrics();
        Assert.True(healthMetrics.IsHealthy());
    }

    [Fact]
    public void IsHealthy_WithHighErrorRate_ShouldReturnFalse()
    {
        // Arrange
        using var metricsCollector = CreateMetricsCollector();

        // Act - simulate high error rate (>5%)
        for (int i = 0; i < 10; i++)
        {
            metricsCollector.RecordQuery(TimeSpan.FromMilliseconds(100), "FeatureServer", "Query", false);
        }

        // Assert
        var healthMetrics = metricsCollector.GetHealthMetrics();
        Assert.False(healthMetrics.IsHealthy());
        Assert.Contains("High error rate", string.Join(", ", healthMetrics.GetAlertConditions()));
    }

    [Fact]
    public void IsHealthy_WithLowCacheHitRatio_ShouldReturnFalse()
    {
        // Arrange
        using var metricsCollector = CreateMetricsCollector();

        // Act - simulate low cache hit ratio (<80%)
        metricsCollector.RecordCacheHit("Layer");
        for (int i = 0; i < 10; i++)
        {
            metricsCollector.RecordCacheMiss("Query");
        }

        // Assert
        var healthMetrics = metricsCollector.GetHealthMetrics();
        Assert.False(healthMetrics.IsHealthy());
        Assert.Contains("Low cache hit ratio", string.Join(", ", healthMetrics.GetAlertConditions()));
    }

    [Fact]
    public void RecordFileUpload_ShouldCategorizeFileSize()
    {
        // Arrange
        using var metricsCollector = CreateMetricsCollector();

        // Act
        metricsCollector.RecordFileUpload(
            TimeSpan.FromSeconds(5),
            50 * 1024 * 1024, // 50MB
            true);

        // Assert - No direct assertion possible without exposing internal metrics,
        // but this verifies the method doesn't throw
        var healthMetrics = metricsCollector.GetHealthMetrics();
        Assert.NotNull(healthMetrics);
    }

    private ProductionMetricsCollector CreateMetricsCollector()
    {
        var logger = new FakeLogger<ProductionMetricsCollector>();
        var cacheOptions = Options.Create(_cacheOptions);

        return new ProductionMetricsCollector(
            _memoryCache,
            _connectionPoolMetrics,
            _connectionTracker,
            cacheOptions,
            logger);
    }

    private sealed class FakeLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }

    public void Dispose()
    {
        _memoryCache.Dispose();
        _connectionPoolMetrics.Dispose();
    }
}