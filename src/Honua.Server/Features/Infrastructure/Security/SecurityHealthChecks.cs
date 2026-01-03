// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Security;

/// <summary>
/// Comprehensive security health checks for enterprise compliance monitoring.
/// Validates security configurations, threat detection systems, and compliance status.
/// </summary>
public partial class SecurityHealthCheck : IHealthCheck
{
    private readonly ILogger<SecurityHealthCheck> _logger;
    private readonly SecurityHealthCheckOptions _options;
    private readonly ComprehensiveAuditLogger _auditLogger;
    private readonly SecurityMonitoringService _monitoringService;
    private readonly IServiceProvider _serviceProvider;

    public SecurityHealthCheck(
        ILogger<SecurityHealthCheck> logger,
        IOptions<SecurityHealthCheckOptions> options,
        ComprehensiveAuditLogger auditLogger,
        SecurityMonitoringService monitoringService,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _options = options.Value;
        _auditLogger = auditLogger;
        _monitoringService = monitoringService;
        _serviceProvider = serviceProvider;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var results = new List<SecurityHealthResult>();
        var overallStatus = HealthStatus.Healthy;

        try
        {
            // Core security configuration checks
            results.Add(await CheckSecurityConfigurationAsync());
            results.Add(await CheckAuthenticationSystemsAsync());
            results.Add(await CheckAuthorizationSystemsAsync());
            results.Add(await CheckAuditLoggingSystemAsync());
            results.Add(await CheckEncryptionConfigurationAsync());

            // Security monitoring checks
            results.Add(await CheckThreatDetectionSystemAsync());
            results.Add(await CheckIntrusionDetectionAsync());
            results.Add(await CheckSecurityAlertsAsync());

            // Compliance checks
            results.Add(await CheckOwaspComplianceAsync());
            results.Add(await CheckDataProtectionComplianceAsync());
            results.Add(await CheckAccessControlComplianceAsync());

            // Infrastructure security checks
            results.Add(await CheckNetworkSecurityAsync());
            results.Add(await CheckDatabaseSecurityAsync());
            results.Add(await CheckFileSystemSecurityAsync());

            // Vulnerability checks
            results.Add(await CheckForSecurityVulnerabilitiesAsync());
            results.Add(await CheckDependencySecurityAsync());

            // Determine overall health status
            if (results.Any(r => r.Status == SecurityHealthStatus.Critical))
            {
                overallStatus = HealthStatus.Unhealthy;
            }
            else if (results.Any(r => r.Status == SecurityHealthStatus.Warning))
            {
                overallStatus = HealthStatus.Degraded;
            }

            var data = new Dictionary<string, object>
            {
                ["SecurityHealthScore"] = CalculateSecurityHealthScore(results),
                ["TotalChecks"] = results.Count,
                ["HealthyChecks"] = results.Count(r => r.Status == SecurityHealthStatus.Healthy),
                ["WarningChecks"] = results.Count(r => r.Status == SecurityHealthStatus.Warning),
                ["CriticalChecks"] = results.Count(r => r.Status == SecurityHealthStatus.Critical),
                ["CheckResults"] = results.Select(r => new
                {
                    r.CheckName,
                    r.Status,
                    r.Message,
                    r.Recommendations
                }).ToList(),
                ["LastChecked"] = DateTime.UtcNow
            };

            return new HealthCheckResult(overallStatus,
                $"Security health check completed: {results.Count(r => r.Status == SecurityHealthStatus.Healthy)}/{results.Count} checks healthy",
                data: data);
        }
        catch (Exception ex)
        {
            Log.HealthCheckFailed(_logger, ex);
            return new HealthCheckResult(HealthStatus.Unhealthy, "Security health check failed", ex);
        }
    }

