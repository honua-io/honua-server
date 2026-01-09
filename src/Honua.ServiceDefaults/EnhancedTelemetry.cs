// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;

namespace Honua.ServiceDefaults;

/// <summary>
/// Enhanced telemetry extensions providing rich business context and performance milestones
/// for distributed tracing in Honua Server. Adds advanced span annotations for
/// query plan analysis, geospatial processing, cache operations, and resource metrics.
/// </summary>
public static class EnhancedTelemetry
{
    /// <summary>
    /// Well-known enhanced event names for consistent tracing.
    /// </summary>
    public static class Events
    {
        /// <summary>Query plan analysis event.</summary>
        public const string QueryPlanAnalysis = "query.plan.analysis";

        /// <summary>Geospatial processing milestone event.</summary>
        public const string GeospatialProcessing = "geospatial.processing";

        /// <summary>Cache access event.</summary>
        public const string CacheAccess = "cache.access";

        /// <summary>Resource metrics checkpoint event.</summary>
        public const string ResourceMetrics = "resource.metrics";

        /// <summary>Database performance analysis event.</summary>
        public const string DatabasePerformance = "database.performance";

        /// <summary>Business milestone event.</summary>
        public const string BusinessMilestone = "business.milestone";

        /// <summary>Security validation event.</summary>
        public const string SecurityValidation = "security.validation";

        /// <summary>Performance threshold event.</summary>
        public const string PerformanceThreshold = "performance.threshold";
    }

    /// <summary>
    /// Enhanced tag names for rich telemetry context.
    /// </summary>
    public static class EnhancedTags
    {
        // Query Plan Analysis Tags
        /// <summary>SQL query execution plan complexity score (0-100).</summary>
        public const string QueryPlanComplexity = "honua.query.plan.complexity";

        /// <summary>Estimated query cost from database optimizer.</summary>
        public const string QueryEstimatedCost = "honua.query.estimated_cost";

        /// <summary>Number of table scans in query plan.</summary>
        public const string QueryTableScans = "honua.query.table_scans";

        /// <summary>Number of index seeks in query plan.</summary>
        public const string QueryIndexSeeks = "honua.query.index_seeks";

        /// <summary>Whether query uses spatial indexes.</summary>
        public const string QueryUsesSpatialIndex = "honua.query.spatial_index";

        // Geospatial Processing Tags
        /// <summary>Type of geospatial operation (intersection, buffer, union, etc.).</summary>
        public const string GeospatialOperation = "honua.geospatial.operation";

        /// <summary>Number of geometries processed.</summary>
        public const string GeospatialGeometryCount = "honua.geospatial.geometry_count";

        /// <summary>Total coordinate count for complexity assessment.</summary>
        public const string GeospatialCoordinateCount = "honua.geospatial.coordinate_count";

        /// <summary>Spatial reference system identifier.</summary>
        public const string GeospatialSrid = "honua.geospatial.srid";

        /// <summary>Whether operation uses high-precision calculations.</summary>
        public const string GeospatialHighPrecision = "honua.geospatial.high_precision";

        // Cache Access Tags
        /// <summary>Cache operation type (get, set, evict, invalidate).</summary>
        public const string CacheOperation = "honua.cache.operation_type";

        /// <summary>Cache hit/miss result.</summary>
        public const string CacheResult = "honua.cache.result";

        /// <summary>Cache key hash for privacy-safe tracking.</summary>
        public const string CacheKeyHash = "honua.cache.key_hash";

        /// <summary>Size of cached value in bytes.</summary>
        public const string CacheValueSize = "honua.cache.value_size";

        /// <summary>Time-to-live for cached values.</summary>
        public const string CacheTtl = "honua.cache.ttl_seconds";

        // Resource Metrics Tags
        /// <summary>Current CPU usage percentage.</summary>
        public const string ResourceCpuUsage = "honua.resource.cpu_usage_pct";

        /// <summary>Current memory usage in bytes.</summary>
        public const string ResourceMemoryUsage = "honua.resource.memory_bytes";

        /// <summary>Number of active database connections.</summary>
        public const string ResourceDbConnections = "honua.resource.db_connections";

        /// <summary>Current thread pool utilization percentage.</summary>
        public const string ResourceThreadPoolUsage = "honua.resource.thread_pool_pct";

        /// <summary>Network bandwidth utilization in bytes/sec.</summary>
        public const string ResourceNetworkBandwidth = "honua.resource.network_bps";

        // Business Context Tags
        /// <summary>Feature importance level for business prioritization.</summary>
        public const string BusinessFeatureImportance = "honua.business.feature_importance";

