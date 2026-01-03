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
        var group = app.MapGroup("/api/security/compliance")
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
public class ComplianceReportGenerationRequest
{
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public string ReportType { get; set; } = string.Empty;
    public string? ComplianceFramework { get; set; }
    public bool IncludePersonalData { get; set; }
    public Dictionary<string, object> Filters { get; set; } = new();
    public string? BusinessJustification { get; set; }
}

public class ComplianceReportListRequest
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public string? ReportType { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
}

public class SecurityIncidentRequest
{
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public string? Status { get; set; }
    public string? Severity { get; set; }
}

public class SecurityAlertRequest
{
    public int Limit { get; set; } = 100;
    public string? Severity { get; set; }
    public string? AlertType { get; set; }
    public string? Status { get; set; }
}

public class ThreatDetectionRequest
{
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public string? ThreatType { get; set; }
    public string? Severity { get; set; }
}

public class AnomalyDetectionRequest
{
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public string? AnomalyType { get; set; }
    public string? Severity { get; set; }
}

public class AlertThresholdsUpdateRequest
{
    public Dictionary<string, object> Thresholds { get; set; } = new();
    public string? Reason { get; set; }
}

// Service interfaces (would be implemented separately)
public interface IComplianceDashboardService
{
    Task<object> GetExecutiveDashboardAsync(DateTime startDate, DateTime endDate);
    Task<object> GetTechnicalDashboardAsync(DateTime startDate, DateTime endDate);
    Task<object> GetComplianceDashboardAsync(string? framework, DateTime startDate, DateTime endDate);
}

public interface IComplianceReportingService
{
    Task<object> GetComplianceReportsAsync(ComplianceReportListRequest request);
    Task<ComplianceReport> GenerateReportAsync(ComplianceReportRequest request);
    Task<ComplianceReport?> GetReportAsync(string reportId);
}

public interface ISecurityMetricsService
{
    Task<object> GetMetricsOverviewAsync(DateTime startDate, DateTime endDate);
    Task<object> GetSecurityTrendsAsync(DateTime startDate, DateTime endDate, string granularity);
}

public interface ISecurityIncidentService
{
    Task<object> GetIncidentsAsync(SecurityIncidentRequest request);
}

public interface ISecurityAlertService
{
    Task<object> GetActiveAlertsAsync(SecurityAlertRequest request);
}

public interface IThreatDetectionService
{
    Task<object> GetThreatDetectionsAsync(ThreatDetectionRequest request);
}

public interface IAnomalyDetectionService
{
    Task<object> GetAnomalyDetectionsAsync(AnomalyDetectionRequest request);
}

public interface IComplianceHealthService
{
    Task<object> GetComplianceHealthAsync();
}

public interface IComplianceFrameworkService
{
    Task<object> GetSupportedFrameworksAsync();
}

public interface ISecurityConfigurationService
{
    Task UpdateAlertThresholdsAsync(Dictionary<string, object> thresholds);
}
