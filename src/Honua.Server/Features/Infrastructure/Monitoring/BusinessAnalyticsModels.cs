// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Monitoring;

// Event models for tracking user behavior and system usage

/// <summary>
/// API usage event for tracking endpoint access and performance.
/// </summary>
public sealed record ApiUsageEvent
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string Method { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string Protocol { get; init; } = string.Empty;
    public double ResponseTimeMs { get; init; }
    public bool IsError { get; init; }
    public string? UserId { get; init; }
    public string? ClientId { get; init; }
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }
}

/// <summary>
/// User behavior event for tracking user actions and engagement.
/// </summary>
public sealed record UserBehaviorEvent
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string UserId { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;
    public string? ClientId { get; init; }
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }
    public string? Country { get; init; }
    public string? Timezone { get; init; }
    public Dictionary<string, object> Properties { get; init; } = new();
}

/// <summary>
/// Feature usage event for tracking feature adoption and usage patterns.
/// </summary>
public sealed record FeatureUsageEvent
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string FeatureName { get; init; } = string.Empty;
    public string UsageType { get; init; } = string.Empty;
    public string UserId { get; init; } = string.Empty;
    public string? ClientId { get; init; }
    public Dictionary<string, object> Parameters { get; init; } = new();
}

// Analytics result models

/// <summary>
/// Comprehensive API usage analytics report.
/// </summary>
public sealed record ApiUsageAnalytics
{
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset EndTime { get; init; }
    public int TotalRequests { get; init; }
    public int UniqueUsers { get; init; }
    public double ErrorRate { get; init; }
    public double AverageResponseTime { get; init; }
    public ApiEndpointStats[] TopEndpoints { get; init; } = Array.Empty<ApiEndpointStats>();
    public ProtocolStats[] ProtocolDistribution { get; init; } = Array.Empty<ProtocolStats>();
    public HourlyTrend[] TrendData { get; init; } = Array.Empty<HourlyTrend>();
}

/// <summary>
/// User behavior analytics with engagement and session metrics.
/// </summary>
public sealed record UserBehaviorAnalytics
{
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset EndTime { get; init; }
    public int ActiveUsers { get; init; }
    public int NewUsers { get; init; }
    public double AverageSessionDuration { get; init; }
    public double BounceRate { get; init; }
    public double RetentionRate { get; init; }
    public UserAgentStats[] UserAgentDistribution { get; init; } = Array.Empty<UserAgentStats>();
    public ActivityPattern[] ActivityPatterns { get; init; } = Array.Empty<ActivityPattern>();
}

/// <summary>
/// Feature adoption analytics and trends.
/// </summary>
public sealed record FeatureAdoptionAnalytics
{
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset EndTime { get; init; }
    public int TotalFeatures { get; init; }
    public int ActiveFeatures { get; init; }
    public FeatureAdoptionStats[] MostPopularFeatures { get; init; } = Array.Empty<FeatureAdoptionStats>();
    public FeatureAdoptionStats[] NewFeatures { get; init; } = Array.Empty<FeatureAdoptionStats>();
    public double OverallAdoptionRate { get; init; }
    public FeatureTrend[] FeatureTrends { get; init; } = Array.Empty<FeatureTrend>();
}

/// <summary>
/// Geographic analytics with regional usage patterns.
/// </summary>
public sealed record GeographicAnalytics
{
    public bool Enabled { get; init; }
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset EndTime { get; init; }
    public int TotalCountries { get; init; }
    public CountryStats[] TopCountries { get; init; } = Array.Empty<CountryStats>();
    public TimezoneStats[] TimezoneDistribution { get; init; } = Array.Empty<TimezoneStats>();
    public GlobalDistribution GlobalDistribution { get; init; } = new();
}

/// <summary>
/// Business KPI summary with key performance indicators.
/// </summary>
public sealed record BusinessKPISummary
{
    public DateTimeOffset Timestamp { get; init; }
    public int DailyActiveUsers { get; init; }
    public int MonthlyActiveUsers { get; init; }
    public int DailyApiCalls { get; init; }
    public int MonthlyApiCalls { get; init; }
    public double AverageSessionDuration { get; init; }
    public double UserRetentionRate { get; init; }
    public int SystemHealthScore { get; init; }
    public double RevenueImpactScore { get; init; }
    public GrowthMetric[] GrowthMetrics { get; init; } = Array.Empty<GrowthMetric>();
    public EfficiencyMetric[] EfficiencyMetrics { get; init; } = Array.Empty<EfficiencyMetric>();
}

/// <summary>
/// Performance correlation analysis with business metrics.
/// </summary>
public sealed record PerformanceCorrelationAnalysis
{
    public bool Enabled { get; init; }
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset EndTime { get; init; }
    public CorrelationMetric[] Correlations { get; init; } = Array.Empty<CorrelationMetric>();
    public double UserSatisfactionScore { get; init; }
    public string[] PerformanceImpactAssessment { get; init; } = Array.Empty<string>();
    public string[] Recommendations { get; init; } = Array.Empty<string>();
}

// Supporting data models

/// <summary>
/// API endpoint usage statistics.
/// </summary>
public sealed record ApiEndpointStats
{
    public string Endpoint { get; init; } = string.Empty;
    public string Method { get; init; } = string.Empty;
    public int RequestCount { get; init; }
    public double AverageResponseTime { get; init; }
    public double ErrorRate { get; init; }
    public int UniqueUsers { get; init; }
}

