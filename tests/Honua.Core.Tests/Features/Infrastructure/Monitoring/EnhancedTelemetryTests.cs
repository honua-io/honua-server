// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.ServiceDefaults;

namespace Honua.Core.Tests.Features.Infrastructure.Monitoring;

/// <summary>
/// Tests for enhanced telemetry functionality including advanced span annotations
/// and telemetry data structures.
/// </summary>
public class EnhancedTelemetryTests
{
    private sealed class ActivityScope : IDisposable
    {
        private readonly ActivitySource _activitySource;
        private readonly ActivityListener _listener;

        public ActivityScope(string name)
        {
            _activitySource = new ActivitySource("test");
            _listener = new ActivityListener
            {
                ShouldListenTo = _ => true,
                Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData
            };
            ActivitySource.AddActivityListener(_listener);

            Activity = _activitySource.StartActivity(name)
                ?? throw new InvalidOperationException("Activity was not created.");
        }

        public Activity Activity { get; }

        public void Dispose()
        {
            Activity.Dispose();
            _activitySource.Dispose();
            _listener.Dispose();
        }
    }

    private static ActivityScope StartActivityScope(string name = "test-operation") => new(name);

    [Fact]
    public void AddQueryPlanAnalysis_WithValidActivity_AddsExpectedTags()
    {
        // Arrange
        using var activitySource = new ActivitySource("test");
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = activitySource.StartActivity("test-operation");

        var queryPlan = new QueryPlanAnalysis
        {
            ComplexityScore = 75,
            EstimatedCost = 1250.5,
            TableScans = 2,
            IndexSeeks = 3,
            UsesSpatialIndex = true
        };

        // Act
        EnhancedTelemetry.AddQueryPlanAnalysis(activity, queryPlan);

        // Assert
        Assert.NotNull(activity);
        Assert.Equal("75", activity.GetTagItem(EnhancedTelemetry.EnhancedTags.QueryPlanComplexity)?.ToString());
        Assert.Equal("1250.5", activity.GetTagItem(EnhancedTelemetry.EnhancedTags.QueryEstimatedCost)?.ToString());

        // Verify event was added
        var events = activity.Events.ToList();
        Assert.Single(events);
        Assert.Equal(EnhancedTelemetry.Events.QueryPlanAnalysis, events[0].Name);

        var eventTags = events[0].Tags.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        Assert.Equal(75, eventTags[EnhancedTelemetry.EnhancedTags.QueryPlanComplexity]);
        Assert.Equal(1250.5, eventTags[EnhancedTelemetry.EnhancedTags.QueryEstimatedCost]);
        Assert.Equal(2, eventTags[EnhancedTelemetry.EnhancedTags.QueryTableScans]);
        Assert.Equal(3, eventTags[EnhancedTelemetry.EnhancedTags.QueryIndexSeeks]);
        Assert.True((bool?)eventTags[EnhancedTelemetry.EnhancedTags.QueryUsesSpatialIndex]);
    }

    [Fact]
    public void AddQueryPlanAnalysis_WithNullActivity_DoesNotThrow()
    {
        // Arrange
        var queryPlan = new QueryPlanAnalysis
        {
            ComplexityScore = 50,
            EstimatedCost = 100.0,
            TableScans = 1,
            IndexSeeks = 1,
            UsesSpatialIndex = false
        };

        // Act & Assert - Should not throw
        EnhancedTelemetry.AddQueryPlanAnalysis(null, queryPlan);
    }

    [Fact]
    public void AddGeospatialProcessing_WithValidActivity_AddsExpectedTags()
    {
        // Arrange
        using var scope = StartActivityScope();
        var activity = scope.Activity;

        var processing = new GeospatialProcessing
        {
            Operation = "buffer",
            GeometryCount = 500,
            CoordinateCount = 15000,
            SpatialReferenceId = 4326,
            HighPrecision = true
        };

        // Act
        EnhancedTelemetry.AddGeospatialProcessing(activity, processing);

        // Assert
        Assert.NotNull(activity);
        Assert.Equal("buffer", activity.GetTagItem(EnhancedTelemetry.EnhancedTags.GeospatialOperation)?.ToString());
        Assert.Equal("500", activity.GetTagItem(EnhancedTelemetry.EnhancedTags.GeospatialGeometryCount)?.ToString());
        Assert.Equal("15000", activity.GetTagItem(EnhancedTelemetry.EnhancedTags.GeospatialCoordinateCount)?.ToString());

        // Verify event was added
        var events = activity.Events.ToList();
        Assert.Single(events);
        Assert.Equal(EnhancedTelemetry.Events.GeospatialProcessing, events[0].Name);

        var eventTags = events[0].Tags.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        Assert.Equal("buffer", eventTags[EnhancedTelemetry.EnhancedTags.GeospatialOperation]);
        Assert.Equal(500, eventTags[EnhancedTelemetry.EnhancedTags.GeospatialGeometryCount]);
        Assert.Equal(15000, eventTags[EnhancedTelemetry.EnhancedTags.GeospatialCoordinateCount]);
        Assert.Equal(4326, eventTags[EnhancedTelemetry.EnhancedTags.GeospatialSrid]);
        Assert.True((bool?)eventTags[EnhancedTelemetry.EnhancedTags.GeospatialHighPrecision]);
    }

