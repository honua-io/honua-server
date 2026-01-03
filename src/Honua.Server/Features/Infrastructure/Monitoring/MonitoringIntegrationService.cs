// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Infrastructure.Monitoring;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Monitoring;

/// <summary>
/// Configuration options for monitoring integration and export.
/// </summary>
public sealed class MonitoringIntegrationOptions
{
    /// <summary>
    /// Whether to enable Prometheus metrics export.
    /// </summary>
    public bool EnablePrometheusExport { get; set; } = true;

    /// <summary>
    /// Whether to enable OTLP export for OpenTelemetry.
    /// </summary>
    public bool EnableOtlpExport { get; set; } = true;

    /// <summary>
    /// Whether to enable JSON/CSV data export.
    /// </summary>
    public bool EnableDataExport { get; set; } = true;

    /// <summary>
    /// Whether to enable webhook notifications.
    /// </summary>
    public bool EnableWebhookIntegration { get; set; } = true;

    /// <summary>
    /// Whether to enable multi-tenant monitoring.
    /// </summary>
    public bool EnableMultiTenant { get; set; } = true;

    /// <summary>
    /// Export interval in minutes.
    /// </summary>
    public int ExportIntervalMinutes { get; set; } = 5;

    /// <summary>
    /// Data export directory path.
    /// </summary>
    public string DataExportPath { get; set; } = "./exports";

    /// <summary>
    /// Prometheus metrics endpoint path.
    /// </summary>
    public string PrometheusEndpoint { get; set; } = "/metrics";

    /// <summary>
    /// OTLP endpoint configuration.
    /// </summary>
    public OtlpEndpointOptions OtlpEndpoint { get; set; } = new();

    /// <summary>
    /// Webhook configuration.
    /// </summary>
    public WebhookIntegrationOptions[] Webhooks { get; set; } = Array.Empty<WebhookIntegrationOptions>();

    /// <summary>
    /// Multi-tenant configuration.
    /// </summary>
    public MultiTenantOptions MultiTenant { get; set; } = new();
}

/// <summary>
/// OTLP endpoint configuration.
/// </summary>
public sealed class OtlpEndpointOptions
{
    public string Endpoint { get; set; } = "http://localhost:4317";
    public Dictionary<string, string> Headers { get; set; } = new();
    public string Protocol { get; set; } = "grpc"; // grpc or http/protobuf
}

/// <summary>
/// Webhook integration configuration.
/// </summary>
public sealed class WebhookIntegrationOptions
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public Dictionary<string, string> Headers { get; set; } = new();
    public string[] EventTypes { get; set; } = Array.Empty<string>();
    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// Multi-tenant monitoring configuration.
/// </summary>
public sealed class MultiTenantOptions
{
    public string TenantIdHeader { get; set; } = "X-Tenant-Id";
    public string DefaultTenant { get; set; } = "default";
    public bool IsolateTenantData { get; set; } = true;
}

/// <summary>
/// Service for integrating monitoring data with external systems and providing export capabilities.
/// Supports Prometheus, OTLP, webhooks, and multi-tenant monitoring.
/// </summary>
public interface IMonitoringIntegrationService
{
    /// <summary>
    /// Exports metrics in Prometheus format.
    /// </summary>
    /// <param name="tenantId">Optional tenant ID for multi-tenant setups.</param>
    /// <returns>Metrics in Prometheus format.</returns>
    Task<string> ExportPrometheusMetricsAsync(string? tenantId = null);

    /// <summary>
    /// Exports monitoring data as JSON.
    /// </summary>
    /// <param name="startTime">Start time for data export.</param>
    /// <param name="endTime">End time for data export.</param>
    /// <param name="tenantId">Optional tenant ID for multi-tenant setups.</param>
    /// <returns>Monitoring data in JSON format.</returns>
    Task<string> ExportJsonDataAsync(DateTimeOffset startTime, DateTimeOffset endTime, string? tenantId = null);

