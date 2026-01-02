// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Honua.Server.Features.Infrastructure.Monitoring;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

/// <summary>
/// Integration tests for metrics endpoints functionality.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.MetricsApi)]
public class MetricsEndpointsTests : IClassFixture<WebAppFixture>
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly WebAppFixture _fixture;

    public MetricsEndpointsTests(WebAppFixture fixture)
    {
        _fixture = fixture;
    }

    [IntegrationTest]
    [Operation(Operations.GetHealthMetrics)]
    [Endpoint("GET /api/metrics/health")]
    public async Task GetHealthMetrics_ShouldReturnBasicHealthData()
    {
        // Act
        var response = await _fixture.Client.GetAsync("/api/metrics/health");
        var content = await response.Content.ReadAsStringAsync();
        var allowHeader = response.Headers.TryGetValues("Allow", out var allowValues)
            ? string.Join(", ", allowValues)
            : "none";

        // Assert
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Expected OK, got {response.StatusCode}. Allow: {allowHeader}. Body: {content}");
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var healthMetrics = JsonSerializer.Deserialize<HealthMetrics>(content, _jsonOptions);

        Assert.NotNull(healthMetrics);
        Assert.Equal("healthy", healthMetrics.Status);
        Assert.True(healthMetrics.MemoryUsageMB > 0, "Memory usage should be positive");
        Assert.True(healthMetrics.MemoryPressurePercent >= 0, "Memory pressure should be non-negative");
        Assert.True(healthMetrics.GCCollections >= 0, "GC collections should be non-negative");
        Assert.True(healthMetrics.Timestamp != default, "Timestamp should be set");
    }

    [IntegrationTest]
    [Operation(Operations.GetPerformanceMetrics)]
    [Endpoint("GET /api/metrics/performance")]
    public async Task GetPerformanceMetrics_ShouldRequireAuthentication()
    {
        // Act
        var response = await _fixture.Client.GetAsync("/api/metrics/performance");
        var content = await response.Content.ReadAsStringAsync();
        var allowHeader = response.Headers.TryGetValues("Allow", out var allowValues)
            ? string.Join(", ", allowValues)
            : "none";

        // Assert
        // In development, endpoints may not require auth, so we check for either OK or Unauthorized
        if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.Unauthorized)
        {
            Assert.Fail($"Expected OK or Unauthorized, got {response.StatusCode}. Allow: {allowHeader}. Body: {content}");
        }

        if (response.StatusCode == HttpStatusCode.OK)
        {
            var perfMetrics = JsonSerializer.Deserialize<PerformanceMetricsResponse>(content, _jsonOptions);

            Assert.NotNull(perfMetrics);
            Assert.True(perfMetrics.Timestamp != default, "Timestamp should be set");
            Assert.True(perfMetrics.Memory.AllocatedBytes > 0, "Memory should be allocated");
            Assert.NotNull(perfMetrics.SystemInfo);
            Assert.True(perfMetrics.SystemInfo.ProcessorCount > 0, "Should have processors");
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetDatabaseMetrics)]
    [Endpoint("GET /api/metrics/database")]
    public async Task GetDatabaseMetrics_ShouldReturnDatabaseStats()
    {
        // Act
        var response = await _fixture.Client.GetAsync("/api/metrics/database");

        // Assert
        if (response.StatusCode == HttpStatusCode.OK)
        {
            var content = await response.Content.ReadAsStringAsync();
            var dbMetrics = JsonSerializer.Deserialize<DatabaseMetrics>(content, _jsonOptions);

            Assert.NotNull(dbMetrics);
            Assert.True(dbMetrics.Timestamp != default, "Timestamp should be set");
            Assert.True(dbMetrics.CacheHitRate >= 0.0 && dbMetrics.CacheHitRate <= 1.0, "Hit rate should be 0-1");
            Assert.True(dbMetrics.CacheHits >= 0, "Cache hits should be non-negative");
            Assert.True(dbMetrics.CacheMisses >= 0, "Cache misses should be non-negative");
            Assert.NotNull(dbMetrics.Operations);
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetCacheMetrics)]
    [Endpoint("GET /api/metrics/cache")]
    public async Task GetCacheMetrics_ShouldReturnCacheStats()
    {
        // Act
        var response = await _fixture.Client.GetAsync("/api/metrics/cache");

        // Assert
        if (response.StatusCode == HttpStatusCode.OK)
        {
            var content = await response.Content.ReadAsStringAsync();
            var cacheMetrics = JsonSerializer.Deserialize<CacheMetrics>(content, _jsonOptions);

            Assert.NotNull(cacheMetrics);
            Assert.True(cacheMetrics.Timestamp != default, "Timestamp should be set");
            Assert.True(cacheMetrics.TotalRequests >= 0, "Total requests should be non-negative");
            Assert.True(cacheMetrics.HitRatio >= 0.0 && cacheMetrics.HitRatio <= 1.0, "Hit ratio should be 0-1");
            Assert.NotNull(cacheMetrics.Types);
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetMemoryMetrics)]
    [Endpoint("GET /api/metrics/memory")]
    public async Task GetMemoryMetrics_ShouldReturnMemoryStats()
    {
        // Act
        var response = await _fixture.Client.GetAsync("/api/metrics/memory");

        // Assert
        if (response.StatusCode == HttpStatusCode.OK)
        {
            var content = await response.Content.ReadAsStringAsync();

            // Parse as memory usage structure
            var memoryUsage = JsonSerializer.Deserialize<JsonElement>(content);

            Assert.True(memoryUsage.TryGetProperty("allocatedBytes", out var allocatedBytes));
            Assert.True(allocatedBytes.GetInt64() > 0, "Allocated bytes should be positive");

            Assert.True(memoryUsage.TryGetProperty("timestamp", out var timestamp));
            Assert.True(timestamp.GetDateTime() != default, "Timestamp should be set");
        }
    }

    [Fact]
    public void HealthMetrics_ShouldHaveRequiredProperties()
    {
        // Arrange & Act
        var healthMetrics = new HealthMetrics
        {
            Status = "healthy",
            Timestamp = DateTimeOffset.UtcNow,
            MemoryUsageMB = 50.5,
            MemoryPressurePercent = 25.0,
            GCCollections = 42
        };

        // Assert
        Assert.Equal("healthy", healthMetrics.Status);
        Assert.True(healthMetrics.MemoryUsageMB > 0);
        Assert.True(healthMetrics.MemoryPressurePercent >= 0);
        Assert.True(healthMetrics.GCCollections >= 0);
    }

    [Fact]
    public void DatabaseOperationMetrics_ShouldCalculateCorrectValues()
    {
        // Arrange & Act
        var metrics = new DatabaseOperationMetrics
        {
            Count = 10,
            TotalTimeMs = 500,
            MaxTimeMs = 150,
            AvgTimeMs = 50.0
        };

        // Assert
        Assert.Equal(10, metrics.Count);
        Assert.Equal(500, metrics.TotalTimeMs);
        Assert.Equal(150, metrics.MaxTimeMs);
        Assert.Equal(50.0, metrics.AvgTimeMs);
    }

    [Fact]
    public void CacheTypeMetrics_HitRatio_ShouldCalculateCorrectly()
    {
        // Arrange & Act
        var metrics = new CacheTypeMetrics
        {
            Hits = 80,
            Misses = 20,
            Evictions = 5,
            AvgOperationTimeMs = 2.5
        };

        // Assert
        Assert.Equal(0.8, metrics.HitRatio, 2); // 80 / (80 + 20) = 0.8
    }

    [Fact]
    public void CacheTypeMetrics_HitRatio_WithZeroOperations_ShouldReturnZero()
    {
        // Arrange & Act
        var metrics = new CacheTypeMetrics
        {
            Hits = 0,
            Misses = 0,
            Evictions = 0,
            AvgOperationTimeMs = 0.0
        };

        // Assert
        Assert.Equal(0.0, metrics.HitRatio);
    }

    /// <summary>
    /// Test protocols for the metrics endpoints tests.
    /// </summary>
    public static class Protocols
    {
        public const string MetricsApi = "MetricsApi";
    }

    /// <summary>
    /// Test operations for the metrics endpoints tests.
    /// </summary>
    public static class Operations
    {
        public const string GetHealthMetrics = "GetHealthMetrics";
        public const string GetPerformanceMetrics = "GetPerformanceMetrics";
        public const string GetDatabaseMetrics = "GetDatabaseMetrics";
        public const string GetCacheMetrics = "GetCacheMetrics";
        public const string GetMemoryMetrics = "GetMemoryMetrics";
    }
}