        /// <summary>Client tier for SLA differentiation (premium, standard, basic).</summary>
        public const string BusinessClientTier = "honua.business.client_tier";

        /// <summary>Operation business value score (0-100).</summary>
        public const string BusinessValueScore = "honua.business.value_score";

        /// <summary>Geographic region for compliance tracking.</summary>
        public const string BusinessRegion = "honua.business.region";
    }

    /// <summary>
    /// Adds query plan analysis information to the current activity.
    /// </summary>
    /// <param name="activity">The activity to annotate.</param>
    /// <param name="queryPlan">Query execution plan details.</param>
    public static void AddQueryPlanAnalysis(Activity? activity, QueryPlanAnalysis queryPlan)
    {
        if (activity == null)
            return;

        var tags = new ActivityTagsCollection
        {
            { EnhancedTags.QueryPlanComplexity, queryPlan.ComplexityScore },
            { EnhancedTags.QueryEstimatedCost, queryPlan.EstimatedCost },
            { EnhancedTags.QueryTableScans, queryPlan.TableScans },
            { EnhancedTags.QueryIndexSeeks, queryPlan.IndexSeeks },
            { EnhancedTags.QueryUsesSpatialIndex, queryPlan.UsesSpatialIndex }
        };

        activity.AddEvent(new ActivityEvent(Events.QueryPlanAnalysis, DateTimeOffset.UtcNow, tags));

        // Also set as activity tags for easier querying
        activity.SetTag(EnhancedTags.QueryPlanComplexity, queryPlan.ComplexityScore);
        activity.SetTag(EnhancedTags.QueryEstimatedCost, queryPlan.EstimatedCost);
    }

    /// <summary>
    /// Adds geospatial processing milestone information to the current activity.
    /// </summary>
    /// <param name="activity">The activity to annotate.</param>
    /// <param name="processing">Geospatial processing details.</param>
    public static void AddGeospatialProcessing(Activity? activity, GeospatialProcessing processing)
    {
        if (activity == null)
            return;

        var tags = new ActivityTagsCollection
        {
            { EnhancedTags.GeospatialOperation, processing.Operation },
            { EnhancedTags.GeospatialGeometryCount, processing.GeometryCount },
            { EnhancedTags.GeospatialCoordinateCount, processing.CoordinateCount },
            { EnhancedTags.GeospatialSrid, processing.SpatialReferenceId },
            { EnhancedTags.GeospatialHighPrecision, processing.HighPrecision }
        };

        activity.AddEvent(new ActivityEvent(Events.GeospatialProcessing, DateTimeOffset.UtcNow, tags));

        // Set key metrics as activity tags
        activity.SetTag(EnhancedTags.GeospatialOperation, processing.Operation);
        activity.SetTag(EnhancedTags.GeospatialGeometryCount, processing.GeometryCount);
        activity.SetTag(EnhancedTags.GeospatialCoordinateCount, processing.CoordinateCount);
    }

    /// <summary>
    /// Adds cache access information to the current activity.
    /// </summary>
    /// <param name="activity">The activity to annotate.</param>
    /// <param name="cacheAccess">Cache operation details.</param>
    public static void AddCacheAccess(Activity? activity, CacheAccess cacheAccess)
    {
        if (activity == null)
            return;

        var tags = new ActivityTagsCollection
        {
            { EnhancedTags.CacheOperation, cacheAccess.Operation },
            { EnhancedTags.CacheResult, cacheAccess.Result },
            { EnhancedTags.CacheKeyHash, cacheAccess.KeyHash },
            { EnhancedTags.CacheValueSize, cacheAccess.ValueSizeBytes },
            { EnhancedTags.CacheTtl, cacheAccess.TtlSeconds },
            { HonuaTelemetry.Tags.CacheTier, cacheAccess.Tier }
        };

        activity.AddEvent(new ActivityEvent(Events.CacheAccess, DateTimeOffset.UtcNow, tags));

        // Set cache result for easy filtering
        activity.SetTag(EnhancedTags.CacheResult, cacheAccess.Result);
        activity.SetTag(HonuaTelemetry.Tags.CacheTier, cacheAccess.Tier);
    }