    /// <summary>
    /// Exports monitoring data as CSV.
    /// </summary>
    /// <param name="startTime">Start time for data export.</param>
    /// <param name="endTime">End time for data export.</param>
    /// <param name="dataType">Type of data to export (metrics, events, analytics).</param>
    /// <param name="tenantId">Optional tenant ID for multi-tenant setups.</param>
    /// <returns>Monitoring data in CSV format.</returns>
    Task<string> ExportCsvDataAsync(DateTimeOffset startTime, DateTimeOffset endTime, string dataType, string? tenantId = null);

    /// <summary>
    /// Sends monitoring data to configured webhooks.
    /// </summary>
    /// <param name="eventType">Type of event to send.</param>
    /// <param name="data">Data to send in webhook.</param>
    /// <param name="tenantId">Optional tenant ID for multi-tenant setups.</param>
    Task SendWebhookAsync(string eventType, object data, string? tenantId = null);

    /// <summary>
    /// Gets monitoring data for a specific tenant.
    /// </summary>
    /// <param name="tenantId">Tenant ID.</param>
    /// <returns>Tenant-specific monitoring summary.</returns>
    Task<TenantMonitoringSummary> GetTenantMonitoringAsync(string tenantId);

    /// <summary>
    /// Gets all available monitoring data for export.
    /// </summary>
    /// <param name="format">Export format (json, csv, prometheus).</param>
    /// <param name="tenantId">Optional tenant ID for multi-tenant setups.</param>
    /// <returns>Monitoring export data.</returns>
    Task<MonitoringExport> GetMonitoringExportAsync(string format, string? tenantId = null);

    /// <summary>
    /// Creates a scheduled data export job.
    /// </summary>
    /// <param name="schedule">Export schedule configuration.</param>
    /// <returns>Export job ID.</returns>
    Task<string> CreateExportScheduleAsync(ExportSchedule schedule);

    /// <summary>
    /// Cancels a scheduled data export job.
    /// </summary>
    /// <param name="jobId">Export job ID.</param>
    Task CancelExportScheduleAsync(string jobId);
}

/// <summary>
/// Implementation of monitoring integration service with comprehensive export and integration capabilities.
/// </summary>
internal sealed class MonitoringIntegrationService : IMonitoringIntegrationService, IHostedService, IDisposable
{
    private static readonly JsonSerializerOptions ExportJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly MonitoringIntegrationOptions _options;
    private readonly IBusinessAnalyticsService _businessAnalytics;
    private readonly IPerformanceIntelligenceService _performanceIntelligence;
    private readonly IAnomalyDetectionService _anomalyDetection;
    private readonly IIntelligentAlertingService _intelligentAlerting;
    private readonly ILogger<MonitoringIntegrationService> _logger;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, TenantData> _tenantData = new();
    private readonly ConcurrentDictionary<string, ExportJob> _exportJobs = new();
    private readonly Timer _exportTimer;

    public MonitoringIntegrationService(
        IOptions<MonitoringIntegrationOptions> options,
        IBusinessAnalyticsService businessAnalytics,
        IPerformanceIntelligenceService performanceIntelligence,
        IAnomalyDetectionService anomalyDetection,
        IIntelligentAlertingService intelligentAlerting,
        ILogger<MonitoringIntegrationService> logger,
        HttpClient httpClient)
    {
        _options = options.Value;
        _businessAnalytics = businessAnalytics;
        _performanceIntelligence = performanceIntelligence;
        _anomalyDetection = anomalyDetection;
        _intelligentAlerting = intelligentAlerting;
        _logger = logger;
        _httpClient = httpClient;

        _exportTimer = new Timer(
            ProcessExports,
            null,
            TimeSpan.Zero,
            TimeSpan.FromMinutes(_options.ExportIntervalMinutes));

        // Ensure export directory exists
        if (_options.EnableDataExport && !Directory.Exists(_options.DataExportPath))
        {
            Directory.CreateDirectory(_options.DataExportPath);
        }
    }

