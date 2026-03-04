// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Proto = Honua.Server.Features.Grpc.Proto.V2;

namespace Honua.Server.Features.Infrastructure.Services;

/// <summary>
/// Production implementation of mobile optimization service.
/// Optimizes queries and results for mobile device constraints including battery, bandwidth, and processing power.
/// </summary>
internal sealed class MobileOptimizationService : IMobileOptimizationService
{
    private readonly ILogger<MobileOptimizationService> _logger;

    // Mobile optimization constants
    private const int DefaultMobileBatchSize = 50; // Smaller batches for mobile
    private const int DefaultDesktopBatchSize = 1000;
    private const int MaxMobileVertices = 1000; // Limit geometry complexity
    private const double MobileToleranceMultiplier = 2.0; // More aggressive simplification
    private const int LowPowerBatchSize = 25; // Even smaller batches for battery conservation

    public MobileOptimizationService(ILogger<MobileOptimizationService> logger)
    {
        _logger = logger;
    }

    public async Task<FeatureQuery> OptimizeQueryAsync(
        FeatureQuery query,
        Proto.MobileOptimizations? mobileOptions)
    {
        if (mobileOptions == null)
            return query;

        using var activity = System.Diagnostics.Activity.Current?.Source.StartActivity("optimize_mobile_query");
        activity?.SetTag("low_power_mode", mobileOptions.LowPowerMode);
        activity?.SetTag("use_compression", mobileOptions.UseCompression);

        var optimizedQuery = query with { };

        // Apply low power mode optimizations
        if (mobileOptions.LowPowerMode)
        {
            optimizedQuery = await ApplyLowPowerOptimizationsAsync(optimizedQuery);
        }

        // Apply field prioritization for progressive loading
        if (mobileOptions.PriorityFields.Any())
        {
            optimizedQuery = optimizedQuery with
            {
                OutFields = OrganizeFieldsByPriority(query.OutFields, mobileOptions.PriorityFields)
            };
        }

        // Apply mobile-specific spatial optimizations
        if (mobileOptions.TargetZoomLevel.HasValue)
        {
            optimizedQuery = await ApplyZoomLevelOptimizationsAsync(optimizedQuery, mobileOptions.TargetZoomLevel.Value);
        }

        // Limit result count for mobile performance
        if (!query.ResultRecordCount.HasValue || query.ResultRecordCount > GetMaxMobileResultCount(mobileOptions))
        {
            optimizedQuery = optimizedQuery with
            {
                ResultRecordCount = GetMaxMobileResultCount(mobileOptions)
            };
        }

        // Apply geometry simplification
        if (mobileOptions.GeometrySimplificationLevel > 0)
        {
            optimizedQuery = await ApplyGeometrySimplificationAsync(optimizedQuery, mobileOptions.GeometrySimplificationLevel);
        }

        _logger.LogDebug("Optimized query for mobile: LowPower={LowPower}, Compression={Compression}, BatchSize={BatchSize}",
            mobileOptions.LowPowerMode, mobileOptions.UseCompression, GetOptimalBatchSize(mobileOptions, 1000));

        return optimizedQuery;
    }

    public async Task<QueryResult<FeatureRecord>> OptimizeResultAsync(
        QueryResult<FeatureRecord> result,
        Proto.MobileOptimizations? mobileOptions)
    {
        if (mobileOptions == null)
            return result;

        using var activity = System.Diagnostics.Activity.Current?.Source.StartActivity("optimize_mobile_result");

        var optimizedFeatures = result.Items.ToList();

        // Apply aggressive caching for mobile
        if (mobileOptions.UseAggressiveCaching)
        {
            optimizedFeatures = await ApplyAggressiveCachingAsync(optimizedFeatures);
        }

        // Prioritize important fields for progressive loading
        if (mobileOptions.PriorityFields.Any())
        {
            optimizedFeatures = await PrioritizeFieldsAsync(optimizedFeatures, mobileOptions.PriorityFields);
        }

        // Apply compression if requested
        if (mobileOptions.UseCompression)
        {
            optimizedFeatures = await ApplyResultCompressionAsync(optimizedFeatures);
        }

        return result with { Items = optimizedFeatures };
    }

