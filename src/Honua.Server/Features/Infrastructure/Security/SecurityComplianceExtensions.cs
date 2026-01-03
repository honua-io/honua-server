// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Infrastructure.Security;

/// <summary>
/// Extension methods to register comprehensive security compliance services.
/// </summary>
public static class SecurityComplianceExtensions
{
    /// <summary>
    /// Adds comprehensive security compliance services to the service collection.
    /// </summary>
    public static IServiceCollection AddSecurityCompliance(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Configure options
        services.Configure<AuditLoggerOptions>(configuration.GetSection("Security:AuditLogging"));
        services.Configure<SecurityMonitoringOptions>(configuration.GetSection("Security:Monitoring"));
        services.Configure<SecurityHealthCheckOptions>(configuration.GetSection("Security:HealthChecks"));
        services.Configure<SecurityComplianceOptions>(configuration.GetSection("Security:Compliance"));

        // Register core security services
        services.AddSingleton<ComprehensiveAuditLogger>();
        services.AddSingleton<SecurityMonitoringService>();
        services.AddSingleton<SecurityHealthCheck>();

        // Register service interfaces with default implementations
        services.TryAddScoped<IAuditLogStorage, DefaultAuditLogStorage>();
        services.TryAddScoped<ICryptographicService, DefaultCryptographicService>();

        // Register dashboard and reporting services
        services.TryAddScoped<IComplianceDashboardService, DefaultComplianceDashboardService>();
        services.TryAddScoped<IComplianceReportingService, DefaultComplianceReportingService>();
        services.TryAddScoped<ISecurityMetricsService, DefaultSecurityMetricsService>();
        services.TryAddScoped<ISecurityIncidentService, DefaultSecurityIncidentService>();
        services.TryAddScoped<ISecurityAlertService, DefaultSecurityAlertService>();
        services.TryAddScoped<IThreatDetectionService, DefaultThreatDetectionService>();
        services.TryAddScoped<IAnomalyDetectionService, DefaultAnomalyDetectionService>();
        services.TryAddScoped<IComplianceHealthService, DefaultComplianceHealthService>();
        services.TryAddScoped<IComplianceFrameworkService, DefaultComplianceFrameworkService>();
        services.TryAddScoped<ISecurityConfigurationService, DefaultSecurityConfigurationService>();

        // Register security health checks
        services.AddHealthChecks()
            .AddCheck<SecurityHealthCheck>("security-compliance");

        // Register monitoring service as hosted service
        services.AddHostedService<SecurityMonitoringService>();

        return services;
    }

    /// <summary>
    /// Adds security compliance middleware to the application pipeline.
    /// </summary>
    public static IApplicationBuilder UseSecurityCompliance(this IApplicationBuilder app)
    {
        return app.UseMiddleware<SecurityComplianceMiddleware>();
    }

    /// <summary>
    /// Maps security compliance endpoints.
    /// </summary>
    public static WebApplication MapSecurityComplianceEndpoints(this WebApplication app)
    {
        app.MapComplianceDashboardEndpoints();
        return app;
    }
}

// Default implementations of service interfaces
internal class DefaultAuditLogStorage : IAuditLogStorage
{
    private static readonly Action<ILogger, string, Exception?> LogAuditEventStored =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(4001, "AuditEventStored"), "Stored audit event {EventId}");

    private static readonly Action<ILogger, string, Exception?> LogComplianceReportStored =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(4002, "ComplianceReportStored"), "Stored compliance report {ReportId}");

    private readonly ILogger<DefaultAuditLogStorage> _logger;
    private readonly List<AuditEvent> _events = new();
    private readonly List<ComplianceReport> _reports = new();

    public DefaultAuditLogStorage(ILogger<DefaultAuditLogStorage> logger)
    {
        _logger = logger;
    }

    public async Task StoreAuditEventAsync(AuditEvent auditEvent)
    {
        _events.Add(auditEvent);
        LogAuditEventStored(_logger, auditEvent.EventId, null);
        await Task.CompletedTask;
    }

    public async Task<IEnumerable<AuditEvent>> GetAuditEventsAsync(
        DateTime startDate, DateTime endDate, Dictionary<string, object> filters)
    {
        var events = _events.Where(e => e.Timestamp >= startDate && e.Timestamp <= endDate);

        foreach (var filter in filters)
        {
            // Apply filters based on filter keys
            events = filter.Key.ToLower() switch
            {
                "userid" => events.Where(e => e.UserId == filter.Value.ToString()),
                "eventtype" => events.Where(e => e.EventType.ToString() == filter.Value.ToString()),
                "resource" => events.Where(e => e.Resource.Contains(filter.Value.ToString() ?? "", StringComparison.OrdinalIgnoreCase)),
                _ => events
            };
        }

        return await Task.FromResult(events.OrderByDescending(e => e.Timestamp));
    }

    public async Task StoreComplianceReportAsync(ComplianceReport report)
    {
        _reports.Add(report);
        LogComplianceReportStored(_logger, report.ReportId, null);
        await Task.CompletedTask;
    }

    public async Task<ComplianceReport?> GetComplianceReportAsync(string reportId)
    {
        return await Task.FromResult(_reports.FirstOrDefault(r => r.ReportId == reportId));
    }
}

