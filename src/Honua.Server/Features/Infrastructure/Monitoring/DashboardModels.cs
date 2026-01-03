// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Monitoring;

/// <summary>
/// Executive dashboard data model providing high-level business KPIs and strategic insights.
/// </summary>
public sealed record ExecutiveDashboard
{
    public DateTimeOffset Timestamp { get; init; }
    public SystemHealthSummary SystemHealth { get; init; } = new();
    public BusinessMetricsSummary BusinessMetrics { get; init; } = new();
    public PerformanceKPISummary PerformanceKPIs { get; init; } = new();
    public TrendItem[] Trends { get; init; } = Array.Empty<TrendItem>();
}

/// <summary>
/// Operations dashboard data model for system health and performance monitoring.
/// </summary>
public sealed record OperationsDashboard
{
    public DateTimeOffset Timestamp { get; init; }
    public SystemStatusDetails SystemStatus { get; init; } = new();
    public PerformanceMetricsDetails PerformanceMetrics { get; init; } = new();
    public IncidentTrackingSummary IncidentTracking { get; init; } = new();
    public AlertItem[] Alerts { get; init; } = Array.Empty<AlertItem>();
}

/// <summary>
/// Developer dashboard data model for code metrics and deployment tracking.
/// </summary>
public sealed record DeveloperDashboard
{
    public DateTimeOffset Timestamp { get; init; }
    public CodeMetricsSummary CodeMetrics { get; init; } = new();
    public DeploymentMetricsSummary DeploymentMetrics { get; init; } = new();
    public ApiMetricsSummary ApiMetrics { get; init; } = new();
    public ErrorAnalysisSummary ErrorAnalysis { get; init; } = new();
}

/// <summary>
/// Security dashboard data model for threat landscape and compliance monitoring.
/// </summary>
public sealed record SecurityDashboard
{
    public DateTimeOffset Timestamp { get; init; }
    public SecurityPostureSummary SecurityPosture { get; init; } = new();
    public ThreatLandscapeSummary ThreatLandscape { get; init; } = new();
    public AccessMetricsSummary AccessMetrics { get; init; } = new();
    public ComplianceStatusSummary ComplianceStatus { get; init; } = new();
}

/// <summary>
/// Business intelligence dashboard data model for usage analytics and insights.
/// </summary>
public sealed record BusinessIntelligenceDashboard
{
    public DateTimeOffset Timestamp { get; init; }
    public UsageAnalyticsSummary UsageAnalytics { get; init; } = new();
    public FeatureAdoptionSummary FeatureAdoption { get; init; } = new();
    public GeographicInsightsSummary GeographicInsights { get; init; } = new();
    public RevenueImpactSummary RevenueImpact { get; init; } = new();
}

/// <summary>
/// Real-time monitoring data model for live system status and performance.
/// </summary>
public sealed record RealtimeMonitoring
{
    public DateTimeOffset Timestamp { get; init; }
    public SystemVitalsSummary SystemVitals { get; init; } = new();
    public LiveMetricsSummary LiveMetrics { get; init; } = new();
    public AlertItem[] Alerts { get; init; } = Array.Empty<AlertItem>();
    public MetricTrend[] Trends { get; init; } = Array.Empty<MetricTrend>();
}

// Supporting data models for dashboard components

/// <summary>
/// System health summary for executive overview.
/// </summary>
public sealed record SystemHealthSummary
{
    public string OverallStatus { get; init; } = string.Empty;
    public double UptimePercentage { get; init; }
    public int ActiveUsers { get; init; }
    public int RequestsPerMinute { get; init; }
    public int AverageResponseTimeMs { get; init; }
}

/// <summary>
/// Business metrics summary for executive reporting.
/// </summary>
public sealed record BusinessMetricsSummary
{
    public int ApiCallsToday { get; init; }
    public int FeaturesAccessedToday { get; init; }
    public int GeographicCoverage { get; init; }
    public double DataThroughputGB { get; init; }
    public int CostEfficiencyScore { get; init; }
}