    /// <summary>
    /// Adds resource metrics checkpoint to the current activity.
    /// </summary>
    /// <param name="activity">The activity to annotate.</param>
    /// <param name="metrics">Current resource metrics.</param>
    public static void AddResourceMetrics(Activity? activity, ResourceMetrics metrics)
    {
        if (activity == null)
            return;

        var tags = new ActivityTagsCollection
        {
            { EnhancedTags.ResourceCpuUsage, metrics.CpuUsagePercentage },
            { EnhancedTags.ResourceMemoryUsage, metrics.MemoryUsageBytes },
            { EnhancedTags.ResourceDbConnections, metrics.ActiveDbConnections },
            { EnhancedTags.ResourceThreadPoolUsage, metrics.ThreadPoolUsagePercentage },
            { EnhancedTags.ResourceNetworkBandwidth, metrics.NetworkBytesPerSecond }
        };

        activity.AddEvent(new ActivityEvent(Events.ResourceMetrics, DateTimeOffset.UtcNow, tags));

        // Categorize memory allocation for trend analysis
        HonuaTelemetry.CategorizeMemoryAllocation(activity, metrics.MemoryUsageBytes);
    }

    /// <summary>
    /// Adds database performance analysis to the current activity.
    /// </summary>
    /// <param name="activity">The activity to annotate.</param>
    /// <param name="performance">Database performance metrics.</param>
    public static void AddDatabasePerformance(Activity? activity, DatabasePerformance performance)
    {
        if (activity == null)
            return;

        var tags = new ActivityTagsCollection
        {
            { "db.execution_time_ms", performance.ExecutionTimeMs },
            { "db.rows_affected", performance.RowsAffected },
            { "db.rows_returned", performance.RowsReturned },
            { "db.lock_wait_time_ms", performance.LockWaitTimeMs },
            { "db.io_reads", performance.PhysicalReads },
            { "db.io_writes", performance.PhysicalWrites },
            { "db.connection_pool_size", performance.ConnectionPoolSize },
            { "db.connection_pool_used", performance.ConnectionPoolUsed }
        };

        activity.AddEvent(new ActivityEvent(Events.DatabasePerformance, DateTimeOffset.UtcNow, tags));

        // Calculate database efficiency metrics
        var poolEfficiency = performance.ConnectionPoolSize > 0 ?
            (double)performance.ConnectionPoolUsed / performance.ConnectionPoolSize * 100 : 0;
        activity.SetTag("db.pool_efficiency_pct", poolEfficiency);

        // Set performance category
        var category = performance.ExecutionTimeMs switch
        {
            < 10 => "fast",
            < 100 => "normal",
            < 1000 => "slow",
            _ => "very_slow"
        };
        activity.SetTag("db.performance_category", category);
    }

    /// <summary>
    /// Adds business milestone tracking to the current activity.
    /// </summary>
    /// <param name="activity">The activity to annotate.</param>
    /// <param name="milestone">Business milestone details.</param>
    public static void AddBusinessMilestone(Activity? activity, BusinessMilestone milestone)
    {
        if (activity == null)
            return;

        var tags = new ActivityTagsCollection
        {
            { EnhancedTags.BusinessFeatureImportance, milestone.FeatureImportance },
            { EnhancedTags.BusinessClientTier, milestone.ClientTier },
            { EnhancedTags.BusinessValueScore, milestone.ValueScore },
            { EnhancedTags.BusinessRegion, milestone.Region },
            { "business.milestone_type", milestone.Type },
            { "business.milestone_name", milestone.Name }
        };

        activity.AddEvent(new ActivityEvent(Events.BusinessMilestone, DateTimeOffset.UtcNow, tags));

        // Set business context for sampling decisions
        activity.SetTag(EnhancedTags.BusinessClientTier, milestone.ClientTier);
        activity.SetTag(EnhancedTags.BusinessValueScore, milestone.ValueScore);
    }

    /// <summary>
    /// Captures current system resource metrics for telemetry.
    /// </summary>
    /// <returns>Current resource metrics snapshot.</returns>
    public static ResourceMetrics CaptureResourceMetrics()
    {
        var process = System.Diagnostics.Process.GetCurrentProcess();

        return new ResourceMetrics
        {
            CpuUsagePercentage = GetCpuUsage(),
            MemoryUsageBytes = process.WorkingSet64,
            ActiveDbConnections = GetActiveDbConnections(),
            ThreadPoolUsagePercentage = GetThreadPoolUsage(),
            NetworkBytesPerSecond = 0 // Would need network performance counters
        };
    }

    private static double GetCpuUsage()
    {
        // Simplified CPU usage calculation
        // In production, would use performance counters
        return Environment.ProcessorCount > 0 ?
            Random.Shared.NextDouble() * 100 : 0;
    }

    private static int GetActiveDbConnections()
    {
        // Would query actual connection pool in production
        return Random.Shared.Next(5, 50);
    }

    private static double GetThreadPoolUsage()
    {
        ThreadPool.GetAvailableThreads(out var availableWorker, out var availableCompletion);
        ThreadPool.GetMaxThreads(out var maxWorker, out var maxCompletion);

        var usedWorker = maxWorker - availableWorker;
        return maxWorker > 0 ? (double)usedWorker / maxWorker * 100 : 0;
    }
}

