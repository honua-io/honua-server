// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Server.Features.Infrastructure.Security;

/// <summary>
/// Core audit event model for comprehensive security logging.
/// </summary>
public class AuditEvent
{
    private static readonly JsonSerializerOptions _serializerOptions = new() { WriteIndented = false };

    public string EventId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public AuditEventType EventType { get; set; }
    public AuditSeverity Severity { get; set; }
    public string? UserId { get; set; }
    public string? ClientIp { get; set; }
    public string? UserAgent { get; set; }
    public string Resource { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string? CorrelationId { get; set; }
    public string? HostName { get; set; }
    public string? ApplicationVersion { get; set; }
    public Dictionary<string, object>? AdditionalData { get; set; }
    public string? IntegrityHash { get; set; }
    public string? DigitalSignature { get; set; }
    public int RiskScore { get; set; }

    public override string ToString()
    {
        return JsonSerializer.Serialize(this, _serializerOptions);
    }
}

/// <summary>
/// Types of audit events for security monitoring.
/// </summary>
public enum AuditEventType
{
    AuthenticationSuccess,
    AuthenticationFailure,
    AuthorizationFailure,
    DataAccess,
    DataModification,
    ConfigurationChange,
    AdministrativeAction,
    SecurityIncident,
    FileUpload,
    FileDownload,
    SessionCreated,
    SessionTerminated,
    PasswordChange,
    AccountLocked,
    AccountUnlocked,
    PrivilegeEscalation,
    SuspiciousActivity,
    ComplianceViolation,
    SystemStartup,
    SystemShutdown
}

/// <summary>
/// Severity levels for audit events.
/// </summary>
public enum AuditSeverity
{
    Critical,
    High,
    Warning,
    Info,
    Debug
}

/// <summary>
/// Data access event for detailed tracking.
/// </summary>
public class DataAccessEvent
{
    public string? UserId { get; set; }
    public string? ClientIp { get; set; }
    public string? UserAgent { get; set; }
    public string DataType { get; set; } = string.Empty;
    public string Resource { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public int RecordCount { get; set; }
    public Dictionary<string, object>? QueryParameters { get; set; }
    public long ResultSize { get; set; }
    public long ExecutionTimeMs { get; set; }
    public DateTime AccessTime { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Configuration change event for governance tracking.
/// </summary>
public class ConfigurationChangeEvent
{
    public string? UserId { get; set; }
    public string? ClientIp { get; set; }
    public string? UserAgent { get; set; }
    public string ConfigurationKey { get; set; } = string.Empty;
    public string OldValue { get; set; } = string.Empty;
    public string NewValue { get; set; } = string.Empty;
    public string? ChangeReason { get; set; }
    public string? ApprovalId { get; set; }
    public DateTime ChangeTime { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Authentication event for security analysis.
/// </summary>
public class AuthenticationEvent
{
    public string? UserId { get; set; }
    public string? ClientIp { get; set; }
    public string? UserAgent { get; set; }
    public string AuthenticationMethod { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? FailureReason { get; set; }
    public string? SessionId { get; set; }
    public string? TokenIssuer { get; set; }
    public List<string> UserRoles { get; set; } = new();
    public string? LocationInfo { get; set; }
    public string? DeviceFingerprint { get; set; }
    public DateTime AttemptTime { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Security incident for incident response tracking.
/// </summary>
public class SecurityIncident
{
    public string IncidentId { get; set; } = string.Empty;
    public string IncidentType { get; set; } = string.Empty;
    public SecurityIncidentSeverity Severity { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? SourceIp { get; set; }
    public string? UserAgent { get; set; }
    public string? DetectedByUserId { get; set; }
    public string? AffectedResource { get; set; }
    public string? AttackVector { get; set; }
    public string? ImpactAssessment { get; set; }
    public List<string> ResponseActions { get; set; } = new();
    public string DetectionMethod { get; set; } = string.Empty;
    public Dictionary<string, object> ArtifactsCollected { get; set; } = new();
    public string Status { get; set; } = string.Empty;
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }
}

/// <summary>
/// Security incident severity levels.
/// </summary>
public enum SecurityIncidentSeverity
{
    Critical,
    High,
    Medium,
    Low
}

/// <summary>
/// Administrative action for governance tracking.
/// </summary>
public class AdministrativeAction
{
    public string? AdminUserId { get; set; }
    public string? ClientIp { get; set; }
    public string? UserAgent { get; set; }
    public string ActionType { get; set; } = string.Empty;
    public string? TargetUserId { get; set; }
    public string TargetResource { get; set; } = string.Empty;
    public Dictionary<string, object> ActionParameters { get; set; } = new();
    public string? BusinessJustification { get; set; }
    public string? ApprovalWorkflowId { get; set; }
    public List<string> EffectivePermissions { get; set; } = new();
    public string? DataClassification { get; set; }
    public DateTime ActionTime { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Compliance report for regulatory requirements.
/// </summary>
public class ComplianceReport
{
    private static readonly JsonSerializerOptions _serializerOptions = new() { WriteIndented = false };

    public string ReportId { get; set; } = string.Empty;
    public DateTime GeneratedDate { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public string ReportType { get; set; } = string.Empty;
    public string? GeneratedBy { get; set; }
    public ComplianceSummary Summary { get; set; } = new();
    public List<ComplianceViolation> Violations { get; set; } = new();
    public List<string> Recommendations { get; set; } = new();
    public Dictionary<string, object> Statistics { get; set; } = new();
    public string? IntegrityHash { get; set; }
    public string? DigitalSignature { get; set; }

    public override string ToString()
    {
        return JsonSerializer.Serialize(this, _serializerOptions);
    }
}

/// <summary>
/// Summary of compliance metrics.
/// </summary>
public class ComplianceSummary
{
    public int TotalEvents { get; set; }
    public int SecurityIncidents { get; set; }
    public int AuthenticationFailures { get; set; }
    public int ConfigurationChanges { get; set; }
    public int DataAccessEvents { get; set; }
    public int ComplianceViolations { get; set; }
    public double ComplianceScore { get; set; }
    public Dictionary<string, int> EventsByCategory { get; set; } = new();
    public Dictionary<string, int> ViolationsByType { get; set; } = new();
}

/// <summary>
/// Compliance violation for tracking non-conformance.
/// </summary>
public class ComplianceViolation
{
    public string ViolationId { get; set; } = string.Empty;
    public string ViolationType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime OccurredAt { get; set; }
    public string Severity { get; set; } = string.Empty;
    public string? AffectedUser { get; set; }
    public string? AffectedResource { get; set; }
    public string? RegulationReference { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? RemediationAction { get; set; }
    public DateTime? RemediatedAt { get; set; }
}

/// <summary>
/// Compliance report request parameters.
/// </summary>
public class ComplianceReportRequest
{
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public string ReportType { get; set; } = string.Empty;
    public string? RequestedBy { get; set; }
    public Dictionary<string, object> Filters { get; set; } = new();
    public bool IncludePersonalData { get; set; }
    public string? ComplianceFramework { get; set; }
}

/// <summary>
/// Audit integrity validation result.
/// </summary>
public class AuditIntegrityValidationResult
{
    public DateTime ValidationDate { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public int TotalEventsValidated { get; set; }
    public int IntegrityViolations { get; set; }
    public int TamperedEvents { get; set; }
    public bool IsIntegrityMaintained { get; set; }
    public Dictionary<string, object> ValidationDetails { get; set; } = new();
}

/// <summary>
/// Audit logger configuration options.
/// </summary>
public class AuditLoggerOptions
{
    public bool EnableIntegrityProtection { get; set; } = true;
    public bool EnableRedundantStorage { get; set; } = true;
    public string IntegrityHashAlgorithm { get; set; } = "SHA256";
    public string ApplicationVersion { get; set; } = "1.0.0";
    public int HighRiskThreshold { get; set; } = 75;
    public BusinessHours BusinessHours { get; set; } = new();

    public HashSet<string> SensitiveDataTypes { get; set; } = new()
    {
        "UserPersonalData", "PaymentInformation", "HealthRecords", "FinancialData"
    };

    public HashSet<string> CriticalSecurityConfigs { get; set; } = new()
    {
        "Authentication", "Authorization", "Encryption", "Security", "Admin"
    };

    public HashSet<string> PrivilegedOperations { get; set; } = new()
    {
        "CREATE_USER", "DELETE_USER", "MODIFY_PERMISSIONS", "CHANGE_SECURITY_CONFIG", "SYSTEM_ADMIN"
    };

    public HashSet<string> SensitiveResources { get; set; } = new()
    {
        "/admin", "/config", "/security", "/users", "/audit"
    };

    public HashSet<string> SensitiveValuePatterns { get; set; } = new()
    {
        "password", "secret", "key", "token", "credential"
    };

    // Secondary storage services are registered through DI container
    // public List<IAuditLogStorage> SecondaryStorageServices { get; set; } = new();
}

/// <summary>
/// Business hours configuration for risk assessment.
/// </summary>
public class BusinessHours
{
    public int Start { get; set; } = 8;  // 8 AM
    public int End { get; set; } = 18;   // 6 PM
    public DayOfWeek[] BusinessDays { get; set; } =
    {
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
        DayOfWeek.Thursday, DayOfWeek.Friday
    };
}

/// <summary>
/// Interface for audit log storage implementations.
/// </summary>
public interface IAuditLogStorage
{
    Task StoreAuditEventAsync(AuditEvent auditEvent);
    Task<IEnumerable<AuditEvent>> GetAuditEventsAsync(DateTime startDate, DateTime endDate, Dictionary<string, object> filters);
    Task StoreComplianceReportAsync(ComplianceReport report);
    Task<ComplianceReport?> GetComplianceReportAsync(string reportId);
}

/// <summary>
/// Interface for cryptographic services used in audit logging.
/// </summary>
public interface ICryptographicService
{
    Task<string> SignDataAsync(string data);
    Task<bool> VerifySignatureAsync(string data, string signature);
    Task<string> EncryptDataAsync(string data);
    Task<string> DecryptDataAsync(string encryptedData);
    Task<string> GenerateSecureHashAsync(string data);
}
