// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Infrastructure.Security;

/// <summary>
/// Endpoints for compliance monitoring and security dashboards.
/// Provides executive and technical dashboards for security posture visibility.
/// </summary>
public static class ComplianceDashboardEndpoints
{
    public static void MapComplianceDashboardEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/admin/security/compliance")
            .WithTags("Security Compliance");

        // Executive dashboard endpoints
        group.MapGet("/dashboard/executive", GetExecutiveDashboard)
            .WithName("GetExecutiveSecurityDashboard")
            .WithSummary("Get executive security dashboard")
            .RequireAuthorization("AdminPolicy");

        group.MapGet("/dashboard/technical", GetTechnicalDashboard)
            .WithName("GetTechnicalSecurityDashboard")
            .WithSummary("Get technical security dashboard")
            .RequireAuthorization("SecurityAnalystPolicy");

        group.MapGet("/dashboard/compliance", GetComplianceDashboard)
            .WithName("GetComplianceDashboard")
            .WithSummary("Get compliance status dashboard")
            .RequireAuthorization("ComplianceOfficerPolicy");

        // Compliance reports endpoints
        group.MapGet("/reports", GetComplianceReports)
            .WithName("GetComplianceReports")
            .WithSummary("Get list of compliance reports")
            .RequireAuthorization("ComplianceOfficerPolicy");

        group.MapPost("/reports", GenerateComplianceReport)
            .WithName("GenerateComplianceReport")
            .WithSummary("Generate new compliance report")
            .RequireAuthorization("ComplianceOfficerPolicy");

        group.MapGet("/reports/{reportId}", GetComplianceReport)
            .WithName("GetComplianceReport")
            .WithSummary("Get specific compliance report")
            .RequireAuthorization("ComplianceOfficerPolicy");

        // Security metrics endpoints
        group.MapGet("/metrics/overview", GetSecurityMetricsOverview)
            .WithName("GetSecurityMetricsOverview")
            .WithSummary("Get security metrics overview")
            .RequireAuthorization("SecurityAnalystPolicy");

        group.MapGet("/metrics/trends", GetSecurityTrends)
            .WithName("GetSecurityTrends")
            .WithSummary("Get security trend analysis")
            .RequireAuthorization("SecurityAnalystPolicy");

        group.MapGet("/metrics/incidents", GetSecurityIncidents)
            .WithName("GetSecurityIncidents")
            .WithSummary("Get security incidents dashboard")
            .RequireAuthorization("SecurityAnalystPolicy");

        // Real-time monitoring endpoints
        group.MapGet("/monitoring/alerts", GetActiveSecurityAlerts)
            .WithName("GetActiveSecurityAlerts")
            .WithSummary("Get active security alerts")
            .RequireAuthorization("SecurityAnalystPolicy");

        group.MapGet("/monitoring/threats", GetThreatDetections)
            .WithName("GetThreatDetections")
            .WithSummary("Get recent threat detections")
            .RequireAuthorization("SecurityAnalystPolicy");

        group.MapGet("/monitoring/anomalies", GetAnomalyDetections)
            .WithName("GetAnomalyDetections")
            .WithSummary("Get recent anomaly detections")
            .RequireAuthorization("SecurityAnalystPolicy");

        // Health and status endpoints
        group.MapGet("/health/security", GetSecurityHealthStatus)
            .WithName("GetSecurityHealthStatus")
            .WithSummary("Get overall security health status")
            .RequireAuthorization("SecurityAnalystPolicy");

        group.MapGet("/health/compliance", GetComplianceHealthStatus)
            .WithName("GetComplianceHealthStatus")
            .WithSummary("Get compliance health status")
            .RequireAuthorization("ComplianceOfficerPolicy");

        // Configuration and settings endpoints
        group.MapGet("/settings/frameworks", GetComplianceFrameworks)
            .WithName("GetComplianceFrameworks")
            .WithSummary("Get supported compliance frameworks")
            .RequireAuthorization("ComplianceOfficerPolicy");