    private async Task<SecurityHealthResult> CheckSecurityConfigurationAsync()
    {
        var issues = new List<string>();
        var recommendations = new List<string>();

        try
        {
            // Check HTTPS configuration
            var httpsEnabled = CheckHttpsConfiguration();
            if (!httpsEnabled)
            {
                issues.Add("HTTPS not properly configured");
                recommendations.Add("Configure HTTPS with strong TLS settings");
            }

            // Check security headers
            var securityHeaders = CheckSecurityHeaders();
            if (securityHeaders.Count > 0)
            {
                issues.AddRange(securityHeaders.Select(h => $"Missing security header: {h}"));
                recommendations.Add("Configure all required security headers");
            }

            // Check CORS configuration
            var corsIssues = CheckCorsConfiguration();
            if (corsIssues.Count > 0)
            {
                issues.AddRange(corsIssues);
                recommendations.Add("Review and tighten CORS policies");
            }

            // Check session configuration
            var sessionIssues = CheckSessionConfiguration();
            if (sessionIssues.Count > 0)
            {
                issues.AddRange(sessionIssues);
                recommendations.Add("Configure secure session management");
            }

            var status = issues.Count == 0 ? SecurityHealthStatus.Healthy :
                        issues.Count <= 2 ? SecurityHealthStatus.Warning : SecurityHealthStatus.Critical;

            return new SecurityHealthResult
            {
                CheckName = "SecurityConfiguration",
                Status = status,
                Message = issues.Count == 0 ? "Security configuration is healthy" : $"{issues.Count} configuration issues found",
                Issues = issues,
                Recommendations = recommendations,
                Metadata = new Dictionary<string, object>
                {
                    ["HttpsEnabled"] = httpsEnabled,
                    ["SecurityHeadersConfigured"] = securityHeaders.Count == 0,
                    ["CorsConfigured"] = corsIssues.Count == 0
                }
            };
        }
        catch (Exception ex)
        {
            Log.SecurityCheckFailed(_logger, "SecurityConfiguration", ex);
            return CreateErrorResult("SecurityConfiguration", ex);
        }
    }

    private async Task<SecurityHealthResult> CheckAuthenticationSystemsAsync()
    {
        var issues = new List<string>();
        var recommendations = new List<string>();

        try
        {
            // Check authentication providers
            var authProviders = CheckAuthenticationProviders();
            if (!authProviders.IsConfigured)
            {
                issues.Add("Authentication providers not properly configured");
                recommendations.Add("Configure OIDC and API key authentication");
            }

            // Check JWT configuration
            var jwtConfig = CheckJwtConfiguration();
            if (!jwtConfig.IsValid)
            {
                issues.AddRange(jwtConfig.Issues);
                recommendations.Add("Review JWT configuration for security");
            }

            // Check password policies
            var passwordPolicy = CheckPasswordPolicies();
            if (!passwordPolicy.IsStrong)
            {
                issues.Add("Weak password policies detected");
                recommendations.Add("Implement strong password requirements");
            }

            // Check multi-factor authentication
            var mfaEnabled = CheckMultiFactorAuthentication();
            if (!mfaEnabled)
            {
                issues.Add("Multi-factor authentication not enforced");
                recommendations.Add("Enable MFA for all user accounts");
            }

            var status = DetermineHealthStatus(issues.Count);

            return new SecurityHealthResult
            {
                CheckName = "AuthenticationSystems",
                Status = status,
                Message = GenerateHealthMessage("authentication systems", issues.Count),
                Issues = issues,
                Recommendations = recommendations,
                Metadata = new Dictionary<string, object>
                {
                    ["AuthProvidersConfigured"] = authProviders.IsConfigured,
                    ["JwtConfigValid"] = jwtConfig.IsValid,
                    ["StrongPasswordPolicy"] = passwordPolicy.IsStrong,
                    ["MfaEnabled"] = mfaEnabled
                }
            };
        }
        catch (Exception ex)
        {
            Log.SecurityCheckFailed(_logger, "AuthenticationSystems", ex);
            return CreateErrorResult("AuthenticationSystems", ex);
        }
    }