/// <summary>
/// Performance KPI summary for executive monitoring.
/// </summary>
public sealed record PerformanceKPISummary
{
    public double SlaCompliance { get; init; }
    public int PerformanceScore { get; init; }
    public double ErrorRate { get; init; }
    public int CapacityUtilization { get; init; }
    public int OptimizationOpportunities { get; init; }
}

/// <summary>
/// Trend item for tracking metric changes over time.
/// </summary>
public sealed record TrendItem
{
    public string Metric { get; init; } = string.Empty;
    public double Change { get; init; }
    public string Period { get; init; } = string.Empty;
}

/// <summary>
/// Detailed system status for operations dashboard.
/// </summary>
public sealed record SystemStatusDetails
{
    public string ServiceHealth { get; init; } = string.Empty;
    public string DatabaseHealth { get; init; } = string.Empty;
    public string CacheHealth { get; init; } = string.Empty;
    public string NetworkHealth { get; init; } = string.Empty;
    public string StorageHealth { get; init; } = string.Empty;
    public int ComponentsUp { get; init; }
    public int ComponentsTotal { get; init; }
}

/// <summary>
/// Detailed performance metrics for operations monitoring.
/// </summary>
public sealed record PerformanceMetricsDetails
{
    public int CpuUsage { get; init; }
    public int MemoryUsage { get; init; }
    public int DiskUsage { get; init; }
    public double NetworkThroughput { get; init; }
    public int DatabaseConnectionPool { get; init; }
    public double CacheHitRatio { get; init; }
}

/// <summary>
/// Incident tracking summary for operations management.
/// </summary>
public sealed record IncidentTrackingSummary
{
    public int OpenIncidents { get; init; }
    public int ResolvedToday { get; init; }
    public int MeanTimeToResolution { get; init; }
    public int EscalatedIncidents { get; init; }
}

/// <summary>
/// Alert item for incident and monitoring notifications.
/// </summary>
public sealed record AlertItem
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Severity { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; }
    public string Status { get; init; } = string.Empty;
}

/// <summary>
/// Code metrics summary for developer dashboard.
/// </summary>
public sealed record CodeMetricsSummary
{
    public int LinesOfCode { get; init; }
    public int TestCoverage { get; init; }
    public int CodeQualityScore { get; init; }
    public int TechnicalDebt { get; init; }
    public int SecurityVulnerabilities { get; init; }
    public int PerformanceIssues { get; init; }
}

/// <summary>
/// Deployment metrics summary for developer tracking.
/// </summary>
public sealed record DeploymentMetricsSummary
{
    public string DeploymentFrequency { get; init; } = string.Empty;
    public int LeadTime { get; init; }
    public double ChangeFailureRate { get; init; }
    public int MeanTimeToRecovery { get; init; }
    public int SuccessfulDeployments { get; init; }
    public int FailedDeployments { get; init; }
}

/// <summary>
/// API metrics summary for developer monitoring.
/// </summary>
public sealed record ApiMetricsSummary
{
    public int EndpointCount { get; init; }
    public int ApiVersions { get; init; }
    public int DeprecatedEndpoints { get; init; }
    public int NewEndpointsThisWeek { get; init; }
    public double AverageComplexity { get; init; }
}

/// <summary>
/// Error analysis summary for developer insights.
/// </summary>
public sealed record ErrorAnalysisSummary
{
    public double ErrorFrequency { get; init; }
    public string[] TopErrorTypes { get; init; } = Array.Empty<string>();
    public int CriticalErrors { get; init; }
    public string ResolutionTrends { get; init; } = string.Empty;
}

/// <summary>
/// Security posture summary for security dashboard.
/// </summary>
public sealed record SecurityPostureSummary
{
    public int OverallScore { get; init; }
    public string ComplianceStatus { get; init; } = string.Empty;
    public int VulnerabilityScore { get; init; }
    public DateTimeOffset LastSecurityScan { get; init; }
    public int CriticalVulnerabilities { get; init; }
    public int MitigatedThreats { get; init; }
}