    [Fact]
    public void AddCacheAccess_WithValidActivity_AddsExpectedTags()
    {
        // Arrange
        using var scope = StartActivityScope();
        var activity = scope.Activity;

        var cacheAccess = new CacheAccess
        {
            Operation = "get",
            Result = "hit",
            KeyHash = "abc123",
            ValueSizeBytes = 4096,
            TtlSeconds = 300,
            Tier = "L1"
        };

        // Act
        EnhancedTelemetry.AddCacheAccess(activity, cacheAccess);

        // Assert
        Assert.NotNull(activity);
        Assert.Equal("hit", activity.GetTagItem(EnhancedTelemetry.EnhancedTags.CacheResult)?.ToString());
        Assert.Equal("L1", activity.GetTagItem(HonuaTelemetry.Tags.CacheTier)?.ToString());

        // Verify event was added
        var events = activity.Events.ToList();
        Assert.Single(events);
        Assert.Equal(EnhancedTelemetry.Events.CacheAccess, events[0].Name);

        var eventTags = events[0].Tags.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        Assert.Equal("get", eventTags[EnhancedTelemetry.EnhancedTags.CacheOperation]);
        Assert.Equal("hit", eventTags[EnhancedTelemetry.EnhancedTags.CacheResult]);
        Assert.Equal("abc123", eventTags[EnhancedTelemetry.EnhancedTags.CacheKeyHash]);
        Assert.Equal(4096L, eventTags[EnhancedTelemetry.EnhancedTags.CacheValueSize]);
        Assert.Equal(300, eventTags[EnhancedTelemetry.EnhancedTags.CacheTtl]);
    }

    [Fact]
    public void AddResourceMetrics_WithValidActivity_AddsExpectedTags()
    {
        // Arrange
        using var scope = StartActivityScope();
        var activity = scope.Activity;

        var metrics = new ResourceMetrics
        {
            CpuUsagePercentage = 65.5,
            MemoryUsageBytes = 1073741824, // 1GB
            ActiveDbConnections = 15,
            ThreadPoolUsagePercentage = 40.0,
            NetworkBytesPerSecond = 1048576 // 1MB/s
        };

        // Act
        EnhancedTelemetry.AddResourceMetrics(activity, metrics);

        // Assert
        Assert.NotNull(activity);

        // Memory categorization should be added
        var memoryCategory = activity.GetTagItem(HonuaTelemetry.Tags.MemoryCategory)?.ToString();
        Assert.Equal("xlarge", memoryCategory); // 1GB should be xlarge

        // Verify event was added
        var events = activity.Events.ToList();
        Assert.Single(events);
        Assert.Equal(EnhancedTelemetry.Events.ResourceMetrics, events[0].Name);

        var eventTags = events[0].Tags.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        Assert.Equal(65.5, eventTags[EnhancedTelemetry.EnhancedTags.ResourceCpuUsage]);
        Assert.Equal(1073741824L, eventTags[EnhancedTelemetry.EnhancedTags.ResourceMemoryUsage]);
        Assert.Equal(15, eventTags[EnhancedTelemetry.EnhancedTags.ResourceDbConnections]);
        Assert.Equal(40.0, eventTags[EnhancedTelemetry.EnhancedTags.ResourceThreadPoolUsage]);
        Assert.Equal(1048576L, eventTags[EnhancedTelemetry.EnhancedTags.ResourceNetworkBandwidth]);
    }

    [Fact]
    public void AddDatabasePerformance_WithValidActivity_AddsExpectedTags()
    {
        // Arrange
        using var scope = StartActivityScope();
        var activity = scope.Activity;

        var performance = new DatabasePerformance
        {
            ExecutionTimeMs = 150.5,
            RowsAffected = 10,
            RowsReturned = 1000,
            LockWaitTimeMs = 5.0,
            PhysicalReads = 50,
            PhysicalWrites = 5,
            ConnectionPoolSize = 20,
            ConnectionPoolUsed = 8
        };

        // Act
        EnhancedTelemetry.AddDatabasePerformance(activity, performance);

        // Assert
        Assert.NotNull(activity);

        // Check calculated metrics
        var poolEfficiency = activity.GetTagItem("db.pool_efficiency_pct");
        Assert.Equal(40.0, poolEfficiency); // 8/20 * 100 = 40%

        var performanceCategory = activity.GetTagItem("db.performance_category")?.ToString();
        Assert.Equal("slow", performanceCategory); // 150ms should be "slow"

        // Verify event was added
        var events = activity.Events.ToList();
        Assert.Single(events);
        Assert.Equal(EnhancedTelemetry.Events.DatabasePerformance, events[0].Name);

        var eventTags = events[0].Tags.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        Assert.Equal(150.5, eventTags["db.execution_time_ms"]);
        Assert.Equal(10, eventTags["db.rows_affected"]);
        Assert.Equal(1000, eventTags["db.rows_returned"]);
        Assert.Equal(5.0, eventTags["db.lock_wait_time_ms"]);
        Assert.Equal(50, eventTags["db.io_reads"]);
        Assert.Equal(5, eventTags["db.io_writes"]);
    }

