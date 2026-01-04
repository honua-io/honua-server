// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Security;

/// <summary>
/// Comprehensive security compliance middleware that integrates all security frameworks.
/// Provides real-time security validation, audit logging, and threat detection.
/// </summary>
public class SecurityComplianceMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<SecurityComplianceMiddleware> _logger;
    private readonly SecurityComplianceOptions _options;
    private readonly ComprehensiveAuditLogger _auditLogger;
    private readonly SecurityMonitoringService _monitoringService;

    public SecurityComplianceMiddleware(
        RequestDelegate next,
        ILogger<SecurityComplianceMiddleware> logger,
        IOptions<SecurityComplianceOptions> options,
        ComprehensiveAuditLogger auditLogger,
        SecurityMonitoringService monitoringService)
    {
        _next = next;
        _logger = logger;
        _options = options.Value;
        _auditLogger = auditLogger;
        _monitoringService = monitoringService;
    }

    // LoggerMessage delegates for performance
    private static readonly Action<ILogger, string, Exception?> _logPreRequestSecurityError =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(1, "PreRequestSecurityError"),
            "Error in pre-request security checks for {CorrelationId}");

    private static readonly Action<ILogger, string, Exception?> _logPostRequestSecurityError =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(2, "PostRequestSecurityError"),
            "Error in post-request security analysis for {CorrelationId}");

    private static readonly Action<ILogger, string, string, string, string, Exception?> _logOwaspViolation =
        LoggerMessage.Define<string, string, string, string>(LogLevel.Warning, new EventId(3, "OwaspViolation"),
            "OWASP violation detected: {Category} - {Message} ({RuleId}) for {CorrelationId}");

    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var correlationId = Guid.NewGuid().ToString();
        context.Items["CorrelationId"] = correlationId;

        try
        {
            // Pre-request security validation
            await PerformPreRequestSecurityChecksAsync(context, correlationId);

            // Execute the request
            await _next(context);

            // Post-request security logging and analysis
            await PerformPostRequestSecurityAnalysisAsync(context, correlationId, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            await HandleSecurityExceptionAsync(context, ex, correlationId, stopwatch.ElapsedMilliseconds);
            throw;
        }
        finally
        {
            stopwatch.Stop();
        }
    }

    private async Task PerformPreRequestSecurityChecksAsync(HttpContext context, string correlationId)
    {
        try
        {
            // OWASP compliance validation
            if (_options.EnableOwaspValidation)
            {
                var owaspResult = OwaspComplianceFramework.ValidateRequest(context, _options.OwaspOptions);
                if (!owaspResult.IsCompliant)
                {
                    await HandleOwaspViolationsAsync(context, owaspResult, correlationId);

                    if (owaspResult.HasCriticalViolations)
                    {
                        throw new SecurityValidationException("Critical OWASP compliance violations detected");
                    }
                }
            }

            // Rate limiting check
            if (_options.EnableRateLimiting)
            {
                await CheckRateLimitsAsync(context);
            }

            // IP reputation check
            if (_options.EnableIpReputationCheck)
            {
                await CheckIpReputationAsync(context);
            }

            // Request size validation
            if (_options.EnableRequestSizeValidation)
            {
                ValidateRequestSize(context);
            }

            // Content type validation
            if (_options.EnableContentTypeValidation)
            {
                ValidateContentType(context);
            }
        }
        catch (SecurityValidationException)
        {
            throw; // Re-throw security exceptions
        }
        catch (Exception ex)
        {
            _logPreRequestSecurityError(_logger, correlationId, ex);
            // Don't throw non-security exceptions to avoid breaking the request pipeline
        }
    }

    private async Task PerformPostRequestSecurityAnalysisAsync(HttpContext context, string correlationId, long executionTimeMs)
    {
        try
        {
            // Create audit event
            var auditEvent = CreateAuditEventFromRequest(context, correlationId, executionTimeMs);

            // Log to comprehensive audit system
            await _auditLogger.LogAuditEventAsync(auditEvent);

            // Perform real-time security analysis
            if (_options.EnableRealTimeAnalysis)
            {
                var analysisResult = await _monitoringService.AnalyzeEventAsync(auditEvent);

                if (analysisResult.RiskScore > _options.HighRiskThreshold)
                {
                    await HandleHighRiskActivityAsync(context, analysisResult, correlationId);
                }
            }

            // Log data access events for compliance
            if (IsDataAccessRequest(context))
            {
                await LogDataAccessEventAsync(context, correlationId, executionTimeMs);
            }

            // Log administrative actions
            if (IsAdministrativeRequest(context))
            {
                await LogAdministrativeActionAsync(context, correlationId);
            }

            // Log authentication events
            if (IsAuthenticationRequest(context))
            {
                await LogAuthenticationEventAsync(context, correlationId);
            }
        }
        catch (Exception ex)
        {
            _logPostRequestSecurityError(_logger, correlationId, ex);
            // Don't throw to avoid impacting response
        }
    }

    private async Task HandleOwaspViolationsAsync(HttpContext context, OwaspValidationResult owaspResult, string correlationId)
    {
        foreach (var violation in owaspResult.Violations)
        {
            _logOwaspViolation(_logger, violation.Category.ToString(), violation.Message, violation.RuleId, correlationId, null);

            // Log security incident for critical violations
            if (violation.RiskLevel == OwaspComplianceFramework.RiskLevel.Critical ||
                violation.RiskLevel == OwaspComplianceFramework.RiskLevel.High)
            {
                await _auditLogger.LogSecurityIncidentAsync(new SecurityIncident
                {
                    IncidentId = Guid.NewGuid().ToString(),
                    IncidentType = "OWASP_COMPLIANCE_VIOLATION",
                    Severity = violation.RiskLevel == OwaspComplianceFramework.RiskLevel.Critical
                        ? SecurityIncidentSeverity.Critical
                        : SecurityIncidentSeverity.High,
                    Description = $"OWASP {violation.Category} violation: {violation.Message}",
                    SourceIp = SecurityAuditLogger.GetClientIpAddress(context),
                    DetectedByUserId = "SYSTEM_OWASP_VALIDATOR",
                    DetectionMethod = "AUTOMATED_OWASP_VALIDATION",
                    Status = "OPEN",
                    ArtifactsCollected = new Dictionary<string, object>
                    {
                        ["ViolationCategory"] = violation.Category.ToString(),
                        ["RuleId"] = violation.RuleId,
                        ["RequestPath"] = context.Request.Path.Value ?? string.Empty,
                        ["RequestMethod"] = context.Request.Method,
                        ["CorrelationId"] = correlationId
                    }
                });
            }
        }

        // Add compliance violation header for debugging (in development only)
        if (_options.IncludeViolationHeaders && context.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment())
        {
            context.Response.Headers["X-Security-Violations"] =
                string.Join(", ", owaspResult.Violations.Select(v => v.RuleId));
        }
    }

    private async Task CheckRateLimitsAsync(HttpContext context)
    {
        // Rate limiting logic would integrate with existing rate limiting service
        var clientIp = SecurityAuditLogger.GetClientIpAddress(context);
        var userId = SecurityAuditLogger.GetUserId(context);

        // Example logic - in real implementation, integrate with rate limiting service
        var rateLimitExceeded = false; // Placeholder

        if (rateLimitExceeded)
        {
            await _auditLogger.LogSecurityIncidentAsync(new SecurityIncident
            {
                IncidentId = Guid.NewGuid().ToString(),
                IncidentType = "RATE_LIMIT_VIOLATION",
                Severity = SecurityIncidentSeverity.Medium,
                Description = "Rate limit exceeded",
                SourceIp = clientIp,
                DetectedByUserId = "SYSTEM_RATE_LIMITER",
                DetectionMethod = "AUTOMATED_RATE_LIMITING",
                Status = "OPEN"
            });

            throw new SecurityValidationException("Rate limit exceeded");
        }
    }

    private async Task CheckIpReputationAsync(HttpContext context)
    {
        var clientIp = SecurityAuditLogger.GetClientIpAddress(context);
        if (string.IsNullOrEmpty(clientIp))
            return;

        // Check against known malicious IPs
        if (_options.MaliciousIpAddresses.Contains(clientIp))
        {
            await _auditLogger.LogSecurityIncidentAsync(new SecurityIncident
            {
                IncidentId = Guid.NewGuid().ToString(),
                IncidentType = "MALICIOUS_IP_ACCESS",
                Severity = SecurityIncidentSeverity.High,
                Description = $"Access from known malicious IP: {clientIp}",
                SourceIp = clientIp,
                DetectedByUserId = "SYSTEM_IP_REPUTATION",
                DetectionMethod = "IP_REPUTATION_CHECK",
                Status = "OPEN"
            });

            throw new SecurityValidationException($"Access denied from malicious IP: {clientIp}");
        }
    }

    private void ValidateRequestSize(HttpContext context)
    {
        if (context.Request.ContentLength > _options.MaxRequestSize)
        {
            throw new SecurityValidationException(
                $"Request size {context.Request.ContentLength} exceeds maximum allowed {_options.MaxRequestSize}");
        }
    }

    private void ValidateContentType(HttpContext context)
    {
        var contentType = context.Request.ContentType;
        if (!string.IsNullOrEmpty(contentType) &&
            _options.AllowedContentTypes.Count > 0 &&
            !_options.AllowedContentTypes.Any(allowed => contentType.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)))
        {
            throw new SecurityValidationException($"Content type not allowed: {contentType}");
        }
    }

    private AuditEvent CreateAuditEventFromRequest(HttpContext context, string correlationId, long executionTimeMs)
    {
        var auditEvent = new AuditEvent
        {
            EventId = Guid.NewGuid().ToString(),
            Timestamp = DateTime.UtcNow,
            EventType = DetermineEventType(context),
            Severity = AuditSeverity.Info,
            UserId = SecurityAuditLogger.GetUserId(context),
            ClientIp = SecurityAuditLogger.GetClientIpAddress(context),
            UserAgent = SecurityAuditLogger.GetUserAgent(context),
            Resource = context.Request.Path.Value ?? "",
            Action = context.Request.Method,
            CorrelationId = correlationId,
            AdditionalData = new Dictionary<string, object>
            {
                ["ResponseStatusCode"] = context.Response.StatusCode,
                ["ExecutionTimeMs"] = executionTimeMs,
                ["RequestSize"] = context.Request.ContentLength ?? 0,
                ["ResponseSize"] = GetResponseSize(context),
                ["Endpoint"] = context.GetEndpoint()?.DisplayName ?? string.Empty,
                ["RoutePattern"] = context.Request.RouteValues.GetValueOrDefault("pattern")?.ToString() ?? string.Empty,
                ["QueryParameters"] = GetSanitizedQueryParameters(context),
                ["RequestHeaders"] = GetSanitizedRequestHeaders(context)
            }
        };

        return auditEvent;
    }

    private AuditEventType DetermineEventType(HttpContext context)
    {
        var path = context.Request.Path.Value?.ToLower() ?? "";

        if (path.Contains("/auth") || path.Contains("/login") || path.Contains("/token"))
        {
            return context.Response.StatusCode >= 200 && context.Response.StatusCode < 300
                ? AuditEventType.AuthenticationSuccess
                : AuditEventType.AuthenticationFailure;
        }

        if (path.Contains("/admin") || path.Contains("/management"))
        {
            return AuditEventType.AdministrativeAction;
        }

        if (context.Request.Method == "GET" && path.Contains("/api/"))
        {
            return AuditEventType.DataAccess;
        }

        if (context.Request.Method is "POST" or "PUT" or "PATCH" or "DELETE")
        {
            return AuditEventType.DataModification;
        }

        if (context.Request.HasFormContentType && context.Request.Form.Files.Any())
        {
            return AuditEventType.FileUpload;
        }

        return AuditEventType.DataAccess;
    }

    private bool IsDataAccessRequest(HttpContext context)
    {
        var path = context.Request.Path.Value?.ToLower() ?? "";
        return path.Contains("/api/") && context.Request.Method == "GET";
    }

    private bool IsAdministrativeRequest(HttpContext context)
    {
        var path = context.Request.Path.Value?.ToLower() ?? "";
        return path.Contains("/admin") || path.Contains("/management") || path.Contains("/config");
    }

    private bool IsAuthenticationRequest(HttpContext context)
    {
        var path = context.Request.Path.Value?.ToLower() ?? "";
        return path.Contains("/auth") || path.Contains("/login") || path.Contains("/token");
    }

    private async Task LogDataAccessEventAsync(HttpContext context, string correlationId, long executionTimeMs)
    {
        var dataAccessEvent = new DataAccessEvent
        {
            UserId = SecurityAuditLogger.GetUserId(context),
            ClientIp = SecurityAuditLogger.GetClientIpAddress(context),
            UserAgent = SecurityAuditLogger.GetUserAgent(context),
            DataType = DetermineDataType(context),
            Resource = context.Request.Path.Value ?? "",
            Action = context.Request.Method,
            RecordCount = GetRecordCount(context),
            QueryParameters = GetQueryParametersDictionary(context),
            ResultSize = GetResponseSize(context),
            ExecutionTimeMs = executionTimeMs
        };

        await _auditLogger.LogDataAccessAsync(dataAccessEvent);
    }

    private async Task LogAdministrativeActionAsync(HttpContext context, string correlationId)
    {
        var adminAction = new AdministrativeAction
        {
            AdminUserId = SecurityAuditLogger.GetUserId(context),
            ClientIp = SecurityAuditLogger.GetClientIpAddress(context),
            UserAgent = SecurityAuditLogger.GetUserAgent(context),
            ActionType = DetermineAdminActionType(context),
            TargetResource = context.Request.Path.Value ?? "",
            ActionParameters = GetActionParameters(context)
        };

        await _auditLogger.LogAdministrativeActionAsync(adminAction);
    }

    private async Task LogAuthenticationEventAsync(HttpContext context, string correlationId)
    {
        var isSuccessful = context.Response.StatusCode >= 200 && context.Response.StatusCode < 300;

        var authEvent = new AuthenticationEvent
        {
            UserId = SecurityAuditLogger.GetUserId(context),
            ClientIp = SecurityAuditLogger.GetClientIpAddress(context),
            UserAgent = SecurityAuditLogger.GetUserAgent(context),
            AuthenticationMethod = DetermineAuthMethod(context),
            Success = isSuccessful,
            FailureReason = isSuccessful ? null : GetFailureReason(context),
            UserRoles = GetUserRoles(context)
        };

        await _auditLogger.LogAuthenticationEventAsync(authEvent);
    }

    private async Task HandleHighRiskActivityAsync(HttpContext context, SecurityAnalysisResult analysisResult, string correlationId)
    {
        Log.HighRiskActivityDetected(_logger, analysisResult.RiskScore, analysisResult.Threats.Count, analysisResult.Anomalies.Count, correlationId);

        // Log security incident
        await _auditLogger.LogSecurityIncidentAsync(new SecurityIncident
        {
            IncidentId = Guid.NewGuid().ToString(),
            IncidentType = "HIGH_RISK_ACTIVITY",
            Severity = analysisResult.RiskScore > 90 ? SecurityIncidentSeverity.Critical : SecurityIncidentSeverity.High,
            Description = $"High risk activity detected (Risk Score: {analysisResult.RiskScore})",
            SourceIp = SecurityAuditLogger.GetClientIpAddress(context),
            DetectedByUserId = "SYSTEM_RISK_ANALYZER",
            DetectionMethod = "AUTOMATED_RISK_ANALYSIS",
            Status = "OPEN",
            ArtifactsCollected = new Dictionary<string, object>
            {
                ["RiskScore"] = analysisResult.RiskScore,
                ["ThreatCount"] = analysisResult.Threats.Count,
                ["AnomalyCount"] = analysisResult.Anomalies.Count,
                ["Threats"] = analysisResult.Threats.Select(t => new { t.ThreatType, t.Severity, t.Description }),
                ["Anomalies"] = analysisResult.Anomalies.Select(a => new { a.AnomalyType, a.Severity, a.Description }),
                ["CorrelationId"] = correlationId
            }
        });

        // Add warning header for high risk activity
        if (_options.IncludeRiskHeaders)
        {
            context.Response.Headers["X-Security-Risk-Score"] = analysisResult.RiskScore.ToString();
        }
    }

    private async Task HandleSecurityExceptionAsync(HttpContext context, Exception ex, string correlationId, long executionTimeMs)
    {
        await _auditLogger.LogSecurityIncidentAsync(new SecurityIncident
        {
            IncidentId = Guid.NewGuid().ToString(),
            IncidentType = "REQUEST_PROCESSING_ERROR",
            Severity = ex is SecurityValidationException ? SecurityIncidentSeverity.High : SecurityIncidentSeverity.Medium,
            Description = $"Error processing request: {ex.Message}",
            SourceIp = SecurityAuditLogger.GetClientIpAddress(context),
            DetectedByUserId = "SYSTEM_ERROR_HANDLER",
            DetectionMethod = "EXCEPTION_HANDLING",
            Status = "OPEN",
            ArtifactsCollected = new Dictionary<string, object>
            {
                ["ExceptionType"] = ex.GetType().Name,
                ["ExceptionMessage"] = ex.Message,
                ["StackTrace"] = ex.StackTrace ?? string.Empty,
                ["RequestPath"] = context.Request.Path.Value ?? string.Empty,
                ["RequestMethod"] = context.Request.Method,
                ["ExecutionTimeMs"] = executionTimeMs,
                ["CorrelationId"] = correlationId
            }
        });
    }

    // Helper methods for data extraction
    private long GetResponseSize(HttpContext context)
    {
        // Estimate response size - in real implementation, use response body monitoring
        return context.Response.Headers.ContentLength ?? 0;
    }

    private Dictionary<string, object> GetSanitizedQueryParameters(HttpContext context)
    {
        var sanitized = new Dictionary<string, object>();
        foreach (var param in context.Request.Query)
        {
            // Mask sensitive parameters
            var isSensitive = _options.OwaspOptions.SensitiveParameterNames
                .Any(name => param.Key.Contains(name, StringComparison.OrdinalIgnoreCase));

            sanitized[param.Key] = isSensitive ? "***MASKED***" : param.Value.ToString();
        }
        return sanitized;
    }

    private Dictionary<string, string> GetSanitizedRequestHeaders(HttpContext context)
    {
        var sanitized = new Dictionary<string, string>();
        var sensitiveHeaders = new[] { "authorization", "cookie", "x-api-key", "x-auth-token" };

        foreach (var header in context.Request.Headers)
        {
            var isSensitive = sensitiveHeaders.Any(name =>
                header.Key.Contains(name, StringComparison.OrdinalIgnoreCase));

            sanitized[header.Key] = isSensitive ? "***MASKED***" : header.Value.ToString();
        }
        return sanitized;
    }

    private string DetermineDataType(HttpContext context)
    {
        var path = context.Request.Path.Value?.ToLower() ?? "";

        if (path.Contains("/users"))
            return "UserData";
        if (path.Contains("/features"))
            return "GeospatialData";
        if (path.Contains("/layers"))
            return "LayerConfiguration";
        if (path.Contains("/admin"))
            return "AdminData";

        return "GeneralData";
    }

    private int GetRecordCount(HttpContext context)
    {
        // In real implementation, extract from response or query parameters
        return 0; // Placeholder
    }

    private Dictionary<string, object> GetQueryParametersDictionary(HttpContext context)
    {
        return context.Request.Query.ToDictionary(
            kvp => kvp.Key,
            kvp => (object)kvp.Value.ToString());
    }

    private string DetermineAdminActionType(HttpContext context)
    {
        var method = context.Request.Method;
        var path = context.Request.Path.Value?.ToLower() ?? "";

        if (path.Contains("/users") && method == "POST")
            return "CREATE_USER";
        if (path.Contains("/users") && method == "DELETE")
            return "DELETE_USER";
        if (path.Contains("/config") && method == "PUT")
            return "UPDATE_CONFIGURATION";
        if (path.Contains("/permissions"))
            return "MODIFY_PERMISSIONS";

        return $"{method}_ADMIN_ACTION";
    }

    private Dictionary<string, object> GetActionParameters(HttpContext context)
    {
        var parameters = new Dictionary<string, object>();

        // Add route parameters
        foreach (var route in context.Request.RouteValues)
        {
            parameters[route.Key] = route.Value ?? "";
        }

        // Add query parameters (sanitized)
        foreach (var query in context.Request.Query.Take(10)) // Limit to prevent large logs
        {
            parameters[$"query_{query.Key}"] = query.Value.ToString();
        }

        return parameters;
    }

    private string DetermineAuthMethod(HttpContext context)
    {
        var authHeader = context.Request.Headers.Authorization.ToString();
        if (authHeader.StartsWith("Bearer"))
            return "JWT";
        if (authHeader.StartsWith("Basic"))
            return "Basic";
        if (context.Request.Headers.ContainsKey("X-API-Key"))
            return "ApiKey";
        return "Unknown";
    }

    private string? GetFailureReason(HttpContext context)
    {
        return context.Response.StatusCode switch
        {
            401 => "Invalid credentials",
            403 => "Access forbidden",
            429 => "Rate limit exceeded",
            _ => "Authentication failed"
        };
    }

    private List<string> GetUserRoles(HttpContext context)
    {
        return context.User?.Claims
            ?.Where(c => c.Type == "role" || c.Type == "roles")
            ?.Select(c => c.Value)
            ?.ToList() ?? new List<string>();
    }
}

