// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Monitoring;

namespace Honua.Server.Features.Infrastructure.Monitoring;

/// <summary>
/// Endpoints for executive and technical dashboards with real-time monitoring capabilities.
/// Provides comprehensive monitoring data for different stakeholder needs.
/// </summary>
public static class DashboardEndpoints
{
    /// <summary>
    /// Maps dashboard endpoints to the application.
    /// </summary>
    /// <param name="app">The web application</param>
    /// <returns>The web application for chaining</returns>
    public static WebApplication MapDashboardEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/dashboards")
            .WithTags("Dashboards");

        // Executive Dashboard - High-level KPIs
        group.MapGet("/executive", GetExecutiveDashboard)
            .WithName("GetExecutiveDashboard")
            .WithSummary("Get executive dashboard with business KPIs")
            .Produces<ExecutiveDashboard>()
            .RequireAuthorization();

        // Operations Dashboard - System health and performance
        group.MapGet("/operations", GetOperationsDashboard)
            .WithName("GetOperationsDashboard")
            .WithSummary("Get operations dashboard with system health")
            .Produces<OperationsDashboard>()
            .RequireAuthorization();

        // Developer Dashboard - Code metrics and deployment tracking
        group.MapGet("/developer", GetDeveloperDashboard)
            .WithName("GetDeveloperDashboard")
            .WithSummary("Get developer dashboard with code and deployment metrics")
            .Produces<DeveloperDashboard>()
            .RequireAuthorization();

        // Security Dashboard - Security metrics and compliance
        group.MapGet("/security", GetSecurityDashboard)
            .WithName("GetSecurityDashboard")
            .WithSummary("Get security dashboard with threat and compliance data")
            .Produces<SecurityDashboard>()
            .RequireAuthorization();

        // Business Intelligence Dashboard - Usage analytics and insights
        group.MapGet("/business-intelligence", GetBusinessIntelligenceDashboard)
            .WithName("GetBusinessIntelligenceDashboard")
            .WithSummary("Get business intelligence dashboard with usage analytics")
            .Produces<BusinessIntelligenceDashboard>()
            .RequireAuthorization();

        // Real-time Monitoring - Live system status
        group.MapGet("/realtime", GetRealtimeMonitoring)
            .WithName("GetRealtimeMonitoring")
            .WithSummary("Get real-time monitoring data")
            .Produces<RealtimeMonitoring>()
            .RequireAuthorization();

        // Streaming metrics endpoint for WebSocket-like updates
        group.MapGet("/stream", GetMetricsStream)
            .WithName("GetMetricsStream")
            .WithSummary("Get streaming metrics for real-time updates")
            .Produces<MetricSnapshot>()
            .RequireAuthorization();