    public int CalculateSimplificationLevel(
        Proto.LevelOfDetail? levelOfDetail,
        Proto.MobileOptimizations? mobileOptions)
    {
        var baseLevel = levelOfDetail?.Level ?? 0;

        // Increase simplification for mobile
        if (mobileOptions != null)
        {
            if (mobileOptions.LowPowerMode)
                baseLevel = Math.Max(baseLevel, 7); // Aggressive simplification for battery

            if (mobileOptions.UseCompression)
                baseLevel = Math.Max(baseLevel, 5); // Moderate simplification for bandwidth

            if (mobileOptions.TargetZoomLevel.HasValue)
            {
                // Higher zoom levels need less detail
                var zoomBasedLevel = Math.Max(0, 10 - mobileOptions.TargetZoomLevel.Value);
                baseLevel = Math.Max(baseLevel, zoomBasedLevel);
            }
        }

        return Math.Min(baseLevel, 10); // Cap at maximum simplification
    }

    public int GetOptimalBatchSize(Proto.MobileOptimizations? mobileOptions, int defaultBatchSize)
    {
        if (mobileOptions == null)
            return defaultBatchSize;

        var batchSize = defaultBatchSize;

        // Reduce batch size for mobile constraints
        if (mobileOptions.LowPowerMode)
        {
            batchSize = Math.Min(batchSize, LowPowerBatchSize);
        }
        else
        {
            batchSize = Math.Min(batchSize, DefaultMobileBatchSize);
        }

        // Adjust based on compression - can handle slightly larger batches if compressed
        if (mobileOptions.UseCompression)
        {
            batchSize = Math.Min((int)(batchSize * 1.5), defaultBatchSize);
        }

        // Minimum batch size for efficiency
        return Math.Max(batchSize, 10);
    }

    public IEnumerable<string> GetProgressiveFieldOrder(
        IEnumerable<FieldDefinition> layerFields,
        IEnumerable<string>? priorityFields)
    {
        var priorityList = priorityFields?.ToList() ?? new List<string>();
        var allFields = layerFields.Select(f => f.Name).ToList();

        // Start with priority fields
        foreach (var field in priorityList.Where(allFields.Contains))
        {
            yield return field;
        }

        // Add essential fields (ID, geometry type indicators)
        var essentialFields = allFields.Where(f =>
            f.Equals("OBJECTID", StringComparison.OrdinalIgnoreCase) ||
            f.Equals("ID", StringComparison.OrdinalIgnoreCase) ||
            f.Equals("FID", StringComparison.OrdinalIgnoreCase) ||
            f.Equals("SHAPE", StringComparison.OrdinalIgnoreCase))
            .Except(priorityList, StringComparer.OrdinalIgnoreCase);

        foreach (var field in essentialFields)
        {
            yield return field;
        }

        // Add remaining fields ordered by importance
        var remainingFields = allFields
            .Except(priorityList, StringComparer.OrdinalIgnoreCase)
            .Except(essentialFields, StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => GetFieldImportanceScore(f, layerFields));

        foreach (var field in remainingFields)
        {
            yield return field;
        }
    }