/// <summary>
/// Configuration options for security compliance middleware.
/// </summary>
public class SecurityComplianceOptions
{
    public bool EnableOwaspValidation { get; set; } = true;
    public bool EnableRateLimiting { get; set; } = true;
    public bool EnableIpReputationCheck { get; set; } = true;
    public bool EnableRequestSizeValidation { get; set; } = true;
    public bool EnableContentTypeValidation { get; set; } = true;
    public bool EnableRealTimeAnalysis { get; set; } = true;
    public bool IncludeViolationHeaders { get; set; }
    public bool IncludeRiskHeaders { get; set; }

    public long MaxRequestSize { get; set; } = 50 * 1024 * 1024; // 50MB
    public int HighRiskThreshold { get; set; } = 75;

    public HashSet<string> MaliciousIpAddresses { get; set; } = new();
    public HashSet<string> AllowedContentTypes { get; set; } = new()
    {
        "application/json",
        "application/xml",
        "text/plain",
        "application/x-www-form-urlencoded",
        "multipart/form-data"
    };

    public OwaspValidationOptions OwaspOptions { get; set; } = new();
}

/// <summary>
/// Logging methods for security compliance middleware.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 3010, Level = LogLevel.Warning, Message = "High risk activity detected: Risk={RiskScore}, Threats={ThreatCount}, Anomalies={AnomalyCount} for {CorrelationId}")]
    public static partial void HighRiskActivityDetected(ILogger logger, double riskScore, int threatCount, int anomalyCount, string correlationId);
}

/// <summary>
/// Exception thrown when security validation fails.
/// </summary>
public class SecurityValidationException : Exception
{
    public SecurityValidationException(string message) : base(message) { }
    public SecurityValidationException(string message, Exception innerException) : base(message, innerException) { }
}