    [Fact]
    public void AddBusinessMilestone_WithValidActivity_AddsExpectedTags()
    {
        // Arrange
        using var scope = StartActivityScope();
        var activity = scope.Activity;

        var milestone = new BusinessMilestone
        {
            FeatureImportance = "high",
            ClientTier = "premium",
            ValueScore = 85,
            Region = "us-west",
            Type = "conversion",
            Name = "feature_query_success"
        };

        // Act
        EnhancedTelemetry.AddBusinessMilestone(activity, milestone);

        // Assert
        Assert.NotNull(activity);
        Assert.Equal("premium", activity.GetTagItem(EnhancedTelemetry.EnhancedTags.BusinessClientTier)?.ToString());
        Assert.Equal("85", activity.GetTagItem(EnhancedTelemetry.EnhancedTags.BusinessValueScore)?.ToString());

        // Verify event was added
        var events = activity.Events.ToList();
        Assert.Single(events);
        Assert.Equal(EnhancedTelemetry.Events.BusinessMilestone, events[0].Name);

        var eventTags = events[0].Tags.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        Assert.Equal("high", eventTags[EnhancedTelemetry.EnhancedTags.BusinessFeatureImportance]);
        Assert.Equal("premium", eventTags[EnhancedTelemetry.EnhancedTags.BusinessClientTier]);
        Assert.Equal(85, eventTags[EnhancedTelemetry.EnhancedTags.BusinessValueScore]);
        Assert.Equal("us-west", eventTags[EnhancedTelemetry.EnhancedTags.BusinessRegion]);
        Assert.Equal("conversion", eventTags["business.milestone_type"]);
        Assert.Equal("feature_query_success", eventTags["business.milestone_name"]);
    }

    [Fact]
    public void CaptureResourceMetrics_ReturnsValidMetrics()
    {
        // Act
        var metrics = EnhancedTelemetry.CaptureResourceMetrics();

        // Assert
        Assert.True(metrics.CpuUsagePercentage >= 0);
        Assert.True(metrics.MemoryUsageBytes > 0);
        Assert.True(metrics.ActiveDbConnections >= 0);
        Assert.True(metrics.ThreadPoolUsagePercentage >= 0);
        Assert.True(metrics.NetworkBytesPerSecond >= 0);
    }

    [Theory]
    [InlineData(1024, "small")]
    [InlineData(512 * 1024, "medium")]
    [InlineData(5 * 1024 * 1024, "large")]
    [InlineData(50 * 1024 * 1024, "xlarge")]
    public void MemoryCategorization_WorksCorrectly(long bytes, string expectedCategory)
    {
        // Arrange
        using var scope = StartActivityScope();
        var activity = scope.Activity;

        // Act
        HonuaTelemetry.CategorizeMemoryAllocation(activity, bytes);

        // Assert
        var category = activity?.GetTagItem(HonuaTelemetry.Tags.MemoryCategory)?.ToString();
        Assert.Equal(expectedCategory, category);
    }

    [Theory]
    [InlineData(5, "fast")]
    [InlineData(50, "normal")]
    [InlineData(500, "slow")]
    [InlineData(5000, "very_slow")]
    public void DatabasePerformanceCategorization_WorksCorrectly(double executionTimeMs, string expectedCategory)
    {
        // Arrange
        using var scope = StartActivityScope();
        var activity = scope.Activity;

        var performance = new DatabasePerformance
        {
            ExecutionTimeMs = executionTimeMs,
            RowsAffected = 1,
            RowsReturned = 1,
            LockWaitTimeMs = 0,
            PhysicalReads = 1,
            PhysicalWrites = 0,
            ConnectionPoolSize = 10,
            ConnectionPoolUsed = 1
        };

        // Act
        EnhancedTelemetry.AddDatabasePerformance(activity, performance);

        // Assert
        var category = activity?.GetTagItem("db.performance_category")?.ToString();
        Assert.Equal(expectedCategory, category);
    }
}