        group.MapPost("/settings/thresholds", UpdateAlertThresholds)
            .WithName("UpdateAlertThresholds")
            .WithSummary("Update security alert thresholds")
            .RequireAuthorization("AdminPolicy");
    }

    private static async Task<IResult> GetExecutiveDashboard(
        [FromServices] IComplianceDashboardService dashboardService,
        [FromQuery] DateTime? startDate,
        [FromQuery] DateTime? endDate)
    {
        try
        {
            var dateRange = GetDateRange(startDate, endDate, TimeSpan.FromDays(30));
            var dashboard = await dashboardService.GetExecutiveDashboardAsync(
                dateRange.StartDate, dateRange.EndDate);

            return Results.Ok(dashboard);
        }
        catch (Exception ex)
        {
            return Results.Problem(
                detail: $"Failed to retrieve executive dashboard: {ex.Message}",
                statusCode: 500,
                title: "Dashboard Error");
        }
    }

    private static async Task<IResult> GetTechnicalDashboard(
        [FromServices] IComplianceDashboardService dashboardService,
        [FromQuery] DateTime? startDate,
        [FromQuery] DateTime? endDate)
    {
        try
        {
            var dateRange = GetDateRange(startDate, endDate, TimeSpan.FromDays(7));
            var dashboard = await dashboardService.GetTechnicalDashboardAsync(
                dateRange.StartDate, dateRange.EndDate);

            return Results.Ok(dashboard);
        }
        catch (Exception ex)
        {
            return Results.Problem(
                detail: $"Failed to retrieve technical dashboard: {ex.Message}",
                statusCode: 500,
                title: "Dashboard Error");
        }
    }

    private static async Task<IResult> GetComplianceDashboard(
        [FromServices] IComplianceDashboardService dashboardService,
        [FromQuery] string? framework,
        [FromQuery] DateTime? startDate,
        [FromQuery] DateTime? endDate)
    {
        try
        {
            var dateRange = GetDateRange(startDate, endDate, TimeSpan.FromDays(90));
            var dashboard = await dashboardService.GetComplianceDashboardAsync(
                framework, dateRange.StartDate, dateRange.EndDate);

            return Results.Ok(dashboard);
        }
        catch (Exception ex)
        {
            return Results.Problem(
                detail: $"Failed to retrieve compliance dashboard: {ex.Message}",
                statusCode: 500,
                title: "Dashboard Error");
        }
    }

    private static async Task<IResult> GetComplianceReports(
        [FromServices] IComplianceReportingService reportingService,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? reportType = null,
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null)
    {
        try
        {
            var request = new ComplianceReportListRequest
            {
                Page = page,
                PageSize = pageSize,
                ReportType = reportType,
                StartDate = startDate,
                EndDate = endDate
            };

            var reports = await reportingService.GetComplianceReportsAsync(request);
            return Results.Ok(reports);
        }
        catch (Exception ex)
        {
            return Results.Problem(
                detail: $"Failed to retrieve compliance reports: {ex.Message}",
                statusCode: 500,
                title: "Reports Error");
        }
    }

    private static async Task<IResult> GenerateComplianceReport(
        [FromServices] IComplianceReportingService reportingService,
        [FromServices] ComprehensiveAuditLogger auditLogger,
        [FromBody] ComplianceReportGenerationRequest request,
        HttpContext context)
    {
        try
        {
            // Validate request
            if (request.StartDate >= request.EndDate)
            {
                return Results.BadRequest("Invalid date range");
            }

            if (request.EndDate > DateTime.UtcNow)
            {
                return Results.BadRequest("End date cannot be in the future");
            }

            // Generate report
            var reportRequest = new ComplianceReportRequest
            {
                StartDate = request.StartDate,
                EndDate = request.EndDate,
                ReportType = request.ReportType,
                RequestedBy = context.User.Identity?.Name,
                Filters = request.Filters,
                IncludePersonalData = request.IncludePersonalData,
                ComplianceFramework = request.ComplianceFramework
            };

            var report = await reportingService.GenerateReportAsync(reportRequest);

            // Log report generation
            await auditLogger.LogAdministrativeActionAsync(new AdministrativeAction
            {
                AdminUserId = context.User.Identity?.Name,
                ClientIp = SecurityAuditLogger.GetClientIpAddress(context),
                UserAgent = SecurityAuditLogger.GetUserAgent(context),
                ActionType = "COMPLIANCE_REPORT_GENERATION",
                TargetResource = "ComplianceReport",
                ActionParameters = new Dictionary<string, object>
                {
                    ["ReportType"] = request.ReportType,
                    ["DateRange"] = $"{request.StartDate:yyyy-MM-dd} to {request.EndDate:yyyy-MM-dd}",
                    ["ComplianceFramework"] = request.ComplianceFramework ?? "All"
                },
                BusinessJustification = request.BusinessJustification
            });

            return Results.Ok(new { ReportId = report.ReportId, Status = "Generated" });
        }
        catch (Exception ex)
        {
            return Results.Problem(
                detail: $"Failed to generate compliance report: {ex.Message}",
                statusCode: 500,
                title: "Report Generation Error");
        }
    }

    private static async Task<IResult> GetComplianceReport(
        [FromServices] IComplianceReportingService reportingService,
        [FromRoute] string reportId)
    {
        try
        {
            var report = await reportingService.GetReportAsync(reportId);
            if (report == null)
            {
                return Results.NotFound($"Report {reportId} not found");
            }

            return Results.Ok(report);
        }
        catch (Exception ex)
        {
            return Results.Problem(
                detail: $"Failed to retrieve compliance report: {ex.Message}",
                statusCode: 500,
                title: "Report Retrieval Error");
        }
    }

    private static async Task<IResult> GetSecurityMetricsOverview(
        [FromServices] ISecurityMetricsService metricsService,
        [FromQuery] DateTime? startDate,
        [FromQuery] DateTime? endDate)
    {
        try
        {
            var dateRange = GetDateRange(startDate, endDate, TimeSpan.FromDays(7));
            var metrics = await metricsService.GetMetricsOverviewAsync(
                dateRange.StartDate, dateRange.EndDate);

            return Results.Ok(metrics);
        }
        catch (Exception ex)
        {
            return Results.Problem(
                detail: $"Failed to retrieve security metrics: {ex.Message}",
                statusCode: 500,
                title: "Metrics Error");
        }
    }

    private static async Task<IResult> GetSecurityTrends(
        [FromServices] ISecurityMetricsService metricsService,
        [FromQuery] DateTime? startDate,
        [FromQuery] DateTime? endDate,
        [FromQuery] string granularity = "daily")
    {
        try
        {
            var dateRange = GetDateRange(startDate, endDate, TimeSpan.FromDays(30));
            var trends = await metricsService.GetSecurityTrendsAsync(
                dateRange.StartDate, dateRange.EndDate, granularity);

            return Results.Ok(trends);
        }
        catch (Exception ex)
        {
            return Results.Problem(
                detail: $"Failed to retrieve security trends: {ex.Message}",
                statusCode: 500,
                title: "Trends Error");
        }
    }

    private static async Task<IResult> GetSecurityIncidents(
        [FromServices] ISecurityIncidentService incidentService,
        [FromQuery] DateTime? startDate,
        [FromQuery] DateTime? endDate,
        [FromQuery] string? status = null,
        [FromQuery] string? severity = null)
    {
        try
        {
            var dateRange = GetDateRange(startDate, endDate, TimeSpan.FromDays(30));
            var request = new SecurityIncidentRequest
            {
                StartDate = dateRange.StartDate,
                EndDate = dateRange.EndDate,
                Status = status,
                Severity = severity
            };

            var incidents = await incidentService.GetIncidentsAsync(request);
            return Results.Ok(incidents);
        }
        catch (Exception ex)
        {
            return Results.Problem(
                detail: $"Failed to retrieve security incidents: {ex.Message}",
                statusCode: 500,
                title: "Incidents Error");
        }
    }

    private static async Task<IResult> GetActiveSecurityAlerts(
        [FromServices] ISecurityAlertService alertService,
        [FromQuery] int limit = 100,
        [FromQuery] string? severity = null,
        [FromQuery] string? alertType = null)
    {
        try
        {
            var request = new SecurityAlertRequest
            {
                Limit = limit,
                Severity = severity,
                AlertType = alertType,
                Status = "Open"
            };

            var alerts = await alertService.GetActiveAlertsAsync(request);
            return Results.Ok(alerts);
        }
        catch (Exception ex)
        {
            return Results.Problem(
                detail: $"Failed to retrieve security alerts: {ex.Message}",
                statusCode: 500,
                title: "Alerts Error");
        }
    }

    private static async Task<IResult> GetThreatDetections(
        [FromServices] IThreatDetectionService threatService,
        [FromQuery] DateTime? startDate,
        [FromQuery] DateTime? endDate,
        [FromQuery] string? threatType = null,
        [FromQuery] string? severity = null)
    {
        try
        {
            var dateRange = GetDateRange(startDate, endDate, TimeSpan.FromHours(24));
            var request = new ThreatDetectionRequest
            {
                StartDate = dateRange.StartDate,
                EndDate = dateRange.EndDate,
                ThreatType = threatType,
                Severity = severity
            };

            var threats = await threatService.GetThreatDetectionsAsync(request);
            return Results.Ok(threats);
        }
        catch (Exception ex)
        {
            return Results.Problem(
                detail: $"Failed to retrieve threat detections: {ex.Message}",
                statusCode: 500,
                title: "Threats Error");
        }
    }

    private static async Task<IResult> GetAnomalyDetections(
        [FromServices] IAnomalyDetectionService anomalyService,
        [FromQuery] DateTime? startDate,
        [FromQuery] DateTime? endDate,
        [FromQuery] string? anomalyType = null,
        [FromQuery] string? severity = null)
    {
        try
        {
            var dateRange = GetDateRange(startDate, endDate, TimeSpan.FromHours(24));
            var request = new AnomalyDetectionRequest
            {
                StartDate = dateRange.StartDate,
                EndDate = dateRange.EndDate,
                AnomalyType = anomalyType,
                Severity = severity
            };

            var anomalies = await anomalyService.GetAnomalyDetectionsAsync(request);
            return Results.Ok(anomalies);
        }
        catch (Exception ex)
        {
            return Results.Problem(
                detail: $"Failed to retrieve anomaly detections: {ex.Message}",
                statusCode: 500,
                title: "Anomalies Error");
        }
    }

    private static async Task<IResult> GetSecurityHealthStatus(
        [FromServices] SecurityHealthCheck healthCheck)
    {
        try
        {
            var healthResult = await healthCheck.CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext());
            return Results.Ok(new
            {
                Status = healthResult.Status.ToString(),
                Description = healthResult.Description,
                Data = healthResult.Data,
                LastChecked = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            return Results.Problem(
                detail: $"Failed to retrieve security health status: {ex.Message}",
                statusCode: 500,
                title: "Health Check Error");
        }
    }

    private static async Task<IResult> GetComplianceHealthStatus(
        [FromServices] IComplianceHealthService complianceHealth)
    {
        try
        {
            var healthStatus = await complianceHealth.GetComplianceHealthAsync();
            return Results.Ok(healthStatus);
        }
        catch (Exception ex)
        {
            return Results.Problem(
                detail: $"Failed to retrieve compliance health status: {ex.Message}",
                statusCode: 500,
                title: "Compliance Health Error");
        }
    }

    private static async Task<IResult> GetComplianceFrameworks(
        [FromServices] IComplianceFrameworkService frameworkService)
    {
        try
        {
            var frameworks = await frameworkService.GetSupportedFrameworksAsync();
            return Results.Ok(frameworks);
        }
        catch (Exception ex)
        {
            return Results.Problem(
                detail: $"Failed to retrieve compliance frameworks: {ex.Message}",
                statusCode: 500,
                title: "Frameworks Error");
        }
    }

    private static async Task<IResult> UpdateAlertThresholds(
        [FromServices] ISecurityConfigurationService configService,
        [FromServices] ComprehensiveAuditLogger auditLogger,
        [FromBody] AlertThresholdsUpdateRequest request,
        HttpContext context)
    {
        try
        {
            // Update thresholds
            await configService.UpdateAlertThresholdsAsync(request.Thresholds);

            // Log configuration change
            await auditLogger.LogConfigurationChangeAsync(new ConfigurationChangeEvent
            {
                UserId = context.User.Identity?.Name,
                ClientIp = SecurityAuditLogger.GetClientIpAddress(context),
                UserAgent = SecurityAuditLogger.GetUserAgent(context),
                ConfigurationKey = "SecurityAlertThresholds",
                OldValue = "Previous thresholds",
                NewValue = System.Text.Json.JsonSerializer.Serialize(request.Thresholds),
                ChangeReason = request.Reason
            });

            return Results.Ok(new { Status = "Updated", Message = "Alert thresholds updated successfully" });
        }
        catch (Exception ex)
        {
            return Results.Problem(
                detail: $"Failed to update alert thresholds: {ex.Message}",
                statusCode: 500,
                title: "Configuration Error");
        }
    }

    private static (DateTime StartDate, DateTime EndDate) GetDateRange(DateTime? startDate, DateTime? endDate, TimeSpan defaultPeriod)
    {
        var end = endDate ?? DateTime.UtcNow;
        var start = startDate ?? end.Subtract(defaultPeriod);
        return (start, end);
    }
}

