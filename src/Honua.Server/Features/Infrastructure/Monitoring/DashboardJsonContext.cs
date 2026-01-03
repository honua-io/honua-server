// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Infrastructure.Monitoring;

namespace Honua.Server.Features.Infrastructure.Monitoring;

/// <summary>
/// JSON serialization context for dashboard models to support AOT compilation.
/// </summary>
[JsonSerializable(typeof(ExecutiveDashboard))]
[JsonSerializable(typeof(OperationsDashboard))]
[JsonSerializable(typeof(DeveloperDashboard))]
[JsonSerializable(typeof(SecurityDashboard))]
[JsonSerializable(typeof(BusinessIntelligenceDashboard))]
[JsonSerializable(typeof(RealtimeMonitoring))]
[JsonSerializable(typeof(MetricSnapshot))]
[JsonSerializable(typeof(SystemHealthSummary))]
[JsonSerializable(typeof(BusinessMetricsSummary))]
[JsonSerializable(typeof(PerformanceKPISummary))]
[JsonSerializable(typeof(TrendItem))]
[JsonSerializable(typeof(SystemStatusDetails))]
[JsonSerializable(typeof(PerformanceMetricsDetails))]
[JsonSerializable(typeof(IncidentTrackingSummary))]
[JsonSerializable(typeof(AlertItem))]
[JsonSerializable(typeof(CodeMetricsSummary))]
[JsonSerializable(typeof(DeploymentMetricsSummary))]
[JsonSerializable(typeof(ApiMetricsSummary))]
[JsonSerializable(typeof(ErrorAnalysisSummary))]
[JsonSerializable(typeof(SecurityPostureSummary))]
[JsonSerializable(typeof(ThreatLandscapeSummary))]
[JsonSerializable(typeof(AccessMetricsSummary))]
[JsonSerializable(typeof(ComplianceStatusSummary))]
[JsonSerializable(typeof(UsageAnalyticsSummary))]
[JsonSerializable(typeof(FeatureAdoptionSummary))]
[JsonSerializable(typeof(GeographicInsightsSummary))]
[JsonSerializable(typeof(RevenueImpactSummary))]
[JsonSerializable(typeof(SystemVitalsSummary))]
[JsonSerializable(typeof(LiveMetricsSummary))]
[JsonSerializable(typeof(MetricTrend))]
[JsonSerializable(typeof(TrendItem[]))]
[JsonSerializable(typeof(AlertItem[]))]
[JsonSerializable(typeof(MetricTrend[]))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(double[]))]
public partial class DashboardJsonContext : JsonSerializerContext
{
}