    public MobileResourceEstimate EstimateResourceConsumption(
        FeatureQuery query,
        LayerDefinition layerDefinition)
    {
        var featureCount = EstimateFeatureCount(query, layerDefinition);
        var avgFeatureSize = EstimateAverageFeatureSize(layerDefinition);
        var geometryComplexity = EstimateGeometryComplexity(layerDefinition.GeometryType);

        var bandwidthBytes = featureCount * avgFeatureSize;
        var processingTimeMs = (int)(featureCount * 0.1 * geometryComplexity); // 0.1ms per feature * complexity
        var batteryUsage = (bandwidthBytes / 1_000_000.0 * 0.5) + (processingTimeMs / 1000.0 * 0.2); // Rough estimate
        var memoryUsageMB = featureCount * avgFeatureSize / 1_000_000.0;

        var recommendations = new List<string>();
        if (bandwidthBytes > 5_000_000)
            recommendations.Add("Consider using geometry simplification");
        if (featureCount > 1000)
            recommendations.Add("Consider using spatial filtering to reduce feature count");
        if (batteryUsage > 10)
            recommendations.Add("Enable low power mode for better battery efficiency");
        if (memoryUsageMB > 50)
            recommendations.Add("Consider using streaming query instead of full result");

        return new MobileResourceEstimate
        {
            EstimatedBandwidthBytes = bandwidthBytes,
            EstimatedBatteryUsagePercent = batteryUsage,
            EstimatedProcessingTimeMs = processingTimeMs,
            EstimatedMemoryUsageMB = memoryUsageMB,
            RecommendedOptimizations = recommendations
        };
    }

    #region Private Helper Methods

    private async Task<FeatureQuery> ApplyLowPowerOptimizationsAsync(FeatureQuery query)
    {
        // Reduce processing overhead for battery conservation
        var optimized = query with
        {
            // Limit expensive operations
            ReturnDistinctValues = false,

            // Prioritize simple queries
            OrderByFields = query.OrderByFields?.Take(1), // Limit sorting complexity

            // Reduce geometry precision
            GeometryPrecision = Math.Min(query.GeometryPrecision ?? 6, 3)
        };

        return optimized;
    }

    private async Task<FeatureQuery> ApplyZoomLevelOptimizationsAsync(FeatureQuery query, int zoomLevel)
    {
        // Adjust detail based on zoom level
        var simplificationTolerance = CalculateToleranceForZoomLevel(zoomLevel);

        return query with
        {
            MaxAllowableOffset = Math.Max(query.MaxAllowableOffset ?? 0, simplificationTolerance)
        };
    }

    private async Task<FeatureQuery> ApplyGeometrySimplificationAsync(FeatureQuery query, int simplificationLevel)
    {
        var tolerance = CalculateSimplificationTolerance(simplificationLevel);

        return query with
        {
            MaxAllowableOffset = Math.Max(query.MaxAllowableOffset ?? 0, tolerance)
        };
    }

    private static IEnumerable<string>? OrganizeFieldsByPriority(
        IEnumerable<string>? originalFields,
        IEnumerable<string> priorityFields)
    {
        if (originalFields == null)
            return null;

        var fields = originalFields.ToList();
        var priority = priorityFields.ToList();

        // Reorder to put priority fields first
        var reordered = new List<string>();

        // Add priority fields first
        reordered.AddRange(priority.Where(fields.Contains));

        // Add remaining fields
        reordered.AddRange(fields.Except(priority, StringComparer.OrdinalIgnoreCase));

        return reordered;
    }

    private static int GetMaxMobileResultCount(Proto.MobileOptimizations mobileOptions)
    {
        return mobileOptions.LowPowerMode ? 100 : 500;
    }

    private static double CalculateToleranceForZoomLevel(int zoomLevel)
    {
        // More tolerance (less detail) at lower zoom levels
        return Math.Pow(2, Math.Max(0, 18 - zoomLevel)) * 0.1;
    }

    private static double CalculateSimplificationTolerance(int simplificationLevel)
    {
        // Linear progression from 0.1 to 10.0 based on level (0-10)
        return 0.1 + (simplificationLevel * 0.99);
    }

    private async Task<List<FeatureRecord>> ApplyAggressiveCachingAsync(List<FeatureRecord> features)
    {
        // Add cache metadata to features for client-side caching
        return features.Select(f => f with
        {
            // Add cache hints in metadata or attributes
            Attributes = f.Attributes.Concat(new[]
            {
                new KeyValuePair<string, object?>("_cache_ttl", TimeSpan.FromHours(24).TotalSeconds),
                new KeyValuePair<string, object?>("_cache_version", DateTime.UtcNow.Ticks)
            }).ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
        }).ToList();
    }