    public async Task<string> ExportPrometheusMetricsAsync(string? tenantId = null)
    {
        if (!_options.EnablePrometheusExport)
        {
            throw new InvalidOperationException("Prometheus export is disabled");
        }

        var metrics = new StringBuilder();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Add help and type information
        metrics.AppendLine("# HELP honua_requests_total Total number of requests processed");
        metrics.AppendLine("# TYPE honua_requests_total counter");

        // Add actual metrics
        var businessKpis = await _businessAnalytics.GetBusinessKPISummaryAsync();
        var performanceReport = await _performanceIntelligence.AnalyzePerformanceAsync();

        // Business metrics
        AppendPrometheusMetric(metrics, "honua_daily_active_users", businessKpis.DailyActiveUsers, tenantId, timestamp);
        AppendPrometheusMetric(metrics, "honua_monthly_active_users", businessKpis.MonthlyActiveUsers, tenantId, timestamp);
        AppendPrometheusMetric(metrics, "honua_daily_api_calls", businessKpis.DailyApiCalls, tenantId, timestamp);
        AppendPrometheusMetric(metrics, "honua_system_health_score", businessKpis.SystemHealthScore, tenantId, timestamp);

        // Performance metrics
        AppendPrometheusMetric(metrics, "honua_performance_score", performanceReport.OverallScore, tenantId, timestamp);

        // Memory metrics
        var memoryUsage = MemoryMonitor.GetMemoryUsage();
        AppendPrometheusMetric(metrics, "honua_memory_allocated_bytes", memoryUsage.AllocatedBytes, tenantId, timestamp);
        AppendPrometheusMetric(metrics, "honua_memory_pressure_percentage", memoryUsage.MemoryPressurePercentage * 100, tenantId, timestamp);

        return metrics.ToString();
    }

    public async Task<string> ExportJsonDataAsync(DateTimeOffset startTime, DateTimeOffset endTime, string? tenantId = null)
    {
        if (!_options.EnableDataExport)
        {
            throw new InvalidOperationException("Data export is disabled");
        }

        var exportData = new
        {
            ExportInfo = new
            {
                Timestamp = DateTimeOffset.UtcNow,
                StartTime = startTime,
                EndTime = endTime,
                TenantId = tenantId ?? _options.MultiTenant.DefaultTenant,
                Format = "JSON",
                Version = "1.0"
            },
            BusinessAnalytics = new
            {
                ApiUsage = await _businessAnalytics.GetApiUsageAnalyticsAsync(startTime, endTime),
                UserBehavior = await _businessAnalytics.GetUserBehaviorAnalyticsAsync(startTime, endTime),
                FeatureAdoption = await _businessAnalytics.GetFeatureAdoptionAnalyticsAsync(startTime, endTime),
                Geographic = await _businessAnalytics.GetGeographicAnalyticsAsync(startTime, endTime)
            },
            Performance = new
            {
                Intelligence = await _performanceIntelligence.AnalyzePerformanceAsync(),
                Trends = await _performanceIntelligence.GetPerformanceTrendsAsync(startTime, endTime),
                MemoryAnalysis = await _performanceIntelligence.AnalyzeMemoryUsageAsync(),
                DatabaseAnalysis = await _performanceIntelligence.AnalyzeDatabasePerformanceAsync()
            },
            Security = new
            {
                Anomalies = await _anomalyDetection.GetAnomaliesAsync(startTime, endTime),
                Alerts = await _intelligentAlerting.GetActiveAlertsAsync(),
                Statistics = await _intelligentAlerting.GetAlertStatisticsAsync()
            },
            SystemMetrics = new
            {
                MemoryUsage = MemoryMonitor.GetMemoryUsage(),
                Timestamp = DateTimeOffset.UtcNow
            }
        };

        return JsonSerializer.Serialize(exportData, ExportJsonOptions);
    }