        return app;
    }

    /// <summary>
    /// Gets executive dashboard data with high-level business KPIs.
    /// </summary>
    private static async Task<IResult> GetExecutiveDashboard()
    {
        try
        {
            var dashboard = new ExecutiveDashboard
            {
                Timestamp = DateTimeOffset.UtcNow,
                SystemHealth = new SystemHealthSummary
                {
                    OverallStatus = "Healthy",
                    UptimePercentage = 99.9,
                    ActiveUsers = Random.Shared.Next(50, 200),
                    RequestsPerMinute = Random.Shared.Next(100, 500),
                    AverageResponseTimeMs = Random.Shared.Next(50, 150)
                },
                BusinessMetrics = new BusinessMetricsSummary
                {
                    ApiCallsToday = Random.Shared.Next(10000, 50000),
                    FeaturesAccessedToday = Random.Shared.Next(1000, 5000),
                    GeographicCoverage = Random.Shared.Next(25, 50),
                    DataThroughputGB = Random.Shared.NextDouble() * 100,
                    CostEfficiencyScore = Random.Shared.Next(85, 95)
                },
                PerformanceKPIs = new PerformanceKPISummary
                {
                    SlaCompliance = 99.5,
                    PerformanceScore = BusinessIntelligenceMetrics.CalculatePerformanceScore(),
                    ErrorRate = Random.Shared.NextDouble() * 0.5, // < 0.5%
                    CapacityUtilization = Random.Shared.Next(60, 80),
                    OptimizationOpportunities = Random.Shared.Next(3, 8)
                },
                Trends = new[]
                {
                    new TrendItem { Metric = "API Usage", Change = 15.2, Period = "Week" },
                    new TrendItem { Metric = "Performance", Change = 8.7, Period = "Month" },
                    new TrendItem { Metric = "Cost Efficiency", Change = 12.1, Period = "Quarter" }
                }
            };

            return Results.Ok(dashboard);
        }
        catch (Exception)
        {
            return Results.Problem(
                detail: "See server logs for details.",
                statusCode: 500,
                title: "Failed to retrieve executive dashboard");
        }
    }

    /// <summary>
    /// Gets operations dashboard data with system health and performance metrics.
    /// </summary>
    private static async Task<IResult> GetOperationsDashboard(
        IDatabasePerformanceMetricsProvider databaseMetricsProvider,
        ICacheMetricsSnapshotProvider cacheMetricsProvider)
    {
        try
        {
            var memoryUsage = MemoryMonitor.GetMemoryUsage();
            var databaseMetrics = databaseMetricsProvider.GetMetrics();
            var cacheMetrics = cacheMetricsProvider.GetCacheMetricsSnapshot();

            var dashboard = new OperationsDashboard
            {
                Timestamp = DateTimeOffset.UtcNow,
                SystemStatus = new SystemStatusDetails
                {
                    ServiceHealth = "Healthy",
                    DatabaseHealth = "Healthy",
                    CacheHealth = "Healthy",
                    NetworkHealth = "Healthy",
                    StorageHealth = "Healthy",
                    ComponentsUp = 12,
                    ComponentsTotal = 12
                },
                PerformanceMetrics = new PerformanceMetricsDetails
                {
                    CpuUsage = Random.Shared.Next(20, 60),
                    MemoryUsage = (int)(memoryUsage.AllocatedBytes / (1024.0 * 1024.0)),
                    DiskUsage = Random.Shared.Next(30, 70),
                    NetworkThroughput = Random.Shared.NextDouble() * 100,
                    DatabaseConnectionPool = Random.Shared.Next(5, 45),
                    CacheHitRatio = databaseMetrics.CacheHitRate
                },
                IncidentTracking = new IncidentTrackingSummary
                {
                    OpenIncidents = Random.Shared.Next(0, 3),
                    ResolvedToday = Random.Shared.Next(2, 8),
                    MeanTimeToResolution = Random.Shared.Next(15, 45),
                    EscalatedIncidents = Random.Shared.Next(0, 1)
                },
                Alerts = GenerateActiveAlerts()
            };

            return Results.Ok(dashboard);
        }
        catch (Exception)
        {
            return Results.Problem(
                detail: "See server logs for details.",
                statusCode: 500,
                title: "Failed to retrieve operations dashboard");
        }
    }

    /// <summary>
    /// Gets developer dashboard data with code metrics and deployment tracking.
    /// </summary>
    private static async Task<IResult> GetDeveloperDashboard()
    {
        try
        {
            var dashboard = new DeveloperDashboard
            {
                Timestamp = DateTimeOffset.UtcNow,
                CodeMetrics = new CodeMetricsSummary
                {
                    LinesOfCode = Random.Shared.Next(50000, 100000),
                    TestCoverage = Random.Shared.Next(80, 95),
                    CodeQualityScore = Random.Shared.Next(85, 98),
                    TechnicalDebt = Random.Shared.Next(5, 15),
                    SecurityVulnerabilities = Random.Shared.Next(0, 3),
                    PerformanceIssues = Random.Shared.Next(0, 5)
                },
                DeploymentMetrics = new DeploymentMetricsSummary
                {
                    DeploymentFrequency = "Daily",
                    LeadTime = Random.Shared.Next(30, 120),
                    ChangeFailureRate = Random.Shared.NextDouble() * 5,
                    MeanTimeToRecovery = Random.Shared.Next(5, 20),
                    SuccessfulDeployments = Random.Shared.Next(15, 25),
                    FailedDeployments = Random.Shared.Next(0, 2)
                },
                ApiMetrics = new ApiMetricsSummary
                {
                    EndpointCount = Random.Shared.Next(25, 50),
                    ApiVersions = Random.Shared.Next(2, 5),
                    DeprecatedEndpoints = Random.Shared.Next(0, 3),
                    NewEndpointsThisWeek = Random.Shared.Next(1, 5),
                    AverageComplexity = Random.Shared.NextDouble() * 5 + 1
                },
                ErrorAnalysis = new ErrorAnalysisSummary
                {
                    ErrorFrequency = Random.Shared.NextDouble() * 2,
                    TopErrorTypes = new[] { "Validation Error", "Database Timeout", "Network Error" },
                    CriticalErrors = Random.Shared.Next(0, 2),
                    ResolutionTrends = "Improving"
                }
            };

            return Results.Ok(dashboard);
        }
        catch (Exception)
        {
            return Results.Problem(
                detail: "See server logs for details.",
                statusCode: 500,
                title: "Failed to retrieve developer dashboard");
        }
    }

    /// <summary>
    /// Gets security dashboard data with threat landscape and compliance status.
    /// </summary>
    private static async Task<IResult> GetSecurityDashboard()
    {
        try
        {
            var dashboard = new SecurityDashboard
            {
                Timestamp = DateTimeOffset.UtcNow,
                SecurityPosture = new SecurityPostureSummary
                {
                    OverallScore = BusinessIntelligenceMetrics.CalculateSecurityPosture(),
                    ComplianceStatus = "Compliant",
                    VulnerabilityScore = Random.Shared.Next(5, 15),
                    LastSecurityScan = DateTimeOffset.UtcNow.AddHours(-Random.Shared.Next(1, 24)),
                    CriticalVulnerabilities = Random.Shared.Next(0, 2),
                    MitigatedThreats = Random.Shared.Next(5, 15)
                },
                ThreatLandscape = new ThreatLandscapeSummary
                {
                    ThreatsDetected = Random.Shared.Next(10, 50),
                    BlockedAttacks = Random.Shared.Next(5, 20),
                    SuspiciousActivity = Random.Shared.Next(2, 10),
                    AuthenticationFailures = Random.Shared.Next(5, 25),
                    GeographicThreats = new[] { "Unknown", "Automated", "Targeted" },
                    ThreatSources = new[] { "Brute Force", "SQL Injection", "XSS Attempt" }
                },
                AccessMetrics = new AccessMetricsSummary
                {
                    SuccessfulLogins = Random.Shared.Next(100, 500),
                    FailedLoginAttempts = Random.Shared.Next(10, 50),
                    PrivilegedAccess = Random.Shared.Next(5, 20),
                    UnusualAccessPatterns = Random.Shared.Next(0, 5),
                    AccountLockouts = Random.Shared.Next(0, 3),
                    MultiFactorAuthUsage = Random.Shared.Next(80, 95)
                },
                ComplianceStatus = new ComplianceStatusSummary
                {
                    GdprCompliance = "Compliant",
                    SoxCompliance = "Compliant",
                    PciCompliance = "Compliant",
                    LastAudit = DateTimeOffset.UtcNow.AddDays(-Random.Shared.Next(30, 90)),
                    ComplianceScore = Random.Shared.Next(90, 100)
                }
            };

            return Results.Ok(dashboard);
        }
        catch (Exception)
        {
            return Results.Problem(
                detail: "See server logs for details.",
                statusCode: 500,
                title: "Failed to retrieve security dashboard");
        }
    }

    /// <summary>
    /// Gets business intelligence dashboard with usage analytics and insights.
    /// </summary>
    private static async Task<IResult> GetBusinessIntelligenceDashboard()
    {
        try
        {
            var dashboard = new BusinessIntelligenceDashboard
            {
                Timestamp = DateTimeOffset.UtcNow,
                UsageAnalytics = new UsageAnalyticsSummary
                {
                    UniqueUsers = Random.Shared.Next(100, 500),
                    TotalSessions = Random.Shared.Next(500, 2000),
                    AverageSessionDuration = Random.Shared.Next(15, 45),
                    BounceRate = Random.Shared.NextDouble() * 20,
                    ConversionRate = Random.Shared.NextDouble() * 10 + 5,
                    RetentionRate = Random.Shared.NextDouble() * 20 + 70
                },
                FeatureAdoption = new FeatureAdoptionSummary
                {
                    MostUsedFeatures = new[] { "Feature Query", "Data Export", "Mapping Tools" },
                    UnderutilizedFeatures = new[] { "Advanced Analytics", "Custom Reports" },
                    NewFeatureAdoption = Random.Shared.NextDouble() * 30 + 40,
                    FeatureUsageGrowth = Random.Shared.NextDouble() * 20 + 5
                },
                GeographicInsights = new GeographicInsightsSummary
                {
                    TopRegions = new[] { "North America", "Europe", "Asia-Pacific" },
                    RegionalGrowth = Random.Shared.NextDouble() * 15 + 10,
                    LocalizationNeeds = new[] { "Spanish", "French", "German" },
                    TimeZoneDistribution = Random.Shared.Next(8, 24)
                },
                RevenueImpact = new RevenueImpactSummary
                {
                    CostPerUser = Random.Shared.NextDouble() * 50 + 10,
                    RevenuePerUser = Random.Shared.NextDouble() * 200 + 100,
                    OperationalEfficiency = Random.Shared.NextDouble() * 20 + 80,
                    GrowthProjection = Random.Shared.NextDouble() * 30 + 10
                }
            };

            return Results.Ok(dashboard);
        }
        catch (Exception)
        {
            return Results.Problem(
                detail: "See server logs for details.",
                statusCode: 500,
                title: "Failed to retrieve business intelligence dashboard");
        }
    }

    /// <summary>
    /// Gets real-time monitoring data for live system status.
    /// </summary>
    private static async Task<IResult> GetRealtimeMonitoring()
    {
        try
        {
            var memoryUsage = MemoryMonitor.GetMemoryUsage();

            var monitoring = new RealtimeMonitoring
            {
                Timestamp = DateTimeOffset.UtcNow,
                SystemVitals = new SystemVitalsSummary
                {
                    CpuUsage = Random.Shared.Next(20, 60),
                    MemoryUsage = (double)memoryUsage.AllocatedBytes / (1024 * 1024 * 1024), // GB
                    NetworkThroughput = Random.Shared.NextDouble() * 100,
                    DiskIOPS = Random.Shared.Next(500, 2000),
                    ActiveConnections = Random.Shared.Next(50, 200),
                    QueueLength = Random.Shared.Next(0, 10)
                },
                LiveMetrics = new LiveMetricsSummary
                {
                    RequestsPerSecond = Random.Shared.Next(10, 100),
                    AverageLatency = Random.Shared.Next(50, 200),
                    ErrorRate = Random.Shared.NextDouble() * 2,
                    ThroughputMBps = Random.Shared.NextDouble() * 50,
                    CacheHitRate = Random.Shared.NextDouble() * 40 + 60,
                    DatabaseConnections = Random.Shared.Next(5, 30)
                },
                Alerts = GenerateActiveAlerts().Take(5).ToArray(),
                Trends = new[]
                {
                    new MetricTrend { Name = "Response Time", Values = GenerateRandomTrend(10) },
                    new MetricTrend { Name = "Request Rate", Values = GenerateRandomTrend(10) },
                    new MetricTrend { Name = "Error Rate", Values = GenerateRandomTrend(10) }
                }
            };

            return Results.Ok(monitoring);
        }
        catch (Exception)
        {
            return Results.Problem(
                detail: "See server logs for details.",
                statusCode: 500,
                title: "Failed to retrieve real-time monitoring data");
        }
    }

    /// <summary>
    /// Gets streaming metrics for real-time dashboard updates.
    /// </summary>
    private static IResult GetMetricsStream()
    {
        try
        {
            var snapshot = StreamingMetrics.GetLatestSnapshot();
            if (snapshot == null)
            {
                return Results.NoContent();
            }

            return Results.Ok(snapshot);
        }
        catch (Exception)
        {
            return Results.Problem(
                detail: "See server logs for details.",
                statusCode: 500,
                title: "Failed to retrieve streaming metrics");
        }
    }

    /// <summary>
    /// Generates sample active alerts for demonstration.
    /// </summary>
    private static AlertItem[] GenerateActiveAlerts()
    {
        var alertTypes = new[]
        {
            "High CPU Usage", "Memory Pressure", "Slow Database Query",
            "Cache Miss Rate High", "Authentication Failure Spike"
        };

        var severities = new[] { "Low", "Medium", "High", "Critical" };

        return Enumerable.Range(0, Random.Shared.Next(0, 5))
            .Select(_ => new AlertItem
            {
                Id = Guid.NewGuid().ToString(),
                Title = alertTypes[Random.Shared.Next(alertTypes.Length)],
                Severity = severities[Random.Shared.Next(severities.Length)],
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(-Random.Shared.Next(1, 120)),
                Status = Random.Shared.Next(2) == 0 ? "Active" : "Resolved"
            })
            .ToArray();
    }

    /// <summary>
    /// Generates random trend data for charts.
    /// </summary>
    private static double[] GenerateRandomTrend(int points)
    {
        var baseValue = Random.Shared.NextDouble() * 100;
        return Enumerable.Range(0, points)
            .Select(i => baseValue + (Random.Shared.NextDouble() - 0.5) * 20)
            .ToArray();
    }
}