// Supporting request/response models
/// <summary>
/// Request parameters for generating a compliance report.
/// </summary>
public class ComplianceReportGenerationRequest
{
    /// <summary>
    /// Start of the reporting period (UTC).
    /// </summary>
    public DateTime StartDate { get; set; }

    /// <summary>
    /// End of the reporting period (UTC).
    /// </summary>
    public DateTime EndDate { get; set; }

    /// <summary>
    /// Requested report type identifier.
    /// </summary>
    public string ReportType { get; set; } = string.Empty;

    /// <summary>
    /// Optional compliance framework identifier.
    /// </summary>
    public string? ComplianceFramework { get; set; }

    /// <summary>
    /// Whether to include personal data in the report output.
    /// </summary>
    public bool IncludePersonalData { get; set; }

    /// <summary>
    /// Additional filter criteria for report generation.
    /// </summary>
    public Dictionary<string, object> Filters { get; set; } = new();

    /// <summary>
    /// Business justification for generating the report.
    /// </summary>
    public string? BusinessJustification { get; set; }
}

/// <summary>
/// Request parameters for listing compliance reports.
/// </summary>
public class ComplianceReportListRequest
{
    /// <summary>
    /// Page number (1-based).
    /// </summary>
    public int Page { get; set; } = 1;

    /// <summary>
    /// Page size for paged results.
    /// </summary>
    public int PageSize { get; set; } = 20;