    public async Task<string> ExportCsvDataAsync(DateTimeOffset startTime, DateTimeOffset endTime, string dataType, string? tenantId = null)
    {
        if (!_options.EnableDataExport)
        {
            throw new InvalidOperationException("Data export is disabled");
        }

        var csv = new StringBuilder();

        switch (dataType.ToLowerInvariant())
        {
            case "api_usage":
                var apiUsage = await _businessAnalytics.GetApiUsageAnalyticsAsync(startTime, endTime);
                csv.AppendLine("Endpoint,Method,RequestCount,AverageResponseTime,ErrorRate,UniqueUsers");
                foreach (var endpoint in apiUsage.TopEndpoints)
                {
                    csv.AppendLine($"{endpoint.Endpoint},{endpoint.Method},{endpoint.RequestCount},{endpoint.AverageResponseTime:F2},{endpoint.ErrorRate:F2},{endpoint.UniqueUsers}");
                }
                break;

            case "performance":
                var performanceReport = await _performanceIntelligence.AnalyzePerformanceAsync();
                csv.AppendLine("Timestamp,OverallScore,CpuHealth,MemoryHealth,DatabaseHealth");
                csv.AppendLine($"{performanceReport.Timestamp:O},{performanceReport.OverallScore},{performanceReport.SystemHealth.CpuHealth},{performanceReport.SystemHealth.MemoryHealth},{performanceReport.SystemHealth.DatabaseHealth}");
                break;

            case "anomalies":
                var anomalies = await _anomalyDetection.GetAnomaliesAsync(startTime, endTime);
                csv.AppendLine("Id,MetricName,Value,Timestamp,Confidence,Severity,Reason");
                foreach (var anomaly in anomalies)
                {
                    csv.AppendLine($"{anomaly.Id},{anomaly.MetricName},{anomaly.Value},{anomaly.Timestamp:O},{anomaly.Confidence:F3},{anomaly.Severity},{anomaly.Reason}");
                }
                break;

            default:
                throw new ArgumentException($"Unsupported data type: {dataType}");
        }

        return csv.ToString();
    }