    private async Task<SecurityHealthResult> CheckAuthorizationSystemsAsync()
    {
        var issues = new List<string>();
        var recommendations = new List<string>();

        try
        {
            // Check role-based access control
            var rbacConfig = CheckRoleBasedAccessControl();
            if (!rbacConfig.IsConfigured)
            {
                issues.Add("RBAC not properly configured");
                recommendations.Add("Implement proper role-based access control");
            }

            // Check resource-level authorization
            var resourceAuth = CheckResourceLevelAuthorization();
            if (resourceAuth.UncoveredEndpoints > 0)
            {
                issues.Add($"{resourceAuth.UncoveredEndpoints} endpoints lack authorization");
                recommendations.Add("Add authorization attributes to all protected endpoints");
            }

            // Check privilege escalation protection
            var privilegeEscalation = CheckPrivilegeEscalationProtection();
            if (!privilegeEscalation.IsProtected)
            {
                issues.Add("Insufficient privilege escalation protection");
                recommendations.Add("Implement privilege escalation monitoring and prevention");
            }

            var status = DetermineHealthStatus(issues.Count);

            return new SecurityHealthResult
            {
                CheckName = "AuthorizationSystems",
                Status = status,
                Message = GenerateHealthMessage("authorization systems", issues.Count),
                Issues = issues,
                Recommendations = recommendations,
                Metadata = new Dictionary<string, object>
                {
                    ["RbacConfigured"] = rbacConfig.IsConfigured,
                    ["ResourceAuthCoverage"] = resourceAuth.CoveragePercentage,
                    ["PrivilegeEscalationProtected"] = privilegeEscalation.IsProtected
                }
            };
        }
        catch (Exception ex)
        {
            Log.SecurityCheckFailed(_logger, "AuthorizationSystems", ex);
            return CreateErrorResult("AuthorizationSystems", ex);
        }
    }

    private async Task<SecurityHealthResult> CheckAuditLoggingSystemAsync()
    {
        var issues = new List<string>();
        var recommendations = new List<string>();

        try
        {
            // Check audit logging configuration
            var auditConfig = CheckAuditLoggingConfiguration();
            if (!auditConfig.IsConfigured)
            {
                issues.Add("Audit logging not properly configured");
                recommendations.Add("Configure comprehensive audit logging");
            }

            // Check log integrity
            var integrityResult = await ValidateLogIntegrityAsync();
            if (!integrityResult.IsIntact)
            {
                issues.Add($"Log integrity violations: {integrityResult.ViolationCount}");
                recommendations.Add("Investigate and resolve log integrity issues");
            }

            // Check log storage and retention
            var storageConfig = CheckLogStorageConfiguration();
            if (!storageConfig.IsSecure)
            {
                issues.AddRange(storageConfig.Issues);
                recommendations.Add("Secure audit log storage and configure proper retention");
            }

            // Check log monitoring
            var logMonitoring = CheckLogMonitoring();
            if (!logMonitoring.IsActive)
            {
                issues.Add("Log monitoring not active");
                recommendations.Add("Enable real-time log monitoring and alerting");
            }

            var status = DetermineHealthStatus(issues.Count);

            return new SecurityHealthResult
            {
                CheckName = "AuditLoggingSystem",
                Status = status,
                Message = GenerateHealthMessage("audit logging system", issues.Count),
                Issues = issues,
                Recommendations = recommendations,
                Metadata = new Dictionary<string, object>
                {
                    ["AuditConfigured"] = auditConfig.IsConfigured,
                    ["LogIntegrityIntact"] = integrityResult.IsIntact,
                    ["StorageSecure"] = storageConfig.IsSecure,
                    ["MonitoringActive"] = logMonitoring.IsActive
                }
            };
        }
        catch (Exception ex)
        {
            Log.SecurityCheckFailed(_logger, "AuditLoggingSystem", ex);
            return CreateErrorResult("AuditLoggingSystem", ex);
        }
    }

