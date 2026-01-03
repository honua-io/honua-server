// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Security;

/// <summary>
/// Comprehensive audit logging system with integrity protection and compliance features.
/// Extends the existing SecurityAuditLogger with enterprise-grade audit capabilities.
/// </summary>
public partial class ComprehensiveAuditLogger
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly ILogger<ComprehensiveAuditLogger> _logger;
    private readonly AuditLoggerOptions _options;
    private readonly IAuditLogStorage _storage;
    private readonly ICryptographicService _cryptoService;

    public ComprehensiveAuditLogger(
        ILogger<ComprehensiveAuditLogger> logger,
        IOptions<AuditLoggerOptions> options,
        IAuditLogStorage storage,
        ICryptographicService cryptoService)
    {
        _logger = logger;
        _options = options.Value;
        _storage = storage;
        _cryptoService = cryptoService;
    }

    /// <summary>
    /// Logs comprehensive audit events with integrity protection.
    /// </summary>
    public async Task LogAuditEventAsync(AuditEvent auditEvent)
    {
        try
        {
            // Enrich event with additional metadata
            EnrichAuditEvent(auditEvent);

            // Add integrity protection if enabled
            if (_options.EnableIntegrityProtection)
            {
                auditEvent.IntegrityHash = GenerateIntegrityHash(auditEvent);
                auditEvent.DigitalSignature = await _cryptoService.SignDataAsync(auditEvent.ToString());
            }

            // Store in multiple destinations
            await StoreAuditEventAsync(auditEvent);

            // Log for immediate visibility
            LogAuditEventToLogger(auditEvent);

            // Check for compliance violations
            await CheckComplianceViolationsAsync(auditEvent);
        }
        catch (Exception ex)
        {
            FailedToLogAuditEvent(_logger, auditEvent.EventType.ToString(), ex);
            // Never throw from audit logging to avoid impacting application flow
        }
    }

    /// <summary>
    /// Logs data access events for compliance and auditing.
    /// </summary>
    public async Task LogDataAccessAsync(DataAccessEvent accessEvent)
    {
        var auditEvent = new AuditEvent
        {
            EventType = AuditEventType.DataAccess,
            Severity = AuditSeverity.Info,
            UserId = accessEvent.UserId,
            ClientIp = accessEvent.ClientIp,
            UserAgent = accessEvent.UserAgent,
            Resource = accessEvent.Resource,
            Action = accessEvent.Action,
            AdditionalData = new Dictionary<string, object>
            {
                ["DataType"] = accessEvent.DataType,
                ["RecordCount"] = accessEvent.RecordCount,
                ["QueryParameters"] = accessEvent.QueryParameters ?? new Dictionary<string, object>(),
                ["ResultSize"] = accessEvent.ResultSize,
                ["ExecutionTimeMs"] = accessEvent.ExecutionTimeMs
            }
        };

        await LogAuditEventAsync(auditEvent);

        // Additional security checks for sensitive data
        if (_options.SensitiveDataTypes.Contains(accessEvent.DataType, StringComparer.OrdinalIgnoreCase))
        {
            await LogSensitiveDataAccessAsync(accessEvent);
        }
    }

    /// <summary>
    /// Logs configuration changes for security audit trail.
    /// </summary>
    public async Task LogConfigurationChangeAsync(ConfigurationChangeEvent changeEvent)
    {
        var auditEvent = new AuditEvent
        {
            EventType = AuditEventType.ConfigurationChange,
            Severity = DetermineSeverityForConfigChange(changeEvent),
            UserId = changeEvent.UserId,
            ClientIp = changeEvent.ClientIp,
            UserAgent = changeEvent.UserAgent,
            Resource = changeEvent.ConfigurationKey,
            Action = "UPDATE",
            AdditionalData = new Dictionary<string, object>
            {
                ["ConfigurationKey"] = changeEvent.ConfigurationKey,
                ["OldValue"] = MaskSensitiveValue(changeEvent.OldValue ?? string.Empty),
                ["NewValue"] = MaskSensitiveValue(changeEvent.NewValue ?? string.Empty),
                ["ChangeReason"] = changeEvent.ChangeReason ?? string.Empty,
                ["ApprovalId"] = changeEvent.ApprovalId ?? string.Empty
            }
        };

        await LogAuditEventAsync(auditEvent);

        // Alert on critical security configuration changes
        if (IsCriticalSecurityConfig(changeEvent.ConfigurationKey))
        {
            await AlertOnCriticalConfigChangeAsync(changeEvent);
        }
    }

    /// <summary>
    /// Logs authentication events with enhanced detail for security analysis.
    /// </summary>
    public async Task LogAuthenticationEventAsync(AuthenticationEvent authEvent)
    {
        var auditEvent = new AuditEvent
        {
            EventType = authEvent.Success ? AuditEventType.AuthenticationSuccess : AuditEventType.AuthenticationFailure,
            Severity = authEvent.Success ? AuditSeverity.Info : AuditSeverity.Warning,
            UserId = authEvent.UserId,
            ClientIp = authEvent.ClientIp,
            UserAgent = authEvent.UserAgent,
            Resource = authEvent.AuthenticationMethod,
            Action = "AUTHENTICATE",
            AdditionalData = new Dictionary<string, object>
            {
                ["AuthenticationMethod"] = authEvent.AuthenticationMethod,
                ["Success"] = authEvent.Success,
                ["FailureReason"] = authEvent.FailureReason ?? string.Empty,
                ["SessionId"] = authEvent.SessionId ?? string.Empty,
                ["TokenIssuer"] = authEvent.TokenIssuer ?? string.Empty,
                ["UserRoles"] = authEvent.UserRoles,
                ["LocationInfo"] = authEvent.LocationInfo ?? string.Empty,
                ["DeviceFingerprint"] = authEvent.DeviceFingerprint ?? string.Empty
            }
        };

        await LogAuditEventAsync(auditEvent);

        // Check for suspicious authentication patterns
        if (!authEvent.Success)
        {
            await AnalyzeFailedAuthenticationPatternsAsync(authEvent);
        }
    }

    /// <summary>
    /// Logs security incidents for incident response and compliance.
    /// </summary>
    public async Task LogSecurityIncidentAsync(SecurityIncident incident)
    {
        var auditEvent = new AuditEvent
        {
            EventType = AuditEventType.SecurityIncident,
            Severity = MapIncidentSeverity(incident.Severity),
            UserId = incident.DetectedByUserId,
            ClientIp = incident.SourceIp,
            UserAgent = incident.UserAgent ?? string.Empty,
            Resource = incident.AffectedResource ?? string.Empty,
            Action = "SECURITY_INCIDENT",
            AdditionalData = new Dictionary<string, object>
            {
                ["IncidentId"] = incident.IncidentId,
                ["IncidentType"] = incident.IncidentType,
                ["Severity"] = incident.Severity,
                ["Description"] = incident.Description ?? string.Empty,
                ["AttackVector"] = incident.AttackVector ?? string.Empty,
                ["ImpactAssessment"] = incident.ImpactAssessment ?? string.Empty,
                ["ResponseActions"] = incident.ResponseActions,
                ["DetectionMethod"] = incident.DetectionMethod,
                ["ArtifactsCollected"] = incident.ArtifactsCollected,
                ["Status"] = incident.Status
            }
        };

        await LogAuditEventAsync(auditEvent);

        // Immediate alerting for critical security incidents
        if (incident.Severity == SecurityIncidentSeverity.Critical || incident.Severity == SecurityIncidentSeverity.High)
        {
            await TriggerSecurityAlertAsync(incident);
        }
    }

    /// <summary>
    /// Logs administrative operations for compliance and governance.
    /// </summary>
    public async Task LogAdministrativeActionAsync(AdministrativeAction adminAction)
    {
        var auditEvent = new AuditEvent
        {
            EventType = AuditEventType.AdministrativeAction,
            Severity = DetermineSeverityForAdminAction(adminAction),
            UserId = adminAction.AdminUserId,
            ClientIp = adminAction.ClientIp,
            UserAgent = adminAction.UserAgent,
            Resource = adminAction.TargetResource,
            Action = adminAction.ActionType,
            AdditionalData = new Dictionary<string, object>
            {
                ["ActionType"] = adminAction.ActionType ?? string.Empty,
                ["TargetUserId"] = adminAction.TargetUserId ?? string.Empty,
                ["TargetResource"] = adminAction.TargetResource,
                ["ActionParameters"] = adminAction.ActionParameters ?? new Dictionary<string, object>(),
                ["BusinessJustification"] = adminAction.BusinessJustification ?? string.Empty,
                ["ApprovalWorkflowId"] = adminAction.ApprovalWorkflowId ?? string.Empty,
                ["EffectivePermissions"] = adminAction.EffectivePermissions,
                ["DataClassification"] = adminAction.DataClassification ?? string.Empty
            }
        };

        await LogAuditEventAsync(auditEvent);

        // Special handling for privileged operations
        if (!string.IsNullOrEmpty(adminAction.ActionType) && IsPrivilegedOperation(adminAction.ActionType))
        {
            await LogPrivilegedOperationAsync(adminAction);
        }
    }

    /// <summary>
    /// Generates compliance reports for auditing and regulatory requirements.
    /// </summary>
    public async Task<ComplianceReport> GenerateComplianceReportAsync(ComplianceReportRequest request)
    {
        var events = await _storage.GetAuditEventsAsync(request.StartDate, request.EndDate, request.Filters);

        var report = new ComplianceReport
        {
            ReportId = Guid.NewGuid().ToString(),
            GeneratedDate = DateTime.UtcNow,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            ReportType = request.ReportType,
            GeneratedBy = request.RequestedBy
        };

        // Analyze events for compliance metrics
        report.Summary = AnalyzeComplianceMetrics(events, request);
        report.Violations = IdentifyComplianceViolations(events, request);
        report.Recommendations = GenerateComplianceRecommendations(report.Violations);
        report.Statistics = CalculateComplianceStatistics(events);

        // Add integrity verification
        if (_options.EnableIntegrityProtection)
        {
            report.IntegrityHash = GenerateReportIntegrityHash(report);
            report.DigitalSignature = await _cryptoService.SignDataAsync(report.ToString());
        }

        await _storage.StoreComplianceReportAsync(report);
        return report;
    }

    /// <summary>
    /// Validates audit log integrity for forensic purposes.
    /// </summary>
    public async Task<AuditIntegrityValidationResult> ValidateAuditIntegrityAsync(
        DateTime startDate, DateTime endDate)
    {
        var events = await _storage.GetAuditEventsAsync(startDate, endDate, new Dictionary<string, object>());
        var result = new AuditIntegrityValidationResult
        {
            ValidationDate = DateTime.UtcNow,
            StartDate = startDate,
            EndDate = endDate,
            TotalEventsValidated = events.Count()
        };

        var invalidEvents = new List<AuditEvent>();
        var tamperedEvents = new List<AuditEvent>();

        foreach (var auditEvent in events)
        {
            try
            {
                // Validate integrity hash
                var expectedHash = GenerateIntegrityHash(auditEvent);
                if (auditEvent.IntegrityHash != expectedHash)
                {
                    invalidEvents.Add(auditEvent);
                }

                // Validate digital signature
                if (!string.IsNullOrEmpty(auditEvent.DigitalSignature))
                {
                    var isValidSignature = await _cryptoService.VerifySignatureAsync(
                        auditEvent.ToString(), auditEvent.DigitalSignature);
                    if (!isValidSignature)
                    {
                        tamperedEvents.Add(auditEvent);
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorValidatingAuditEvent(_logger, auditEvent.EventId, ex);
                invalidEvents.Add(auditEvent);
            }
        }

        result.IntegrityViolations = invalidEvents.Count;
        result.TamperedEvents = tamperedEvents.Count;
        result.IsIntegrityMaintained = invalidEvents.Count == 0 && tamperedEvents.Count == 0;
        result.ValidationDetails = new Dictionary<string, object>
        {
            ["InvalidEventIds"] = invalidEvents.Select(e => e.EventId).ToList(),
            ["TamperedEventIds"] = tamperedEvents.Select(e => e.EventId).ToList(),
            ["ValidationAlgorithm"] = _options.IntegrityHashAlgorithm,
            ["SignatureVerificationEnabled"] = _options.EnableIntegrityProtection
        };

        if (!result.IsIntegrityMaintained)
        {
            await LogSecurityIncidentAsync(new SecurityIncident
            {
                IncidentId = Guid.NewGuid().ToString(),
                IncidentType = "AUDIT_INTEGRITY_VIOLATION",
                Severity = SecurityIncidentSeverity.Critical,
                Description = $"Audit log integrity violations detected: {invalidEvents.Count} invalid events, {tamperedEvents.Count} tampered events",
                DetectionMethod = "AUTOMATED_INTEGRITY_CHECK",
                Status = "OPEN"
            });
        }

        return result;
    }

    private void EnrichAuditEvent(AuditEvent auditEvent)
    {
        auditEvent.EventId = Guid.NewGuid().ToString();
        auditEvent.Timestamp = DateTime.UtcNow;
        auditEvent.CorrelationId = Activity.Current?.Id ?? Guid.NewGuid().ToString();
        auditEvent.HostName = Environment.MachineName;
        auditEvent.ApplicationVersion = _options.ApplicationVersion;

        // Add geolocation if available
        if (!string.IsNullOrEmpty(auditEvent.ClientIp))
        {
            auditEvent.AdditionalData ??= new Dictionary<string, object>();
            auditEvent.AdditionalData["EstimatedLocation"] = GetEstimatedLocation(auditEvent.ClientIp);
        }

        // Add risk score
        auditEvent.RiskScore = CalculateRiskScore(auditEvent);
    }

    private string GenerateIntegrityHash(AuditEvent auditEvent)
    {
        var hashInput = $"{auditEvent.EventId}{auditEvent.Timestamp:O}{auditEvent.UserId}{auditEvent.EventType}{auditEvent.Resource}{auditEvent.Action}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(hashInput));
        return Convert.ToBase64String(hashBytes);
    }

    private async Task StoreAuditEventAsync(AuditEvent auditEvent)
    {
        await _storage.StoreAuditEventAsync(auditEvent);

        // Store in secondary locations for redundancy
        // Note: SecondaryStorageServices feature is disabled pending DI container configuration
        if (_options.EnableRedundantStorage)
        {
            // TODO: Implement secondary storage through DI container
            // foreach (var secondaryStorage in _options.SecondaryStorageServices)
            // {
            //     try
            //     {
            //         await secondaryStorage.StoreAuditEventAsync(auditEvent);
            //     }
            //     catch (Exception ex)
            //     {
            //         FailedToStoreAuditEventInSecondaryStorage(_logger, ex);
            //     }
            // }
        }
    }

    private void LogAuditEventToLogger(AuditEvent auditEvent)
    {
        var logLevel = MapSeverityToLogLevel(auditEvent.Severity);
        var eventType = auditEvent.EventType.ToString();

        switch (logLevel)
        {
            case LogLevel.Information:
                AuditEventInformation(_logger, eventType, auditEvent.UserId, auditEvent.Resource, auditEvent.Action, auditEvent.ClientIp, auditEvent.RiskScore);
                break;
            case LogLevel.Warning:
                AuditEventWarning(_logger, eventType, auditEvent.UserId, auditEvent.Resource, auditEvent.Action, auditEvent.ClientIp, auditEvent.RiskScore);
                break;
            case LogLevel.Error:
                AuditEventError(_logger, eventType, auditEvent.UserId, auditEvent.Resource, auditEvent.Action, auditEvent.ClientIp, auditEvent.RiskScore);
                break;
            case LogLevel.Critical:
                AuditEventCritical(_logger, eventType, auditEvent.UserId, auditEvent.Resource, auditEvent.Action, auditEvent.ClientIp, auditEvent.RiskScore);
                break;
            default:
                AuditEventInformation(_logger, eventType, auditEvent.UserId, auditEvent.Resource, auditEvent.Action, auditEvent.ClientIp, auditEvent.RiskScore);
                break;
        }
    }

    private async Task CheckComplianceViolationsAsync(AuditEvent auditEvent)
    {
        // Check for immediate compliance violations
        var violations = new List<string>();

        // Check for after-hours access to sensitive resources
        if (IsAfterHours() && IsSensitiveResource(auditEvent.Resource))
        {
            violations.Add("AFTER_HOURS_SENSITIVE_ACCESS");
        }

        // Check for unusual access patterns
        if (auditEvent.RiskScore > _options.HighRiskThreshold)
        {
            violations.Add("HIGH_RISK_ACTIVITY");
        }

        // Check for privilege escalation
        if (auditEvent.EventType == AuditEventType.AdministrativeAction)
        {
            violations.Add("PRIVILEGE_ESCALATION_DETECTED");
        }

        if (violations.Count > 0)
        {
            await LogSecurityIncidentAsync(new SecurityIncident
            {
                IncidentId = Guid.NewGuid().ToString(),
                IncidentType = "COMPLIANCE_VIOLATION",
                Severity = SecurityIncidentSeverity.Medium,
                Description = $"Compliance violations detected: {string.Join(", ", violations)}",
                SourceIp = auditEvent.ClientIp,
                DetectedByUserId = "SYSTEM",
                DetectionMethod = "AUTOMATED_COMPLIANCE_CHECK",
                Status = "OPEN"
            });
        }
    }

    // Helper methods
    private AuditSeverity DetermineSeverityForConfigChange(ConfigurationChangeEvent changeEvent) =>
        IsCriticalSecurityConfig(changeEvent.ConfigurationKey) ? AuditSeverity.High : AuditSeverity.Info;

    private AuditSeverity DetermineSeverityForAdminAction(AdministrativeAction adminAction) =>
        IsPrivilegedOperation(adminAction.ActionType) ? AuditSeverity.High : AuditSeverity.Info;

    private bool IsCriticalSecurityConfig(string configKey) =>
        _options.CriticalSecurityConfigs.Any(pattern => configKey.Contains(pattern, StringComparison.OrdinalIgnoreCase));

    private bool IsPrivilegedOperation(string actionType) =>
        _options.PrivilegedOperations.Contains(actionType, StringComparer.OrdinalIgnoreCase);

    private bool IsSensitiveResource(string resource) =>
        _options.SensitiveResources.Any(pattern => resource.Contains(pattern, StringComparison.OrdinalIgnoreCase));

    private bool IsAfterHours() =>
        DateTime.Now.Hour < _options.BusinessHours.Start || DateTime.Now.Hour >= _options.BusinessHours.End;

    private string MaskSensitiveValue(string value) =>
        IsSensitiveConfigValue(value) ? "***MASKED***" : value;

    private bool IsSensitiveConfigValue(string value) =>
        _options.SensitiveValuePatterns.Any(pattern => value.Contains(pattern, StringComparison.OrdinalIgnoreCase));

    private AuditSeverity MapIncidentSeverity(SecurityIncidentSeverity severity) => severity switch
    {
        SecurityIncidentSeverity.Critical => AuditSeverity.Critical,
        SecurityIncidentSeverity.High => AuditSeverity.High,
        SecurityIncidentSeverity.Medium => AuditSeverity.Warning,
        SecurityIncidentSeverity.Low => AuditSeverity.Info,
        _ => AuditSeverity.Info
    };

    private LogLevel MapSeverityToLogLevel(AuditSeverity severity) => severity switch
    {
        AuditSeverity.Critical => LogLevel.Critical,
        AuditSeverity.High => LogLevel.Error,
        AuditSeverity.Warning => LogLevel.Warning,
        AuditSeverity.Info => LogLevel.Information,
        _ => LogLevel.Information
    };

    private int CalculateRiskScore(AuditEvent auditEvent)
    {
        var score = 0;

        // Base score by event type
        score += auditEvent.EventType switch
        {
            AuditEventType.SecurityIncident => 80,
            AuditEventType.AuthenticationFailure => 40,
            AuditEventType.AdministrativeAction => 60,
            AuditEventType.ConfigurationChange => 50,
            AuditEventType.DataAccess => 20,
            _ => 10
        };

        // Adjust for time of access
        if (IsAfterHours())
            score += 20;

        // Adjust for resource sensitivity
        if (IsSensitiveResource(auditEvent.Resource))
            score += 30;

        // Adjust for user role
        if (auditEvent.AdditionalData?.ContainsKey("UserRoles") == true)
        {
            var roles = auditEvent.AdditionalData["UserRoles"]?.ToString() ?? "";
            if (roles.Contains("Admin", StringComparison.OrdinalIgnoreCase))
                score += 25;
        }

        return Math.Min(score, 100); // Cap at 100
    }

    private string GetEstimatedLocation(string clientIp)
    {
        // In real implementation, use GeoIP service
        // For now, return placeholder
        return "Location lookup disabled";
    }

    private string GenerateReportIntegrityHash(ComplianceReport report)
    {
        var hashInput = JsonSerializer.Serialize(report, JsonOptions);
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(hashInput));
        return Convert.ToBase64String(hashBytes);
    }

    private ComplianceSummary AnalyzeComplianceMetrics(IEnumerable<AuditEvent> events, ComplianceReportRequest request)
    {
        // Implementation would analyze events for compliance metrics
        return new ComplianceSummary();
    }

    private List<ComplianceViolation> IdentifyComplianceViolations(IEnumerable<AuditEvent> events, ComplianceReportRequest request)
    {
        // Implementation would identify specific violations
        return new List<ComplianceViolation>();
    }

    private List<string> GenerateComplianceRecommendations(List<ComplianceViolation> violations)
    {
        // Implementation would generate actionable recommendations
        return new List<string>();
    }

    private Dictionary<string, object> CalculateComplianceStatistics(IEnumerable<AuditEvent> events)
    {
        // Implementation would calculate detailed statistics
        return new Dictionary<string, object>();
    }

    private async Task LogSensitiveDataAccessAsync(DataAccessEvent accessEvent)
    {
        // Special logging for sensitive data access
        await LogSecurityIncidentAsync(new SecurityIncident
        {
            IncidentId = Guid.NewGuid().ToString(),
            IncidentType = "SENSITIVE_DATA_ACCESS",
            Severity = SecurityIncidentSeverity.Medium,
            Description = $"Access to sensitive data type: {accessEvent.DataType}",
            SourceIp = accessEvent.ClientIp,
            DetectedByUserId = accessEvent.UserId,
            DetectionMethod = "AUTOMATED_MONITORING",
            Status = "LOGGED"
        });
    }

    private async Task AlertOnCriticalConfigChangeAsync(ConfigurationChangeEvent changeEvent)
    {
        // Implementation would send real-time alerts
        CriticalSecurityConfigurationChanged(_logger, changeEvent.ConfigurationKey, changeEvent.UserId ?? "Unknown");
    }

    private async Task AnalyzeFailedAuthenticationPatternsAsync(AuthenticationEvent authEvent)
    {
        // Implementation would analyze patterns for brute force, etc.
        FailedAuthenticationAnalyzed(_logger, authEvent.UserId ?? "Unknown", authEvent.ClientIp ?? "Unknown");
    }

    private async Task TriggerSecurityAlertAsync(SecurityIncident incident)
    {
        // Implementation would trigger immediate security alerts
        CriticalSecurityIncidentDetected(_logger, incident.IncidentType, incident.Description);
    }

    private async Task LogPrivilegedOperationAsync(AdministrativeAction adminAction)
    {
        // Additional logging for privileged operations
        PrivilegedOperationExecuted(_logger, adminAction.ActionType ?? "Unknown", adminAction.AdminUserId ?? "Unknown", adminAction.TargetResource);
    }

    [LoggerMessage(1001, LogLevel.Error, "Failed to log audit event: {EventType}")]
    private static partial void FailedToLogAuditEvent(ILogger logger, string eventType, Exception exception);

    [LoggerMessage(1002, LogLevel.Error, "Error validating audit event {EventId}")]
    private static partial void ErrorValidatingAuditEvent(ILogger logger, string eventId, Exception exception);

    [LoggerMessage(1003, LogLevel.Warning, "Failed to store audit event in secondary storage")]
    private static partial void FailedToStoreAuditEventInSecondaryStorage(ILogger logger, Exception exception);

    [LoggerMessage(1004, LogLevel.Warning, "Critical security configuration changed: {ConfigKey} by {UserId}")]
    private static partial void CriticalSecurityConfigurationChanged(ILogger logger, string configKey, string userId);

    [LoggerMessage(1005, LogLevel.Warning, "Failed authentication analyzed for {UserId} from {ClientIp}")]
    private static partial void FailedAuthenticationAnalyzed(ILogger logger, string userId, string clientIp);

    [LoggerMessage(1006, LogLevel.Error, "Critical security incident detected: {IncidentType} - {Description}")]
    private static partial void CriticalSecurityIncidentDetected(ILogger logger, string incidentType, string description);

    [LoggerMessage(1007, LogLevel.Warning, "Privileged operation executed: {ActionType} by {AdminUserId} on {TargetResource}")]
    private static partial void PrivilegedOperationExecuted(ILogger logger, string actionType, string adminUserId, string targetResource);

    [LoggerMessage(1008, LogLevel.Information, "AUDIT: {EventType} | User: {UserId} | Resource: {Resource} | Action: {Action} | IP: {ClientIp} | Risk: {RiskScore}")]
    private static partial void AuditEventInformation(ILogger logger, string eventType, string? userId, string resource, string action, string? clientIp, int riskScore);

    [LoggerMessage(1009, LogLevel.Warning, "AUDIT: {EventType} | User: {UserId} | Resource: {Resource} | Action: {Action} | IP: {ClientIp} | Risk: {RiskScore}")]
    private static partial void AuditEventWarning(ILogger logger, string eventType, string? userId, string resource, string action, string? clientIp, int riskScore);

    [LoggerMessage(1010, LogLevel.Error, "AUDIT: {EventType} | User: {UserId} | Resource: {Resource} | Action: {Action} | IP: {ClientIp} | Risk: {RiskScore}")]
    private static partial void AuditEventError(ILogger logger, string eventType, string? userId, string resource, string action, string? clientIp, int riskScore);

    [LoggerMessage(1011, LogLevel.Critical, "AUDIT: {EventType} | User: {UserId} | Resource: {Resource} | Action: {Action} | IP: {ClientIp} | Risk: {RiskScore}")]
    private static partial void AuditEventCritical(ILogger logger, string eventType, string? userId, string resource, string action, string? clientIp, int riskScore);
}