/// <summary>
/// Protocol usage statistics.
/// </summary>
public sealed record ProtocolStats
{
    public string Protocol { get; init; } = string.Empty;
    public int RequestCount { get; init; }
    public double AverageResponseTime { get; init; }
    public double ErrorRate { get; init; }
}

/// <summary>
/// Hourly trend data for time-series analysis.
/// </summary>
public sealed record HourlyTrend
{
    public DateTimeOffset Hour { get; init; }
    public int RequestCount { get; init; }
    public int ErrorCount { get; init; }
    public double AverageResponseTime { get; init; }
}

/// <summary>
/// User agent distribution statistics.
/// </summary>
public sealed record UserAgentStats
{
    public string Browser { get; init; } = string.Empty;
    public int UserCount { get; init; }
    public double Percentage { get; set; }
}

/// <summary>
/// Activity pattern by hour of day.
/// </summary>
public sealed record ActivityPattern
{
    public int Hour { get; init; }
    public int ActivityCount { get; init; }
    public int UniqueUsers { get; init; }
}

/// <summary>
/// Feature adoption statistics.
/// </summary>
public sealed record FeatureAdoptionStats
{
    public string FeatureName { get; init; } = string.Empty;
    public int TotalUsage { get; init; }
    public int UniqueUsers { get; init; }
    public double AdoptionRate { get; init; }
    public DateTimeOffset FirstUsed { get; init; }
    public DateTimeOffset LastUsed { get; init; }
    public double GrowthRate { get; init; }
}

/// <summary>
/// Feature usage trend over time.
/// </summary>
public sealed record FeatureTrend
{
    public string FeatureName { get; init; } = string.Empty;
    public double UsageTrend { get; init; }
    public DailyUsage[] DailyUsage { get; init; } = Array.Empty<DailyUsage>();
}

/// <summary>
/// Daily usage data point.
/// </summary>
public sealed record DailyUsage
{
    public DateTime Date { get; init; }
    public int Count { get; init; }
}

/// <summary>
/// Country usage statistics.
/// </summary>
public sealed record CountryStats
{
    public string Country { get; init; } = string.Empty;
    public int UserCount { get; init; }
    public int RequestCount { get; init; }
    public double Percentage { get; set; }
}

/// <summary>
/// Timezone usage statistics.
/// </summary>
public sealed record TimezoneStats
{
    public string Timezone { get; init; } = string.Empty;
    public int UserCount { get; init; }
    public string[] ActiveHours { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Global distribution summary.
/// </summary>
public sealed record GlobalDistribution
{
    public ContinentStats[] Continents { get; init; } = Array.Empty<ContinentStats>();
    public string PrimaryMarket { get; init; } = string.Empty;
    public int GlobalReach { get; init; }
}

/// <summary>
/// Continent usage statistics.
/// </summary>
public sealed record ContinentStats
{
    public string Continent { get; init; } = string.Empty;
    public int Countries { get; init; }
    public int Users { get; init; }
}

/// <summary>
/// Growth metric for business analysis.
/// </summary>
public sealed record GrowthMetric
{
    public string Metric { get; init; } = string.Empty;
    public double GrowthRate { get; init; }
}

/// <summary>
/// Efficiency metric for operational analysis.
/// </summary>
public sealed record EfficiencyMetric
{
    public string Metric { get; init; } = string.Empty;
    public double Value { get; init; }
}

/// <summary>
/// Correlation metric for performance analysis.
/// </summary>
public sealed record CorrelationMetric
{
    public string Metric1 { get; init; } = string.Empty;
    public string Metric2 { get; init; } = string.Empty;
    public double CorrelationCoefficient { get; init; }
    public string Strength { get; init; } = string.Empty;
}

// Internal data models for tracking

/// <summary>
/// Internal model for API endpoint metrics tracking.
/// </summary>
internal sealed record ApiEndpointMetrics
{
    public string Endpoint { get; init; } = string.Empty;
    public string Method { get; init; } = string.Empty;
    public int RequestCount { get; init; }
    public double TotalResponseTime { get; init; }
    public int ErrorCount { get; init; }
    public DateTimeOffset LastAccessed { get; init; }
}

/// <summary>
/// Internal model for user session tracking.
/// </summary>
internal sealed record UserSessionData
{
    public string UserId { get; init; } = string.Empty;
    public DateTimeOffset SessionStart { get; init; }
    public DateTimeOffset LastActivity { get; init; }
    public int ActivityCount { get; init; }
    public string? ClientId { get; init; }
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }
}

/// <summary>
/// Internal model for feature metrics tracking.
/// </summary>
internal sealed record FeatureMetrics
{
    public string FeatureName { get; init; } = string.Empty;
    public int UsageCount { get; init; }
    public HashSet<string> UniqueUsers { get; init; } = new();
    public DateTimeOffset FirstUsed { get; init; }
    public DateTimeOffset LastUsed { get; init; }
}

/// <summary>
/// Internal model for geographic metrics tracking.
/// </summary>
internal sealed record GeographicMetrics
{
    public string Country { get; init; } = string.Empty;
    public int UserCount { get; init; }
    public int RequestCount { get; init; }
    public HashSet<string> UniqueUsers { get; init; } = new();
}