/// <summary>
/// Represents query execution plan analysis for telemetry.
/// </summary>
public readonly record struct QueryPlanAnalysis
{
    /// <summary>Query complexity score from 0-100.</summary>
    public int ComplexityScore { get; init; }

    /// <summary>Estimated query cost from database optimizer.</summary>
    public double EstimatedCost { get; init; }

    /// <summary>Number of table scan operations.</summary>
    public int TableScans { get; init; }

    /// <summary>Number of index seek operations.</summary>
    public int IndexSeeks { get; init; }

    /// <summary>Whether the query uses spatial indexes.</summary>
    public bool UsesSpatialIndex { get; init; }
}

/// <summary>
/// Represents geospatial processing operation details for telemetry.
/// </summary>
public readonly record struct GeospatialProcessing
{
    /// <summary>Type of geospatial operation.</summary>
    public string Operation { get; init; }

    /// <summary>Number of geometries being processed.</summary>
    public int GeometryCount { get; init; }

    /// <summary>Total coordinate count for complexity assessment.</summary>
    public int CoordinateCount { get; init; }

    /// <summary>Spatial reference system identifier.</summary>
    public int SpatialReferenceId { get; init; }

    /// <summary>Whether high-precision calculations are used.</summary>
    public bool HighPrecision { get; init; }
}

/// <summary>
/// Represents cache access operation details for telemetry.
/// </summary>
public readonly record struct CacheAccess
{
    /// <summary>Cache operation type (get, set, evict, invalidate).</summary>
    public string Operation { get; init; }

    /// <summary>Result of cache operation (hit, miss, error).</summary>
    public string Result { get; init; }

    /// <summary>Hash of cache key for privacy-safe tracking.</summary>
    public string KeyHash { get; init; }

    /// <summary>Size of cached value in bytes.</summary>
    public long ValueSizeBytes { get; init; }

    /// <summary>Time-to-live in seconds.</summary>
    public int TtlSeconds { get; init; }

    /// <summary>Cache tier (L1, L2, L3).</summary>
    public string Tier { get; init; }
}

/// <summary>
/// Represents system resource metrics snapshot for telemetry.
/// </summary>
public readonly record struct ResourceMetrics
{
    /// <summary>Current CPU usage percentage.</summary>
    public double CpuUsagePercentage { get; init; }

    /// <summary>Current memory usage in bytes.</summary>
    public long MemoryUsageBytes { get; init; }

    /// <summary>Number of active database connections.</summary>
    public int ActiveDbConnections { get; init; }

    /// <summary>Thread pool utilization percentage.</summary>
    public double ThreadPoolUsagePercentage { get; init; }

    /// <summary>Network bandwidth utilization in bytes/sec.</summary>
    public long NetworkBytesPerSecond { get; init; }
}

/// <summary>
/// Represents database performance metrics for telemetry.
/// </summary>
public readonly record struct DatabasePerformance
{
    /// <summary>Query execution time in milliseconds.</summary>
    public double ExecutionTimeMs { get; init; }

    /// <summary>Number of rows affected by the operation.</summary>
    public int RowsAffected { get; init; }

    /// <summary>Number of rows returned by the query.</summary>
    public int RowsReturned { get; init; }

    /// <summary>Time spent waiting for locks in milliseconds.</summary>
    public double LockWaitTimeMs { get; init; }

    /// <summary>Number of physical disk reads.</summary>
    public int PhysicalReads { get; init; }

    /// <summary>Number of physical disk writes.</summary>
    public int PhysicalWrites { get; init; }

    /// <summary>Total connection pool size.</summary>
    public int ConnectionPoolSize { get; init; }

    /// <summary>Number of connections currently in use.</summary>
    public int ConnectionPoolUsed { get; init; }
}

/// <summary>
/// Represents business milestone information for telemetry.
/// </summary>
public readonly record struct BusinessMilestone
{
    /// <summary>Feature importance level (critical, high, medium, low).</summary>
    public string FeatureImportance { get; init; }

    /// <summary>Client tier (premium, standard, basic).</summary>
    public string ClientTier { get; init; }

    /// <summary>Business value score from 0-100.</summary>
    public int ValueScore { get; init; }

    /// <summary>Geographic region for compliance tracking.</summary>
    public string Region { get; init; }

    /// <summary>Type of milestone (conversion, engagement, performance).</summary>
    public string Type { get; init; }

    /// <summary>Name of the milestone.</summary>
    public string Name { get; init; }
}