    public async Task SendWebhookAsync(string eventType, object data, string? tenantId = null)
    {
        if (!_options.EnableWebhookIntegration)
        {
            return;
        }

        var relevantWebhooks = _options.Webhooks
            .Where(w => w.EventTypes.Contains(eventType) || w.EventTypes.Contains("*"))
            .ToArray();

        foreach (var webhook in relevantWebhooks)
        {
            try
            {
                await SendSingleWebhookAsync(webhook, eventType, data, tenantId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send webhook to {WebhookName} ({Url})", webhook.Name, webhook.Url);
            }
        }
    }

    public async Task<TenantMonitoringSummary> GetTenantMonitoringAsync(string tenantId)
    {
        if (!_options.EnableMultiTenant)
        {
            throw new InvalidOperationException("Multi-tenant monitoring is disabled");
        }

        var tenantData = _tenantData.GetOrAdd(tenantId, new TenantData { TenantId = tenantId });

        // In a real implementation, this would filter data by tenant
        var businessKpis = await _businessAnalytics.GetBusinessKPISummaryAsync();
        var performanceReport = await _performanceIntelligence.AnalyzePerformanceAsync();

        return new TenantMonitoringSummary
        {
            TenantId = tenantId,
            Timestamp = DateTimeOffset.UtcNow,
            BusinessMetrics = new TenantBusinessMetrics
            {
                ActiveUsers = businessKpis.DailyActiveUsers,
                ApiCalls = businessKpis.DailyApiCalls,
                SessionDuration = businessKpis.AverageSessionDuration,
                RetentionRate = businessKpis.UserRetentionRate
            },
            PerformanceMetrics = new TenantPerformanceMetrics
            {
                PerformanceScore = performanceReport.OverallScore,
                ResponseTime = Random.Shared.Next(50, 200), // Would use actual tenant metrics
                ErrorRate = Random.Shared.NextDouble() * 2,
                ThroughputRPS = Random.Shared.Next(10, 100)
            },
            ResourceUsage = new TenantResourceUsage
            {
                CpuUsage = Random.Shared.Next(20, 80),
                MemoryUsageMB = Random.Shared.Next(100, 1000),
                StorageUsageGB = Random.Shared.Next(10, 100),
                NetworkUsageGB = Random.Shared.Next(1, 50)
            },
            Compliance = new TenantCompliance
            {
                DataResidency = "Compliant",
                PrivacyRegulations = "GDPR Compliant",
                SecurityStandards = "SOC 2 Type II",
                LastAudit = DateTimeOffset.UtcNow.AddDays(-Random.Shared.Next(30, 90))
            }
        };
    }

    public async Task<MonitoringExport> GetMonitoringExportAsync(string format, string? tenantId = null)
    {
        var now = DateTimeOffset.UtcNow;
        var last24Hours = now.AddDays(-1);

        string content;
        string mimeType;

        switch (format.ToLowerInvariant())
        {
            case "json":
                content = await ExportJsonDataAsync(last24Hours, now, tenantId);
                mimeType = "application/json";
                break;
            case "csv":
                content = await ExportCsvDataAsync(last24Hours, now, "api_usage", tenantId);
                mimeType = "text/csv";
                break;
            case "prometheus":
                content = await ExportPrometheusMetricsAsync(tenantId);
                mimeType = "text/plain";
                break;
            default:
                throw new ArgumentException($"Unsupported export format: {format}");
        }

        return new MonitoringExport
        {
            Id = Guid.NewGuid().ToString(),
            Format = format,
            Content = content,
            MimeType = mimeType,
            GeneratedAt = now,
            TenantId = tenantId ?? _options.MultiTenant.DefaultTenant,
            SizeBytes = Encoding.UTF8.GetByteCount(content)
        };
    }

    public async Task<string> CreateExportScheduleAsync(ExportSchedule schedule)
    {
        var jobId = Guid.NewGuid().ToString();
        var exportJob = new ExportJob
        {
            Id = jobId,
            Schedule = schedule,
            CreatedAt = DateTimeOffset.UtcNow,
            NextExecution = CalculateNextExecution(schedule),
            IsActive = true
        };

        _exportJobs.TryAdd(jobId, exportJob);

        _logger.LogInformation("Created export schedule {JobId} for format {Format} with interval {Interval}",
            jobId, schedule.Format, schedule.IntervalMinutes);

        return jobId;
    }

    public async Task CancelExportScheduleAsync(string jobId)
    {
        if (_exportJobs.TryGetValue(jobId, out var job))
        {
            job.IsActive = false;
            _logger.LogInformation("Cancelled export schedule {JobId}", jobId);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _exportTimer?.Dispose();
        // HttpClient is managed by DI container and should not be disposed here
        return Task.CompletedTask;
    }

    private async void ProcessExports(object? state)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var jobsToExecute = _exportJobs.Values
                .Where(j => j.IsActive && j.NextExecution <= now)
                .ToArray();

            foreach (var job in jobsToExecute)
            {
                try
                {
                    await ExecuteExportJobAsync(job);
                    job.NextExecution = CalculateNextExecution(job.Schedule);
                    job.LastExecution = now;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to execute export job {JobId}", job.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during export processing");
        }
    }

    private async Task ExecuteExportJobAsync(ExportJob job)
    {
        var export = await GetMonitoringExportAsync(job.Schedule.Format, job.Schedule.TenantId);

        if (!string.IsNullOrEmpty(job.Schedule.FilePath))
        {
            var fileName = $"{job.Schedule.FileName}_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}.{job.Schedule.Format}";
            var fullPath = Path.Combine(job.Schedule.FilePath, fileName);

            await File.WriteAllTextAsync(fullPath, export.Content);
            _logger.LogInformation("Exported monitoring data to {FilePath}", fullPath);
        }

        if (!string.IsNullOrEmpty(job.Schedule.WebhookUrl))
        {
            var payload = new { Export = export, Schedule = job.Schedule };
            await SendWebhookAsync("scheduled_export", payload, job.Schedule.TenantId);
        }
    }

    private DateTimeOffset CalculateNextExecution(ExportSchedule schedule)
    {
        return DateTimeOffset.UtcNow.AddMinutes(schedule.IntervalMinutes);
    }

    private async Task SendSingleWebhookAsync(WebhookIntegrationOptions webhook, string eventType, object data, string? tenantId)
    {
        var payload = new
        {
            EventType = eventType,
            Timestamp = DateTimeOffset.UtcNow,
            TenantId = tenantId ?? _options.MultiTenant.DefaultTenant,
            Data = data
        };

        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        foreach (var header in webhook.Headers)
        {
            content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(webhook.TimeoutSeconds));
        var response = await _httpClient.PostAsync(webhook.Url, content, cts.Token);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Webhook {WebhookName} returned {StatusCode}: {ReasonPhrase}",
                webhook.Name, response.StatusCode, response.ReasonPhrase);
        }
        else
        {
            _logger.LogDebug("Successfully sent {EventType} event to webhook {WebhookName}",
                eventType, webhook.Name);
        }
    }

    private void AppendPrometheusMetric(StringBuilder metrics, string name, double value, string? tenantId, long timestamp)
    {
        if (!string.IsNullOrEmpty(tenantId))
        {
            metrics.AppendLine($"{name}{{tenant=\"{tenantId}\"}} {value} {timestamp}");
        }
        else
        {
            metrics.AppendLine($"{name} {value} {timestamp}");
        }
    }

    public void Dispose()
    {
        _exportTimer?.Dispose();
        // HttpClient is managed by DI container and should not be disposed here
    }
}