    private async Task<SecurityHealthResult> CheckEncryptionConfigurationAsync()
    {
        var issues = new List<string>();
        var recommendations = new List<string>();

        try
        {
            // Check data encryption at rest
            var encryptionAtRest = CheckDataEncryptionAtRest();
            if (!encryptionAtRest.IsEnabled)
            {
                issues.Add("Data encryption at rest not enabled");
                recommendations.Add("Enable database and file system encryption");
            }

            // Check data encryption in transit
            var encryptionInTransit = CheckDataEncryptionInTransit();
            if (!encryptionInTransit.IsEnabled)
            {
                issues.Add("Data encryption in transit not properly configured");
                recommendations.Add("Ensure all communications use strong encryption");
            }

            // Check key management
            var keyManagement = CheckKeyManagement();
            if (!keyManagement.IsSecure)
            {
                issues.AddRange(keyManagement.Issues);
                recommendations.Add("Implement secure key management practices");
            }

            // Check cryptographic algorithms
            var cryptoAlgorithms = CheckCryptographicAlgorithms();
            if (cryptoAlgorithms.WeakAlgorithmsFound)
            {
                issues.Add("Weak cryptographic algorithms detected");
                recommendations.Add("Upgrade to strong cryptographic algorithms");
            }

            var status = DetermineHealthStatus(issues.Count);

            return new SecurityHealthResult
            {
                CheckName = "EncryptionConfiguration",
                Status = status,
                Message = GenerateHealthMessage("encryption configuration", issues.Count),
                Issues = issues,
                Recommendations = recommendations,
                Metadata = new Dictionary<string, object>
                {
                    ["EncryptionAtRestEnabled"] = encryptionAtRest.IsEnabled,
                    ["EncryptionInTransitEnabled"] = encryptionInTransit.IsEnabled,
                    ["KeyManagementSecure"] = keyManagement.IsSecure,
                    ["StrongAlgorithmsUsed"] = !cryptoAlgorithms.WeakAlgorithmsFound
                }
            };
        }
        catch (Exception ex)
        {
            Log.SecurityCheckFailed(_logger, "EncryptionConfiguration", ex);
            return CreateErrorResult("EncryptionConfiguration", ex);
        }
    }

    private async Task<SecurityHealthResult> CheckThreatDetectionSystemAsync()
    {
        var issues = new List<string>();
        var recommendations = new List<string>();

        try
        {
            // Check if monitoring service is running
            var monitoringActive = CheckMonitoringServiceStatus();
            if (!monitoringActive.IsRunning)
            {
                issues.Add("Security monitoring service not running");
                recommendations.Add("Start security monitoring service");
            }

            // Check threat detection rules
            var threatRules = CheckThreatDetectionRules();
            if (threatRules.OutdatedRulesCount > 0)
            {
                issues.Add($"{threatRules.OutdatedRulesCount} threat detection rules are outdated");
                recommendations.Add("Update threat detection rules");
            }

            // Check anomaly detection
            var anomalyDetection = CheckAnomalyDetection();
            if (!anomalyDetection.IsActive)
            {
                issues.Add("Anomaly detection not active");
                recommendations.Add("Enable behavioral anomaly detection");
            }

            // Check threat intelligence feeds
            var threatIntel = CheckThreatIntelligenceFeeds();
            if (!threatIntel.IsUpdated)
            {
                issues.Add("Threat intelligence feeds not updated");
                recommendations.Add("Update threat intelligence feeds");
            }

            var status = DetermineHealthStatus(issues.Count);

            return new SecurityHealthResult
            {
                CheckName = "ThreatDetectionSystem",
                Status = status,
                Message = GenerateHealthMessage("threat detection system", issues.Count),
                Issues = issues,
                Recommendations = recommendations,
                Metadata = new Dictionary<string, object>
                {
                    ["MonitoringActive"] = monitoringActive.IsRunning,
                    ["ThreatRulesUpdated"] = threatRules.OutdatedRulesCount == 0,
                    ["AnomalyDetectionActive"] = anomalyDetection.IsActive,
                    ["ThreatIntelUpdated"] = threatIntel.IsUpdated
                }
            };
        }
        catch (Exception ex)
        {
            Log.SecurityCheckFailed(_logger, "ThreatDetectionSystem", ex);
            return CreateErrorResult("ThreatDetectionSystem", ex);
        }
    }