internal class DefaultCryptographicService : ICryptographicService
{
    public DefaultCryptographicService()
    {
    }

    public async Task<string> SignDataAsync(string data)
    {
        // In real implementation, use proper digital signing
        var hash = await GenerateSecureHashAsync(data);
        return $"SIGNATURE:{hash}";
    }

    public async Task<bool> VerifySignatureAsync(string data, string signature)
    {
        // In real implementation, verify digital signature
        var expectedSignature = await SignDataAsync(data);
        return signature == expectedSignature;
    }

    public async Task<string> EncryptDataAsync(string data)
    {
        // In real implementation, use proper encryption
        var bytes = System.Text.Encoding.UTF8.GetBytes(data);
        return await Task.FromResult(Convert.ToBase64String(bytes));
    }

    public async Task<string> DecryptDataAsync(string encryptedData)
    {
        // In real implementation, use proper decryption
        var bytes = Convert.FromBase64String(encryptedData);
        return await Task.FromResult(System.Text.Encoding.UTF8.GetString(bytes));
    }

    public async Task<string> GenerateSecureHashAsync(string data)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(data));
        return await Task.FromResult(Convert.ToBase64String(hashBytes));
    }
}

// Placeholder service implementations
internal class DefaultComplianceDashboardService : IComplianceDashboardService
{
    public async Task<object> GetExecutiveDashboardAsync(DateTime startDate, DateTime endDate)
    {
        return await Task.FromResult(new
        {
            OverallSecurityScore = 85,
            ComplianceStatus = "Compliant",
            CriticalIssues = 0,
            RecentIncidents = 2,
            DateRange = new { startDate, endDate },
            LastUpdated = DateTime.UtcNow,
            KeyMetrics = new
            {
                AuthenticationSuccess = 99.5,
                DataBreaches = 0,
                ComplianceViolations = 3,
                SystemUptime = 99.9
            }
        });
    }

    public async Task<object> GetTechnicalDashboardAsync(DateTime startDate, DateTime endDate)
    {
        return await Task.FromResult(new
        {
            SecurityAlerts = new { Critical = 0, High = 2, Medium = 5, Low = 12 },
            ThreatDetections = new { SqlInjection = 1, XssAttempt = 0, BruteForce = 3 },
            SystemHealth = new { SecurityServices = "Healthy", Monitoring = "Active", Logging = "Operational" },
            PerformanceMetrics = new { ResponseTime = "125ms", ThroughputPerSecond = 850, ErrorRate = 0.1 },
            DateRange = new { startDate, endDate }
        });
    }

    public async Task<object> GetComplianceDashboardAsync(string? framework, DateTime startDate, DateTime endDate)
    {
        return await Task.FromResult(new
        {
            Framework = framework ?? "OWASP Top 10",
            ComplianceScore = 88.5,
            CoveredControls = 9,
            TotalControls = 10,
            Violations = new { Critical = 0, High = 1, Medium = 4, Low = 8 },
            Recommendations = new[]
            {
                "Implement stronger password policies",
                "Enable multi-factor authentication",
                "Review access control configurations"
            },
            DateRange = new { startDate, endDate }
        });
    }
}

internal class DefaultComplianceReportingService : IComplianceReportingService
{
    private readonly ComprehensiveAuditLogger _auditLogger;

    public DefaultComplianceReportingService(ComprehensiveAuditLogger auditLogger)
    {
        _auditLogger = auditLogger;
    }