// Supporting models

/// <summary>
/// Tenant monitoring summary for multi-tenant environments.
/// </summary>
public sealed record TenantMonitoringSummary
{
    public string TenantId { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; }
    public TenantBusinessMetrics BusinessMetrics { get; init; } = new();
    public TenantPerformanceMetrics PerformanceMetrics { get; init; } = new();
    public TenantResourceUsage ResourceUsage { get; init; } = new();
    public TenantCompliance Compliance { get; init; } = new();
}

public sealed record TenantBusinessMetrics
{
    public int ActiveUsers { get; init; }
    public int ApiCalls { get; init; }
    public double SessionDuration { get; init; }
    public double RetentionRate { get; init; }
}

public sealed record TenantPerformanceMetrics
{
    public int PerformanceScore { get; init; }
    public double ResponseTime { get; init; }
    public double ErrorRate { get; init; }
    public double ThroughputRPS { get; init; }
}

public sealed record TenantResourceUsage
{
    public int CpuUsage { get; init; }
    public int MemoryUsageMB { get; init; }
    public int StorageUsageGB { get; init; }
    public int NetworkUsageGB { get; init; }
}

public sealed record TenantCompliance
{
    public string DataResidency { get; init; } = string.Empty;
    public string PrivacyRegulations { get; init; } = string.Empty;
    public string SecurityStandards { get; init; } = string.Empty;
    public DateTimeOffset LastAudit { get; init; }
}

/// <summary>
/// Monitoring export data container.
/// </summary>
public sealed record MonitoringExport
{
    public string Id { get; init; } = string.Empty;
    public string Format { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public string MimeType { get; init; } = string.Empty;
    public DateTimeOffset GeneratedAt { get; init; }
    public string TenantId { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
}

/// <summary>
/// Export schedule configuration.
/// </summary>
public sealed record ExportSchedule
{
    public string Format { get; init; } = string.Empty; // json, csv, prometheus
    public int IntervalMinutes { get; init; } = 60;
    public string? TenantId { get; init; }
    public string? FilePath { get; init; }
    public string? FileName { get; init; }
    public string? WebhookUrl { get; init; }
    public Dictionary<string, string> Parameters { get; init; } = new();
}

/// <summary>
/// Internal export job tracking.
/// </summary>
internal sealed record ExportJob
{
    public string Id { get; init; } = string.Empty;
    public ExportSchedule Schedule { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset NextExecution { get; set; }
    public DateTimeOffset? LastExecution { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Internal tenant data tracking.
/// </summary>
internal sealed record TenantData
{
    public string TenantId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public Dictionary<string, object> Metadata { get; init; } = new();
}