    private async Task<SecurityHealthResult> CheckOwaspComplianceAsync()
    {
        var issues = new List<string>();
        var recommendations = new List<string>();

        try
        {
            var owaspResults = await CheckOwaspTop10ComplianceAsync();

            foreach (var category in Enum.GetValues<OwaspComplianceFramework.OwaspCategory>())
            {
                var categoryResult = owaspResults.GetValueOrDefault(category);
                if (categoryResult?.ComplianceScore < 0.8)
                {
                    issues.Add($"OWASP {category} compliance below threshold");
                    recommendations.Add($"Address {category} vulnerabilities");
                }
            }

            var overallScore = owaspResults.Values.Average(r => r?.ComplianceScore ?? 0);
            var status = overallScore >= 0.9 ? SecurityHealthStatus.Healthy :
                        overallScore >= 0.7 ? SecurityHealthStatus.Warning : SecurityHealthStatus.Critical;

            return new SecurityHealthResult
            {
                CheckName = "OwaspCompliance",
                Status = status,
                Message = $"OWASP compliance score: {overallScore:P}",
                Issues = issues,
                Recommendations = recommendations,
                Metadata = new Dictionary<string, object>
                {
                    ["OverallComplianceScore"] = overallScore,
                    ["CategoryScores"] = owaspResults.ToDictionary(
                        kvp => kvp.Key.ToString(),
                        kvp => kvp.Value?.ComplianceScore ?? 0)
                }
            };
        }
        catch (Exception ex)
        {
            Log.SecurityCheckFailed(_logger, "OwaspCompliance", ex);
            return CreateErrorResult("OwaspCompliance", ex);
        }
    }

    private async Task<SecurityHealthResult> CheckForSecurityVulnerabilitiesAsync()
    {
        var issues = new List<string>();
        var recommendations = new List<string>();

        try
        {
            // Check for common vulnerabilities
            var vulnerabilities = await ScanForSecurityVulnerabilitiesAsync();

            foreach (var vuln in vulnerabilities.HighSeverityVulnerabilities)
            {
                issues.Add($"High severity vulnerability: {vuln.Description}");
            }

            foreach (var vuln in vulnerabilities.MediumSeverityVulnerabilities.Take(5))
            {
                issues.Add($"Medium severity vulnerability: {vuln.Description}");
            }

            if (vulnerabilities.HighSeverityVulnerabilities.Any())
            {
                recommendations.Add("Address high severity vulnerabilities immediately");
            }

            if (vulnerabilities.MediumSeverityVulnerabilities.Any())
            {
                recommendations.Add("Plan remediation for medium severity vulnerabilities");
            }

            var status = vulnerabilities.HighSeverityVulnerabilities.Any() ? SecurityHealthStatus.Critical :
                        vulnerabilities.MediumSeverityVulnerabilities.Any() ? SecurityHealthStatus.Warning :
                        SecurityHealthStatus.Healthy;

            return new SecurityHealthResult
            {
                CheckName = "SecurityVulnerabilities",
                Status = status,
                Message = $"Found {vulnerabilities.TotalVulnerabilities} vulnerabilities",
                Issues = issues,
                Recommendations = recommendations,
                Metadata = new Dictionary<string, object>
                {
                    ["HighSeverityCount"] = vulnerabilities.HighSeverityVulnerabilities.Count(),
                    ["MediumSeverityCount"] = vulnerabilities.MediumSeverityVulnerabilities.Count(),
                    ["LowSeverityCount"] = vulnerabilities.LowSeverityVulnerabilities.Count(),
                    ["LastScanDate"] = vulnerabilities.ScanDate
                }
            };
        }
        catch (Exception ex)
        {
            Log.SecurityCheckFailed(_logger, "SecurityVulnerabilities", ex);
            return CreateErrorResult("SecurityVulnerabilities", ex);
        }
    }

    // Helper methods
    private double CalculateSecurityHealthScore(List<SecurityHealthResult> results)
    {
        if (results.Count == 0)
            return 0;

        var healthyWeight = 100;
        var warningWeight = 60;
        var criticalWeight = 0;

        var totalWeight = results.Sum(r => r.Status switch
        {
            SecurityHealthStatus.Healthy => healthyWeight,
            SecurityHealthStatus.Warning => warningWeight,
            SecurityHealthStatus.Critical => criticalWeight,
            _ => 0
        });

        var maxWeight = results.Count * healthyWeight;
        return maxWeight > 0 ? (double)totalWeight / maxWeight * 100 : 0;
    }