    /// <summary>
    /// Optional report type filter.
    /// </summary>
    public string? ReportType { get; set; }

    /// <summary>
    /// Optional start date filter (UTC).
    /// </summary>
    public DateTime? StartDate { get; set; }

    /// <summary>
    /// Optional end date filter (UTC).
    /// </summary>
    public DateTime? EndDate { get; set; }
}

/// <summary>
/// Request parameters for querying security incidents.
/// </summary>
public class SecurityIncidentRequest
{
    /// <summary>
    /// Start of the incident window (UTC).
    /// </summary>
    public DateTime StartDate { get; set; }

    /// <summary>
    /// End of the incident window (UTC).
    /// </summary>
    public DateTime EndDate { get; set; }

    /// <summary>
    /// Optional incident status filter.
    /// </summary>
    public string? Status { get; set; }

    /// <summary>
    /// Optional incident severity filter.
    /// </summary>
    public string? Severity { get; set; }
}

/// <summary>
/// Request parameters for querying security alerts.
/// </summary>
public class SecurityAlertRequest
{
    /// <summary>
    /// Maximum number of alerts to return.
    /// </summary>
    public int Limit { get; set; } = 100;

    /// <summary>
    /// Optional severity filter.
    /// </summary>
    public string? Severity { get; set; }