/// <summary>
/// Threat landscape summary for security monitoring.
/// </summary>
public sealed record ThreatLandscapeSummary
{
    public int ThreatsDetected { get; init; }
    public int BlockedAttacks { get; init; }
    public int SuspiciousActivity { get; init; }
    public int AuthenticationFailures { get; init; }
    public string[] GeographicThreats { get; init; } = Array.Empty<string>();
    public string[] ThreatSources { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Access metrics summary for security analysis.
/// </summary>
public sealed record AccessMetricsSummary
{
    public int SuccessfulLogins { get; init; }
    public int FailedLoginAttempts { get; init; }
    public int PrivilegedAccess { get; init; }
    public int UnusualAccessPatterns { get; init; }
    public int AccountLockouts { get; init; }
    public int MultiFactorAuthUsage { get; init; }
}

/// <summary>
/// Compliance status summary for regulatory monitoring.
/// </summary>
public sealed record ComplianceStatusSummary
{
    public string GdprCompliance { get; init; } = string.Empty;
    public string SoxCompliance { get; init; } = string.Empty;
    public string PciCompliance { get; init; } = string.Empty;
    public DateTimeOffset LastAudit { get; init; }
    public int ComplianceScore { get; init; }
}

/// <summary>
/// Usage analytics summary for business intelligence.
/// </summary>
public sealed record UsageAnalyticsSummary
{
    public int UniqueUsers { get; init; }
    public int TotalSessions { get; init; }
    public int AverageSessionDuration { get; init; }
    public double BounceRate { get; init; }
    public double ConversionRate { get; init; }
    public double RetentionRate { get; init; }
}

/// <summary>
/// Feature adoption summary for product insights.
/// </summary>
public sealed record FeatureAdoptionSummary
{
    public string[] MostUsedFeatures { get; init; } = Array.Empty<string>();
    public string[] UnderutilizedFeatures { get; init; } = Array.Empty<string>();
    public double NewFeatureAdoption { get; init; }
    public double FeatureUsageGrowth { get; init; }
}

/// <summary>
/// Geographic insights summary for market analysis.
/// </summary>
public sealed record GeographicInsightsSummary
{
    public string[] TopRegions { get; init; } = Array.Empty<string>();
    public double RegionalGrowth { get; init; }
    public string[] LocalizationNeeds { get; init; } = Array.Empty<string>();
    public int TimeZoneDistribution { get; init; }
}

/// <summary>
/// Revenue impact summary for financial analysis.
/// </summary>
public sealed record RevenueImpactSummary
{
    public double CostPerUser { get; init; }
    public double RevenuePerUser { get; init; }
    public double OperationalEfficiency { get; init; }
    public double GrowthProjection { get; init; }
}

/// <summary>
/// System vitals summary for real-time monitoring.
/// </summary>
public sealed record SystemVitalsSummary
{
    public int CpuUsage { get; init; }
    public double MemoryUsage { get; init; }
    public double NetworkThroughput { get; init; }
    public int DiskIOPS { get; init; }
    public int ActiveConnections { get; init; }
    public int QueueLength { get; init; }
}

/// <summary>
/// Live metrics summary for real-time dashboard updates.
/// </summary>
public sealed record LiveMetricsSummary
{
    public int RequestsPerSecond { get; init; }
    public int AverageLatency { get; init; }
    public double ErrorRate { get; init; }
    public double ThroughputMBps { get; init; }
    public double CacheHitRate { get; init; }
    public int DatabaseConnections { get; init; }
}

/// <summary>
/// Metric trend for time-series charts.
/// </summary>
public sealed record MetricTrend
{
    public string Name { get; init; } = string.Empty;
    public double[] Values { get; init; } = Array.Empty<double>();
}