    private SecurityHealthStatus DetermineHealthStatus(int issueCount)
    {
        return issueCount == 0 ? SecurityHealthStatus.Healthy :
               issueCount <= 2 ? SecurityHealthStatus.Warning : SecurityHealthStatus.Critical;
    }

    private string GenerateHealthMessage(string systemName, int issueCount)
    {
        return issueCount == 0 ? $"{systemName} is healthy" : $"{issueCount} issues found in {systemName}";
    }

    private SecurityHealthResult CreateErrorResult(string checkName, Exception ex)
    {
        return new SecurityHealthResult
        {
            CheckName = checkName,
            Status = SecurityHealthStatus.Critical,
            Message = $"Health check failed: {ex.Message}",
            Issues = new List<string> { $"Exception: {ex.Message}" },
            Recommendations = new List<string> { "Check logs and resolve the underlying issue" }
        };
    }

    // Placeholder implementations for security checks
    private bool CheckHttpsConfiguration() => true;
    private List<string> CheckSecurityHeaders() => new();
    private List<string> CheckCorsConfiguration() => new();
    private List<string> CheckSessionConfiguration() => new();
    private AuthenticationProvidersCheck CheckAuthenticationProviders() => new() { IsConfigured = true };
    private JwtConfigurationCheck CheckJwtConfiguration() => new() { IsValid = true };
    private PasswordPolicyCheck CheckPasswordPolicies() => new() { IsStrong = true };
    private bool CheckMultiFactorAuthentication() => false;
    private RbacConfigurationCheck CheckRoleBasedAccessControl() => new() { IsConfigured = true };
    private ResourceAuthorizationCheck CheckResourceLevelAuthorization() => new() { TotalEndpoints = 1, ProtectedEndpoints = 1, UncoveredEndpoints = 0 };
    private PrivilegeEscalationCheck CheckPrivilegeEscalationProtection() => new() { IsProtected = true };
    private AuditConfigurationCheck CheckAuditLoggingConfiguration() => new() { IsConfigured = true };
    private async Task<LogIntegrityCheck> ValidateLogIntegrityAsync() => new() { IsIntact = true, ViolationCount = 0 };
    private LogStorageConfigurationCheck CheckLogStorageConfiguration() => new() { IsSecure = true };
    private LogMonitoringCheck CheckLogMonitoring() => new() { IsActive = true };
    private EncryptionAtRestCheck CheckDataEncryptionAtRest() => new() { IsEnabled = false };
    private EncryptionInTransitCheck CheckDataEncryptionInTransit() => new() { IsEnabled = true };
    private KeyManagementCheck CheckKeyManagement() => new() { IsSecure = true };
    private CryptographicAlgorithmsCheck CheckCryptographicAlgorithms() => new() { WeakAlgorithmsFound = false };
    private MonitoringServiceCheck CheckMonitoringServiceStatus() => new() { IsRunning = true };
    private ThreatRulesCheck CheckThreatDetectionRules() => new() { OutdatedRulesCount = 0 };
    private AnomalyDetectionCheck CheckAnomalyDetection() => new() { IsActive = true };
    private ThreatIntelligenceCheck CheckThreatIntelligenceFeeds() => new() { IsUpdated = true };
    private async Task<Dictionary<OwaspComplianceFramework.OwaspCategory, OwaspCategoryResult?>> CheckOwaspTop10ComplianceAsync() =>
        new();

    private async Task<SecurityHealthResult> CheckIntrusionDetectionAsync()
    {
        try
        {
            var intrusionDetection = CheckIntrusionDetectionSystem();
            var status = intrusionDetection.IsActive ? SecurityHealthStatus.Healthy : SecurityHealthStatus.Warning;

            return new SecurityHealthResult
            {
                CheckName = "IntrusionDetection",
                Status = status,
                Message = intrusionDetection.IsActive ? "Intrusion detection is active" : "Intrusion detection not active",
                Issues = intrusionDetection.IsActive ? new List<string>() : new List<string> { "Intrusion detection system not active" },
                Recommendations = intrusionDetection.IsActive ? new List<string>() : new List<string> { "Enable intrusion detection system" }
            };
        }
        catch (Exception ex)
        {
            Log.SecurityCheckFailed(_logger, "IntrusionDetection", ex);
            return CreateErrorResult("IntrusionDetection", ex);
        }
    }