    /// <summary>
    /// Optional alert type filter.
    /// </summary>
    public string? AlertType { get; set; }

    /// <summary>
    /// Optional alert status filter.
    /// </summary>
    public string? Status { get; set; }
}

/// <summary>
/// Request parameters for querying threat detections.
/// </summary>
public class ThreatDetectionRequest
{
    /// <summary>
    /// Start of the threat detection window (UTC).
    /// </summary>
    public DateTime StartDate { get; set; }

    /// <summary>
    /// End of the threat detection window (UTC).
    /// </summary>
    public DateTime EndDate { get; set; }

    /// <summary>
    /// Optional threat type filter.
    /// </summary>
    public string? ThreatType { get; set; }

    /// <summary>
    /// Optional severity filter.
    /// </summary>
    public string? Severity { get; set; }
}

/// <summary>
/// Request parameters for querying anomaly detections.
/// </summary>
public class AnomalyDetectionRequest
{
    /// <summary>
    /// Start of the anomaly detection window (UTC).
    /// </summary>
    public DateTime StartDate { get; set; }

    /// <summary>
    /// End of the anomaly detection window (UTC).
    /// </summary>
    public DateTime EndDate { get; set; }

    /// <summary>
    /// Optional anomaly type filter.
    /// </summary>
    public string? AnomalyType { get; set; }

