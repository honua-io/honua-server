// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Security;

/// <summary>
/// Configuration options for security health checks.
/// </summary>
public class SecurityHealthCheckOptions
{
    public bool EnableComplianceChecks { get; set; } = true;
    public bool EnableVulnerabilityScanning { get; set; } = true;
    public bool EnableIntegrityValidation { get; set; } = true;
    public TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromMinutes(5);
    public int CriticalIssueThreshold { get; set; } = 1;
    public int WarningIssueThreshold { get; set; } = 3;
    public Dictionary<string, object> ComplianceFrameworks { get; set; } = new();
}

/// <summary>
/// Result of a security health check.
/// </summary>
public class SecurityHealthResult
{
    public string CheckName { get; set; } = string.Empty;
    public SecurityHealthStatus Status { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<string> Issues { get; set; } = new();
    public List<string> Recommendations { get; set; } = new();
    public Dictionary<string, object> Metadata { get; set; } = new();
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Security health status levels.
/// </summary>
public enum SecurityHealthStatus
{
    Healthy,
    Warning,
    Critical
}

/// <summary>
/// Authentication providers configuration check result.
/// </summary>
public class AuthenticationProvidersCheck
{
    public bool IsConfigured { get; set; }
    public List<string> ConfiguredProviders { get; set; } = new();
    public List<string> Issues { get; set; } = new();
}

/// <summary>
/// JWT configuration check result.
/// </summary>
public class JwtConfigurationCheck
{
    public bool IsValid { get; set; }
    public List<string> Issues { get; set; } = new();
    public bool HasStrongSecrets { get; set; }
    public bool HasAppropriateExpiration { get; set; }
}

/// <summary>
/// Password policy check result.
/// </summary>
public class PasswordPolicyCheck
{
    public bool IsStrong { get; set; }
    public int MinimumLength { get; set; }
    public bool RequiresComplexity { get; set; }
    public bool RequiresNumbers { get; set; }
    public bool RequiresSpecialCharacters { get; set; }
    public List<string> Issues { get; set; } = new();
}

/// <summary>
/// RBAC configuration check result.
/// </summary>
public class RbacConfigurationCheck
{
    public bool IsConfigured { get; set; }
    public int RoleCount { get; set; }
    public int PermissionCount { get; set; }
    public List<string> Issues { get; set; } = new();
}

/// <summary>
/// Resource authorization check result.
/// </summary>
public class ResourceAuthorizationCheck
{
    public int TotalEndpoints { get; set; }
    public int ProtectedEndpoints { get; set; }
    public int UncoveredEndpoints { get; set; }
    public double CoveragePercentage => TotalEndpoints > 0 ? (double)ProtectedEndpoints / TotalEndpoints * 100 : 0;
    public List<string> UncoveredEndpointPaths { get; set; } = new();
}

/// <summary>
/// Privilege escalation protection check result.
/// </summary>
public class PrivilegeEscalationCheck
{
    public bool IsProtected { get; set; }
    public bool HasMonitoring { get; set; }
    public bool HasPrevention { get; set; }
    public List<string> Issues { get; set; } = new();
}

/// <summary>
/// Audit configuration check result.
/// </summary>
public class AuditConfigurationCheck
{
    public bool IsConfigured { get; set; }
    public bool HasIntegrityProtection { get; set; }
    public bool HasRetentionPolicy { get; set; }
    public List<string> Issues { get; set; } = new();
}

/// <summary>
/// Log integrity check result.
/// </summary>
public class LogIntegrityCheck
{
    public bool IsIntact { get; set; }
    public int ViolationCount { get; set; }
    public int TotalRecordsChecked { get; set; }
    public List<string> ViolationDetails { get; set; } = new();
    public DateTime LastChecked { get; set; }
}

/// <summary>
/// Log storage configuration check result.
/// </summary>
public class LogStorageConfigurationCheck
{
    public bool IsSecure { get; set; }
    public bool HasEncryption { get; set; }
    public bool HasAccessControl { get; set; }
    public bool HasRetentionPolicy { get; set; }
    public List<string> Issues { get; set; } = new();
}

/// <summary>
/// Log monitoring check result.
/// </summary>
public class LogMonitoringCheck
{
    public bool IsActive { get; set; }
    public bool HasRealTimeAlerts { get; set; }
    public bool HasAnomalyDetection { get; set; }
    public List<string> Issues { get; set; } = new();
}

/// <summary>
/// Encryption at rest check result.
/// </summary>
public class EncryptionAtRestCheck
{
    public bool IsEnabled { get; set; }
    public bool DatabaseEncrypted { get; set; }
    public bool FileSystemEncrypted { get; set; }
    public bool BackupsEncrypted { get; set; }
    public List<string> Issues { get; set; } = new();
}

/// <summary>
/// Encryption in transit check result.
/// </summary>
public class EncryptionInTransitCheck
{
    public bool IsEnabled { get; set; }
    public bool HttpsConfigured { get; set; }
    public bool DatabaseConnectionEncrypted { get; set; }
    public bool InternalCommunicationEncrypted { get; set; }
    public List<string> Issues { get; set; } = new();
}

/// <summary>
/// Key management check result.
/// </summary>
public class KeyManagementCheck
{
    public bool IsSecure { get; set; }
    public bool HasKeyRotation { get; set; }
    public bool HasSecureStorage { get; set; }
    public bool HasAccessControl { get; set; }
    public List<string> Issues { get; set; } = new();
}

/// <summary>
/// Cryptographic algorithms check result.
/// </summary>
public class CryptographicAlgorithmsCheck
{
    public bool WeakAlgorithmsFound { get; set; }
    public List<string> WeakAlgorithms { get; set; } = new();
    public List<string> StrongAlgorithms { get; set; } = new();
    public List<string> Issues { get; set; } = new();
}

/// <summary>
/// Monitoring service status check result.
/// </summary>
public class MonitoringServiceCheck
{
    public bool IsRunning { get; set; }
    public DateTime LastActivity { get; set; }
    public int ActiveMonitors { get; set; }
    public List<string> Issues { get; set; } = new();
}

/// <summary>
/// Threat detection rules check result.
/// </summary>
public class ThreatRulesCheck
{
    public int TotalRules { get; set; }
    public int OutdatedRulesCount { get; set; }
    public DateTime LastUpdated { get; set; }
    public List<string> OutdatedRules { get; set; } = new();
}

/// <summary>
/// Anomaly detection check result.
/// </summary>
public class AnomalyDetectionCheck
{
    public bool IsActive { get; set; }
    public bool HasBehavioralAnalysis { get; set; }
    public bool HasMachineLearning { get; set; }
    public List<string> Issues { get; set; } = new();
}

/// <summary>
/// Threat intelligence check result.
/// </summary>
public class ThreatIntelligenceCheck
{
    public bool IsUpdated { get; set; }
    public DateTime LastUpdate { get; set; }
    public int FeedCount { get; set; }
    public List<string> Issues { get; set; } = new();
}

/// <summary>
/// OWASP category compliance result.
/// </summary>
public class OwaspCategoryResult
{
    public OwaspComplianceFramework.OwaspCategory Category { get; set; }
    public double ComplianceScore { get; set; }
    public List<string> VulnerabilitiesFound { get; set; } = new();
    public List<string> Recommendations { get; set; } = new();
    public DateTime LastAssessed { get; set; }
}

/// <summary>
/// Vulnerability scan result.
/// </summary>
public class VulnerabilityScanResult
{
    public DateTime ScanDate { get; set; }
    public int TotalVulnerabilities => HighSeverityVulnerabilities.Count() +
                                       MediumSeverityVulnerabilities.Count() +
                                       LowSeverityVulnerabilities.Count();

    public IEnumerable<SecurityVulnerability> HighSeverityVulnerabilities { get; set; } = new List<SecurityVulnerability>();
    public IEnumerable<SecurityVulnerability> MediumSeverityVulnerabilities { get; set; } = new List<SecurityVulnerability>();
    public IEnumerable<SecurityVulnerability> LowSeverityVulnerabilities { get; set; } = new List<SecurityVulnerability>();
    public Dictionary<string, object> ScanMetadata { get; set; } = new();
}

/// <summary>
/// Individual security vulnerability.
/// </summary>
public class SecurityVulnerability
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public VulnerabilitySeverity Severity { get; set; }
    public string Component { get; set; } = string.Empty;
    public string? Version { get; set; }
    public string? CveId { get; set; }
    public double CvssScore { get; set; }
    public List<string> References { get; set; } = new();
    public string? Remediation { get; set; }
    public DateTime DiscoveredAt { get; set; }
}

/// <summary>
/// Vulnerability severity levels.
/// </summary>
public enum VulnerabilitySeverity
{
    Critical,
    High,
    Medium,
    Low,
    Info
}

/// <summary>
/// Network security check result.
/// </summary>
public class NetworkSecurityCheck
{
    public bool IsSecure { get; set; }
    public bool HasFirewallRules { get; set; }
    public bool HasIntrusionDetection { get; set; }
    public bool HasDdosProtection { get; set; }
    public List<string> Issues { get; set; } = new();
}

/// <summary>
/// Database security check result.
/// </summary>
public class DatabaseSecurityCheck
{
    public bool IsSecure { get; set; }
    public bool HasConnectionEncryption { get; set; }
    public bool HasAccessControl { get; set; }
    public bool HasAuditLogging { get; set; }
    public bool HasBackupEncryption { get; set; }
    public List<string> Issues { get; set; } = new();
}

/// <summary>
/// File system security check result.
/// </summary>
public class FileSystemSecurityCheck
{
    public bool IsSecure { get; set; }
    public bool HasProperPermissions { get; set; }
    public bool HasEncryption { get; set; }
    public bool HasAccessLogging { get; set; }
    public List<string> Issues { get; set; } = new();
}

/// <summary>
/// Intrusion detection check result.
/// </summary>
public class IntrusionDetectionCheck
{
    public bool IsActive { get; set; }
    public bool HasRealTimeMonitoring { get; set; }
    public bool HasAutomatedResponse { get; set; }
    public DateTime LastAlert { get; set; }
    public int AlertCount24h { get; set; }
    public List<string> Issues { get; set; } = new();
}

/// <summary>
/// Security alerts check result.
/// </summary>
public class SecurityAlertsCheck
{
    public int PendingAlerts { get; set; }
    public int CriticalAlerts { get; set; }
    public int HighPriorityAlerts { get; set; }
    public bool HasEscalationProcess { get; set; }
    public bool HasResponseTeam { get; set; }
    public List<string> Issues { get; set; } = new();
}

/// <summary>
/// Data protection compliance check result.
/// </summary>
public class DataProtectionComplianceCheck
{
    public bool IsCompliant { get; set; }
    public bool HasDataClassification { get; set; }
    public bool HasPrivacyControls { get; set; }
    public bool HasDataRetentionPolicy { get; set; }
    public bool HasDataDeletionCapability { get; set; }
    public List<string> Issues { get; set; } = new();
}

/// <summary>
/// Access control compliance check result.
/// </summary>
public class AccessControlComplianceCheck
{
    public bool IsCompliant { get; set; }
    public bool HasLeastPrivilege { get; set; }
    public bool HasSegregationOfDuties { get; set; }
    public bool HasRegularAccessReview { get; set; }
    public List<string> Issues { get; set; } = new();
}

/// <summary>
/// Dependency security check result.
/// </summary>
public class DependencySecurityCheck
{
    public bool IsSecure { get; set; }
    public int VulnerableDependencies { get; set; }
    public int OutdatedDependencies { get; set; }
    public DateTime LastScan { get; set; }
    public List<VulnerableDependency> VulnerablePackages { get; set; } = new();
    public List<string> Issues { get; set; } = new();
}

/// <summary>
/// Vulnerable dependency information.
/// </summary>
public class VulnerableDependency
{
    public string PackageName { get; set; } = string.Empty;
    public string CurrentVersion { get; set; } = string.Empty;
    public string VulnerableVersions { get; set; } = string.Empty;
    public string? FixedVersion { get; set; }
    public VulnerabilitySeverity Severity { get; set; }
    public List<string> VulnerabilityIds { get; set; } = new();
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Security alert configuration check result.
/// </summary>
public class SecurityAlertConfigurationCheck
{
    public bool IsConfigured { get; set; }
}

/// <summary>
/// Network security configuration check result.
/// </summary>
public class NetworkSecurityConfigurationCheck
{
    public bool IsSecure { get; set; }
    public int IssueCount { get; set; }
    public List<string> Issues { get; set; } = new();
    public List<string> Recommendations { get; set; } = new();
}

/// <summary>
/// Database security configuration check result.
/// </summary>
public class DatabaseSecurityConfigurationCheck
{
    public bool IsSecure { get; set; }
    public int IssueCount { get; set; }
    public List<string> Issues { get; set; } = new();
    public List<string> Recommendations { get; set; } = new();
}

/// <summary>
/// File system security configuration check result.
/// </summary>
public class FileSystemSecurityConfigurationCheck
{
    public bool IsSecure { get; set; }
    public int IssueCount { get; set; }
    public List<string> Issues { get; set; } = new();
    public List<string> Recommendations { get; set; } = new();
}