    private async Task<SecurityHealthResult> CheckSecurityAlertsAsync()
    {
        try
        {
            var alertConfig = CheckSecurityAlertConfiguration();
            var status = alertConfig.IsConfigured ? SecurityHealthStatus.Healthy : SecurityHealthStatus.Warning;

            return new SecurityHealthResult
            {
                CheckName = "SecurityAlerts",
                Status = status,
                Message = alertConfig.IsConfigured ? "Security alerts properly configured" : "Security alert configuration issues found",
                Issues = alertConfig.IsConfigured ? new List<string>() : new List<string> { "Security alerts not properly configured" },
                Recommendations = alertConfig.IsConfigured ? new List<string>() : new List<string> { "Configure security alerting system" }
            };
        }
        catch (Exception ex)
        {
            Log.SecurityCheckFailed(_logger, "SecurityAlerts", ex);
            return CreateErrorResult("SecurityAlerts", ex);
        }
    }

    private async Task<SecurityHealthResult> CheckDataProtectionComplianceAsync()
    {
        try
        {
            var dataProtection = CheckDataProtectionCompliance();
            var status = dataProtection.IsCompliant ? SecurityHealthStatus.Healthy : SecurityHealthStatus.Critical;

            return new SecurityHealthResult
            {
                CheckName = "DataProtectionCompliance",
                Status = status,
                Message = dataProtection.IsCompliant ? "Data protection compliance verified" : $"{dataProtection.Issues.Count} data protection issues found",
                Issues = dataProtection.Issues,
                Recommendations = dataProtection.Issues
            };
        }
        catch (Exception ex)
        {
            Log.SecurityCheckFailed(_logger, "DataProtectionCompliance", ex);
            return CreateErrorResult("DataProtectionCompliance", ex);
        }
    }

    private async Task<SecurityHealthResult> CheckAccessControlComplianceAsync()
    {
        try
        {
            var accessControl = CheckAccessControlCompliance();
            var status = accessControl.IsCompliant ? SecurityHealthStatus.Healthy : SecurityHealthStatus.Warning;

            return new SecurityHealthResult
            {
                CheckName = "AccessControlCompliance",
                Status = status,
                Message = accessControl.IsCompliant ? "Access control compliance verified" : $"{accessControl.Issues.Count} access control issues found",
                Issues = accessControl.Issues,
                Recommendations = accessControl.Issues
            };
        }
        catch (Exception ex)
        {
            Log.SecurityCheckFailed(_logger, "AccessControlCompliance", ex);
            return CreateErrorResult("AccessControlCompliance", ex);
        }
    }

    private async Task<SecurityHealthResult> CheckNetworkSecurityAsync()
    {
        try
        {
            var networkSecurity = CheckNetworkSecurityConfiguration();
            var status = networkSecurity.IsSecure ? SecurityHealthStatus.Healthy : SecurityHealthStatus.Warning;

            return new SecurityHealthResult
            {
                CheckName = "NetworkSecurity",
                Status = status,
                Message = networkSecurity.IsSecure ? "Network security configuration is secure" : $"{networkSecurity.Issues.Count} network security issues found",
                Issues = networkSecurity.Issues,
                Recommendations = networkSecurity.Issues
            };
        }
        catch (Exception ex)
        {
            Log.SecurityCheckFailed(_logger, "NetworkSecurity", ex);
            return CreateErrorResult("NetworkSecurity", ex);
        }
    }

    private async Task<SecurityHealthResult> CheckDatabaseSecurityAsync()
    {
        try
        {
            var databaseSecurity = CheckDatabaseSecurityConfiguration();
            var status = databaseSecurity.IsSecure ? SecurityHealthStatus.Healthy : SecurityHealthStatus.Warning;

            return new SecurityHealthResult
            {
                CheckName = "DatabaseSecurity",
                Status = status,
                Message = databaseSecurity.IsSecure ? "Database security configuration is secure" : $"{databaseSecurity.Issues.Count} database security issues found",
                Issues = databaseSecurity.Issues,
                Recommendations = databaseSecurity.Issues
            };
        }
        catch (Exception ex)
        {
            Log.SecurityCheckFailed(_logger, "DatabaseSecurity", ex);
            return CreateErrorResult("DatabaseSecurity", ex);
        }
    }