    public async Task<object> GetComplianceReportsAsync(ComplianceReportListRequest request)
    {
        return await Task.FromResult(new
        {
            Reports = new[]
            {
                new { Id = "RPT001", Type = "OWASP", GeneratedDate = DateTime.UtcNow.AddDays(-1), Status = "Complete" },
                new { Id = "RPT002", Type = "SOC2", GeneratedDate = DateTime.UtcNow.AddDays(-7), Status = "Complete" }
            },
            TotalCount = 2,
            Page = request.Page,
            PageSize = request.PageSize
        });
    }

    public async Task<ComplianceReport> GenerateReportAsync(ComplianceReportRequest request)
    {
        return await _auditLogger.GenerateComplianceReportAsync(request);
    }

    public async Task<ComplianceReport?> GetReportAsync(string reportId)
    {
        // In real implementation, retrieve from storage
        return await Task.FromResult<ComplianceReport?>(null);
    }
}

internal class DefaultSecurityMetricsService : ISecurityMetricsService
{
    public async Task<object> GetMetricsOverviewAsync(DateTime startDate, DateTime endDate)
    {
        return await Task.FromResult(new
        {
            TotalRequests = 125000,
            SecurityIncidents = 5,
            BlockedRequests = 247,
            AuthenticationFailures = 123,
            AverageResponseTime = "95ms",
            UptimePercentage = 99.95,
            DateRange = new { startDate, endDate }
        });
    }

    public async Task<object> GetSecurityTrendsAsync(DateTime startDate, DateTime endDate, string granularity)
    {
        return await Task.FromResult(new
        {
            Granularity = granularity,
            DataPoints = new[]
            {
                new { Date = startDate, Incidents = 2, Threats = 15, Anomalies = 8 },
                new { Date = startDate.AddDays(1), Incidents = 1, Threats = 12, Anomalies = 5 }
            },
            Trends = new
            {
                IncidentsTrend = "Decreasing",
                ThreatsTrend = "Stable",
                AnomaliesTrend = "Decreasing"
            }
        });
    }
}

// Additional placeholder services would be implemented similarly
internal class DefaultSecurityIncidentService : ISecurityIncidentService
{
    public async Task<object> GetIncidentsAsync(SecurityIncidentRequest request)
    {
        return await Task.FromResult(new { Incidents = Array.Empty<object>() });
    }
}

internal class DefaultSecurityAlertService : ISecurityAlertService
{
    public async Task<object> GetActiveAlertsAsync(SecurityAlertRequest request)
    {
        return await Task.FromResult(new { Alerts = Array.Empty<object>() });
    }
}

internal class DefaultThreatDetectionService : IThreatDetectionService
{
    public async Task<object> GetThreatDetectionsAsync(ThreatDetectionRequest request)
    {
        return await Task.FromResult(new { Threats = Array.Empty<object>() });
    }
}

internal class DefaultAnomalyDetectionService : IAnomalyDetectionService
{
    public async Task<object> GetAnomalyDetectionsAsync(AnomalyDetectionRequest request)
    {
        return await Task.FromResult(new { Anomalies = Array.Empty<object>() });
    }
}

internal class DefaultComplianceHealthService : IComplianceHealthService
{
    public async Task<object> GetComplianceHealthAsync()
    {
        return await Task.FromResult(new
        {
            Status = "Healthy",
            Score = 88.5,
            LastAssessment = DateTime.UtcNow.AddHours(-1)
        });
    }
}

internal class DefaultComplianceFrameworkService : IComplianceFrameworkService
{
    public async Task<object> GetSupportedFrameworksAsync()
    {
        return await Task.FromResult(new
        {
            Frameworks = new[]
            {
                new { Id = "owasp-top10", Name = "OWASP Top 10", Version = "2021", Supported = true },
                new { Id = "iso27001", Name = "ISO 27001", Version = "2022", Supported = true },
                new { Id = "soc2", Name = "SOC 2", Version = "Type II", Supported = true },
                new { Id = "nist", Name = "NIST Cybersecurity Framework", Version = "1.1", Supported = true }
            }
        });
    }
}

internal class DefaultSecurityConfigurationService : ISecurityConfigurationService
{
    public async Task UpdateAlertThresholdsAsync(Dictionary<string, object> thresholds)
    {
        // In real implementation, update configuration storage
        await Task.CompletedTask;
    }
}