    private async Task<List<FeatureRecord>> PrioritizeFieldsAsync(
        List<FeatureRecord> features,
        IEnumerable<string> priorityFields)
    {
        var priority = priorityFields.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Reorder attributes to put priority fields first
        return features.Select(f => f with
        {
            Attributes = f.Attributes
                .OrderBy(kvp => priority.Contains(kvp.Key) ? 0 : 1)
                .ThenBy(kvp => kvp.Key)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
        }).ToList();
    }

    private async Task<List<FeatureRecord>> ApplyResultCompressionAsync(List<FeatureRecord> features)
    {
        // Apply result-level optimizations for compression
        // This could include string interning, value deduplication, etc.

        // For now, just return as-is - compression happens at transport layer
        return features;
    }

    private static int GetFieldImportanceScore(string fieldName, IEnumerable<FieldDefinition> layerFields)
    {
        var field = layerFields.FirstOrDefault(f =>
            f.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase));

        if (field == null) return 100;

        // Score based on field characteristics
        var score = 50;

        // Essential fields get higher priority (lower score)
        if (fieldName.Contains("ID", StringComparison.OrdinalIgnoreCase)) score -= 20;
        if (fieldName.Contains("NAME", StringComparison.OrdinalIgnoreCase)) score -= 15;
        if (fieldName.Contains("TYPE", StringComparison.OrdinalIgnoreCase)) score -= 10;
        if (fieldName.Contains("STATUS", StringComparison.OrdinalIgnoreCase)) score -= 10;

        // Frequently used types get priority
        if (field.Type == FieldType.String) score -= 5;
        if (field.Type == FieldType.Integer) score -= 5;

        // Large fields get lower priority (higher score)
        if (field.Type == FieldType.Blob) score += 30;
        if (field.Type == FieldType.Geometry) score += 20;

        return Math.Max(0, score);
    }

    private static long EstimateFeatureCount(FeatureQuery query, LayerDefinition layerDefinition)
    {
        // Rough estimation based on query constraints
        var baseCount = 10000L; // Assume moderate layer size

        // Apply query filters to estimate
        if (query.SpatialFilter.HasValue)
            baseCount = (long)(baseCount * 0.1); // Spatial filter reduces significantly

        if (!string.IsNullOrEmpty(query.WhereClause))
            baseCount = (long)(baseCount * 0.3); // Attribute filter reduces moderately

        if (query.ObjectIds?.Any() == true)
            baseCount = query.ObjectIds.Count(); // Direct ID query

        // Apply result limits
        if (query.ResultRecordCount.HasValue)
            baseCount = Math.Min(baseCount, query.ResultRecordCount.Value);

        return Math.Max(1, baseCount);
    }

    private static long EstimateAverageFeatureSize(LayerDefinition layerDefinition)
    {
        // Rough estimation based on layer characteristics
        var size = 1000L; // Base feature size in bytes

        // Adjust for geometry complexity
        size += layerDefinition.GeometryType switch
        {
            GeometryType.Point => 50,
            GeometryType.MultiPoint => 200,
            GeometryType.Linestring => 500,
            GeometryType.MultiLinestring => 1000,
            GeometryType.Polygon => 2000,
            GeometryType.MultiPolygon => 5000,
            _ => 1000
        };

        // Adjust for attribute count and types
        var attributeSize = layerDefinition.AttributeFields.Sum(f => f.Type switch
        {
            FieldType.String => f.Length ?? 255,
            FieldType.Integer => 8,
            FieldType.Double => 8,
            FieldType.Date => 16,
            FieldType.Blob => 1000, // Assume moderate blob size
            _ => 50
        });

        return size + attributeSize;
    }

    private static double EstimateGeometryComplexity(GeometryType geometryType)
    {
        return geometryType switch
        {
            GeometryType.Point => 1.0,
            GeometryType.MultiPoint => 1.5,
            GeometryType.Linestring => 2.0,
            GeometryType.MultiLinestring => 3.0,
            GeometryType.Polygon => 4.0,
            GeometryType.MultiPolygon => 5.0,
            _ => 2.0
        };
    }

    #endregion
}