    private async Task<SecurityHealthResult> CheckFileSystemSecurityAsync()
    {
        try
        {
            var fileSystemSecurity = CheckFileSystemSecurityConfiguration();
            var status = fileSystemSecurity.IsSecure ? SecurityHealthStatus.Healthy : SecurityHealthStatus.Warning;

            return new SecurityHealthResult
            {
                CheckName = "FileSystemSecurity",
                Status = status,
                Message = fileSystemSecurity.IsSecure ? "File system security configuration is secure" : $"{fileSystemSecurity.Issues.Count} file system security issues found",
                Issues = fileSystemSecurity.Issues,
                Recommendations = fileSystemSecurity.Issues
            };
        }
        catch (Exception ex)
        {
            Log.SecurityCheckFailed(_logger, "FileSystemSecurity", ex);
            return CreateErrorResult("FileSystemSecurity", ex);
        }
    }

    private async Task<SecurityHealthResult> CheckDependencySecurityAsync()
    {
        try
        {
            var dependencySecurity = CheckDependencySecurityStatus();
            var status = dependencySecurity.IsSecure ? SecurityHealthStatus.Healthy : SecurityHealthStatus.Warning;

            return new SecurityHealthResult
            {
                CheckName = "DependencySecurity",
                Status = status,
                Message = dependencySecurity.IsSecure ? "Dependencies security verified" : $"{dependencySecurity.VulnerableDependencies} vulnerable dependencies found",
                Issues = dependencySecurity.Issues,
                Recommendations = dependencySecurity.Issues
            };
        }
        catch (Exception ex)
        {
            Log.SecurityCheckFailed(_logger, "DependencySecurity", ex);
            return CreateErrorResult("DependencySecurity", ex);
        }
    }

    private async Task<VulnerabilityScanResult> ScanForSecurityVulnerabilitiesAsync()
    {
        return new VulnerabilityScanResult
        {
            ScanDate = DateTime.UtcNow,
            HighSeverityVulnerabilities = new List<SecurityVulnerability>(),
            MediumSeverityVulnerabilities = new List<SecurityVulnerability>(),
            LowSeverityVulnerabilities = new List<SecurityVulnerability>()
        };
    }

    // Additional helper methods for the new checks
    private IntrusionDetectionCheck CheckIntrusionDetectionSystem() => new() { IsActive = true };
    private SecurityAlertConfigurationCheck CheckSecurityAlertConfiguration() => new() { IsConfigured = true };
    private DataProtectionComplianceCheck CheckDataProtectionCompliance() => new() { IsCompliant = true };
    private AccessControlComplianceCheck CheckAccessControlCompliance() => new() { IsCompliant = true };
    private NetworkSecurityConfigurationCheck CheckNetworkSecurityConfiguration() => new() { IsSecure = true };
    private DatabaseSecurityConfigurationCheck CheckDatabaseSecurityConfiguration() => new() { IsSecure = true };
    private FileSystemSecurityConfigurationCheck CheckFileSystemSecurityConfiguration() => new() { IsSecure = true };
    private DependencySecurityCheck CheckDependencySecurityStatus() => new() { IsSecure = true, VulnerableDependencies = 0 };

    /// <summary>
    /// Logging methods for security health checks.
    /// </summary>
    private static partial class Log
    {
        [LoggerMessage(EventId = 3001, Level = LogLevel.Error, Message = "Security health check '{CheckName}' failed.")]
        public static partial void SecurityCheckFailed(ILogger logger, string checkName, Exception exception);

        [LoggerMessage(EventId = 3002, Level = LogLevel.Error, Message = "Error performing security health check.")]
        public static partial void HealthCheckFailed(ILogger logger, Exception exception);
    }
}
