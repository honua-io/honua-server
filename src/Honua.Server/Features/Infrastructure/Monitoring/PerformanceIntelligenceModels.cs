// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Monitoring;

/// <summary>
/// Comprehensive performance intelligence report containing analysis and recommendations.
/// </summary>
public sealed record PerformanceIntelligenceReport
{
    public DateTimeOffset Timestamp { get; init; }
    public int OverallScore { get; init; }
    public SystemHealthDetails SystemHealth { get; init; } = new();
    public BottleneckAnalysis[] Bottlenecks { get; init; } = Array.Empty<BottleneckAnalysis>();
    public ResourceUtilizationAnalysis ResourceUtilization { get; init; } = new();
    public PerformanceProfile PerformanceProfile { get; init; } = new();
    public OptimizationRecommendation[] Recommendations { get; init; } = Array.Empty<OptimizationRecommendation>();
    public PerformanceTrends Trends { get; init; } = new();
}

/// <summary>
/// Performance snapshot representing system state at a point in time.
/// </summary>
public sealed record PerformanceSnapshot
{
    public DateTimeOffset Timestamp { get; init; }
    public double CpuUsagePercent { get; init; }
    public double MemoryUsageMB { get; init; }
    public double AverageResponseTimeMs { get; init; }
    public double RequestsPerSecond { get; init; }
    public double ErrorRatePercent { get; init; }
}

/// <summary>
/// System health details with component-level health indicators.
/// </summary>
public sealed record SystemHealthDetails
{
    public string Status { get; init; } = string.Empty;
    public string CpuHealth { get; init; } = string.Empty;
    public string MemoryHealth { get; init; } = string.Empty;
    public string DiskHealth { get; init; } = string.Empty;
    public string NetworkHealth { get; init; } = string.Empty;
    public string DatabaseHealth { get; init; } = string.Empty;
}

/// <summary>
/// Bottleneck analysis identifying performance constraints.
/// </summary>
public sealed record BottleneckAnalysis
{
    public string Type { get; init; } = string.Empty;
    public string Severity { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Impact { get; init; } = string.Empty;
    public string[] Recommendations { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Resource utilization analysis across system components.
/// </summary>
public sealed record ResourceUtilizationAnalysis
{
    public double CpuUtilization { get; init; }
    public double MemoryUtilization { get; init; }
    public double DiskUtilization { get; init; }
    public double NetworkUtilization { get; init; }
    public double DatabaseUtilization { get; init; }
    public double CacheUtilization { get; init; }
}

/// <summary>
/// Performance profile describing application characteristics and patterns.
/// </summary>
public sealed record PerformanceProfile
{
    public string ApplicationProfile { get; init; } = string.Empty;
    public string[] WorkloadCharacteristics { get; init; } = Array.Empty<string>();
    public string[] PerformanceCharacteristics { get; init; } = Array.Empty<string>();
    public string[] ScalingRecommendations { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Optimization recommendation with actionable insights.
/// </summary>
public sealed record OptimizationRecommendation
{
    public string Id { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public int Priority { get; init; } // 1-5 scale
    public int ImpactScore { get; init; } // 0-100 scale
    public string ImplementationComplexity { get; init; } = string.Empty;
    public double EstimatedImprovementPercent { get; init; }
    public string[] Actions { get; init; } = Array.Empty<string>();
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Performance trends analysis over time.
/// </summary>
public sealed record PerformanceTrends
{
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset EndTime { get; init; }
    public int DataPoints { get; init; }
    public double CpuTrend { get; init; }
    public double MemoryTrend { get; init; }
    public double ResponseTimeTrend { get; init; }
    public double ThroughputTrend { get; init; }
    public double ErrorRateTrend { get; init; }
}

/// <summary>
/// Memory analysis report with usage patterns and optimization opportunities.
/// </summary>
public sealed record MemoryAnalysisReport
{
    public bool Enabled { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public double CurrentUsageMB { get; init; }
    public double PeakUsageMB { get; init; }
    public double AverageUsageMB { get; init; }
    public double MemoryPressurePercent { get; init; }
    public long GCCollections { get; init; }
    public string[] PotentialLeaks { get; init; } = Array.Empty<string>();
    public string[] OptimizationOpportunities { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Database performance analysis with query and connection metrics.
/// </summary>
public sealed record DatabasePerformanceAnalysis
{
    public bool Enabled { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public double AverageQueryTimeMs { get; init; }
    public int SlowQueriesDetected { get; init; }
    public double ConnectionPoolUtilization { get; init; }
    public double CacheHitRatio { get; init; }
    public int IndexOptimizationOpportunities { get; init; }
    public string[] QueryOptimizationSuggestions { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Performance benchmark comparing current metrics to historical baseline.
/// </summary>
public sealed record PerformanceBenchmark
{
    public DateTimeOffset Timestamp { get; init; }
    public bool HasBaseline { get; init; }
    public string Message { get; init; } = string.Empty;
    public PerformanceComparison CpuComparison { get; init; } = new();
    public PerformanceComparison MemoryComparison { get; init; } = new();
    public PerformanceComparison ResponseTimeComparison { get; init; } = new();
    public PerformanceComparison ThroughputComparison { get; init; } = new();
    public double OverallImprovement { get; init; }
}

/// <summary>
/// Performance comparison between current and baseline values.
/// </summary>
public sealed record PerformanceComparison
{
    public double Current { get; init; }
    public double Baseline { get; init; }
    public double PercentChange { get; init; }
}

/// <summary>
/// Performance baseline for comparison purposes.
/// </summary>
public sealed record PerformanceBaseline
{
    public DateTimeOffset Date { get; init; }
    public PerformanceSnapshot Snapshot { get; init; } = new();
    public int SampleCount { get; init; }
}