    /// <summary>
    /// Optional severity filter.
    /// </summary>
    public string? Severity { get; set; }
}

/// <summary>
/// Request payload for updating alert thresholds.
/// </summary>
public class AlertThresholdsUpdateRequest
{
    /// <summary>
    /// Threshold values keyed by alert type or metric.
    /// </summary>
    public Dictionary<string, object> Thresholds { get; set; } = new();

    /// <summary>
    /// Reason for the threshold change.
    /// </summary>
    public string? Reason { get; set; }
}

// Service interfaces (would be implemented separately)
/// <summary>
/// Service for building compliance dashboard views.
/// </summary>
public interface IComplianceDashboardService
{
    /// <summary>
    /// Gets the executive security dashboard for the specified period.
    /// </summary>
    Task<object> GetExecutiveDashboardAsync(DateTime startDate, DateTime endDate);

    /// <summary>
    /// Gets the technical security dashboard for the specified period.
    /// </summary>
    Task<object> GetTechnicalDashboardAsync(DateTime startDate, DateTime endDate);

    /// <summary>
    /// Gets the compliance dashboard for the specified period and framework.
    /// </summary>
    Task<object> GetComplianceDashboardAsync(string? framework, DateTime startDate, DateTime endDate);
}

/// <summary>
/// Service for generating and retrieving compliance reports.
/// </summary>
public interface IComplianceReportingService
{
    /// <summary>
    /// Gets a paged list of compliance reports.
    /// </summary>
    Task<object> GetComplianceReportsAsync(ComplianceReportListRequest request);

    /// <summary>
    /// Generates a compliance report from the provided request.
    /// </summary>
    Task<ComplianceReport> GenerateReportAsync(ComplianceReportRequest request);

    /// <summary>
    /// Gets a compliance report by identifier.
    /// </summary>
    Task<ComplianceReport?> GetReportAsync(string reportId);
}

/// <summary>
/// Service for security metrics and trend analysis.
/// </summary>
public interface ISecurityMetricsService
{
    /// <summary>
    /// Gets a metrics overview for the specified period.
    /// </summary>
    Task<object> GetMetricsOverviewAsync(DateTime startDate, DateTime endDate);

    /// <summary>
    /// Gets trend analytics for the specified period and granularity.
    /// </summary>
    Task<object> GetSecurityTrendsAsync(DateTime startDate, DateTime endDate, string granularity);
}

/// <summary>
/// Service for querying security incidents.
/// </summary>
public interface ISecurityIncidentService
{
    /// <summary>
    /// Gets security incidents matching the request criteria.
    /// </summary>
    Task<object> GetIncidentsAsync(SecurityIncidentRequest request);
}

/// <summary>
/// Service for querying active security alerts.
/// </summary>
public interface ISecurityAlertService
{
    /// <summary>
    /// Gets active alerts matching the request criteria.
    /// </summary>
    Task<object> GetActiveAlertsAsync(SecurityAlertRequest request);
}

/// <summary>
/// Service for querying threat detections.
/// </summary>
public interface IThreatDetectionService
{
    /// <summary>
    /// Gets threat detections matching the request criteria.
    /// </summary>
    Task<object> GetThreatDetectionsAsync(ThreatDetectionRequest request);
}

/// <summary>
/// Service for querying anomaly detections.
/// </summary>
public interface IAnomalyDetectionService
{
    /// <summary>
    /// Gets anomaly detections matching the request criteria.
    /// </summary>
    Task<object> GetAnomalyDetectionsAsync(AnomalyDetectionRequest request);
}

/// <summary>
/// Service for assessing compliance health status.
/// </summary>
public interface IComplianceHealthService
{
    /// <summary>
    /// Gets overall compliance health status.
    /// </summary>
    Task<object> GetComplianceHealthAsync();
}

/// <summary>
/// Service for enumerating compliance frameworks.
/// </summary>
public interface IComplianceFrameworkService
{
    /// <summary>
    /// Gets supported compliance frameworks.
    /// </summary>
    Task<object> GetSupportedFrameworksAsync();
}

/// <summary>
/// Service for updating security configuration settings.
/// </summary>
public interface ISecurityConfigurationService
{
    /// <summary>
    /// Updates alert threshold settings.
    /// </summary>
    Task UpdateAlertThresholdsAsync(Dictionary<string, object> thresholds);
}
