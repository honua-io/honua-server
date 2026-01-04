// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using static Honua.Server.Features.Infrastructure.Security.OwaspComplianceFramework;

namespace Honua.Server.Features.Infrastructure.Security;

/// <summary>
/// Comprehensive OWASP Top 10 compliance framework for enterprise security.
/// Provides structured security controls aligned with OWASP guidelines.
/// </summary>
public static class OwaspComplianceFramework
{
    /// <summary>
    /// OWASP Top 10 vulnerability categories.
    /// </summary>
    public enum OwaspCategory
    {
        A01_BrokenAccessControl,
        A02_CryptographicFailures,
        A03_Injection,
        A04_InsecureDesign,
        A05_SecurityMisconfiguration,
        A06_VulnerableComponents,
        A07_IdentificationAndAuthentication,
        A08_SoftwareAndDataIntegrity,
        A09_SecurityLoggingAndMonitoring,
        A10_ServerSideRequestForgery
    }

    /// <summary>
    /// Security risk levels for compliance reporting.
    /// </summary>
    public enum RiskLevel
    {
        Critical,
        High,
        Medium,
        Low,
        Info
    }

    /// <summary>
    /// Validates request against OWASP security controls.
    /// </summary>
    public static OwaspValidationResult ValidateRequest(HttpContext context, OwaspValidationOptions options)
    {
        var result = new OwaspValidationResult();

        // A01: Broken Access Control
        ValidateAccessControl(context, result, options);

        // A02: Cryptographic Failures
        ValidateCryptographicRequirements(context, result, options);

        // A03: Injection
        ValidateInjectionPrevention(context, result, options);

        // A04: Insecure Design
        ValidateSecureDesign(context, result, options);

        // A05: Security Misconfiguration
        ValidateSecurityConfiguration(context, result, options);

        // A07: Identification and Authentication Failures
        ValidateAuthentication(context, result, options);

        // A08: Software and Data Integrity Failures
        ValidateDataIntegrity(context, result, options);

        // A10: Server-Side Request Forgery (SSRF)
        ValidateSsrfPrevention(context, result, options);

        return result;
    }

    private static void ValidateAccessControl(HttpContext context, OwaspValidationResult result, OwaspValidationOptions options)
    {
        // Check for proper authorization
        if (!context.User.Identity?.IsAuthenticated == true && options.RequireAuthentication)
        {
            result.AddViolation(OwaspCategory.A01_BrokenAccessControl, RiskLevel.High,
                "Unauthenticated access to protected resource", "RequireAuthentication");
        }

        // Validate resource-level permissions
        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<AuthorizeAttribute>() != null)
        {
            var requiredRole = endpoint.Metadata.GetMetadata<AuthorizeAttribute>()?.Roles;
            if (!string.IsNullOrEmpty(requiredRole) && !context.User.IsInRole(requiredRole))
            {
                result.AddViolation(OwaspCategory.A01_BrokenAccessControl, RiskLevel.High,
                    $"Insufficient privileges for role: {requiredRole}", "RoleBasedAccess");
            }
        }

        // Check for direct object reference attacks
        var pathParameters = context.Request.RouteValues;
        foreach (var param in pathParameters)
        {
            if (param.Key.Contains("id", StringComparison.OrdinalIgnoreCase) && int.TryParse(param.Value?.ToString(), out var id))
            {
                // Basic validation - in real implementation, check ownership
                if (id <= 0)
                {
                    result.AddViolation(OwaspCategory.A01_BrokenAccessControl, RiskLevel.Medium,
                        $"Invalid object reference: {param.Key}={id}", "DirectObjectReference");
                }
            }
        }
    }

    private static void ValidateCryptographicRequirements(HttpContext context, OwaspValidationResult result, OwaspValidationOptions options)
    {
        // Ensure HTTPS
        if (!context.Request.IsHttps && options.RequireHttps)
        {
            result.AddViolation(OwaspCategory.A02_CryptographicFailures, RiskLevel.High,
                "Request not using HTTPS encryption", "HttpsRequired");
        }

        // Validate security headers
        var headers = context.Request.Headers;
        if (options.RequireSecureHeaders)
        {
            if (!headers.ContainsKey("Strict-Transport-Security"))
            {
                result.AddViolation(OwaspCategory.A02_CryptographicFailures, RiskLevel.Medium,
                    "Missing HSTS header", "HstsRequired");
            }

            if (!headers.ContainsKey("X-Content-Type-Options"))
            {
                result.AddViolation(OwaspCategory.A02_CryptographicFailures, RiskLevel.Medium,
                    "Missing X-Content-Type-Options header", "ContentTypeOptionsRequired");
            }
        }

        // Check for sensitive data in query parameters
        foreach (var param in context.Request.Query)
        {
            if (options.SensitiveParameterNames.Any(name =>
                param.Key.Contains(name, StringComparison.OrdinalIgnoreCase)))
            {
                result.AddViolation(OwaspCategory.A02_CryptographicFailures, RiskLevel.High,
                    $"Sensitive data in query parameter: {param.Key}", "SensitiveDataExposure");
            }
        }
    }

    private static void ValidateInjectionPrevention(HttpContext context, OwaspValidationResult result, OwaspValidationOptions options)
    {
        // SQL Injection patterns
        var sqlInjectionPatterns = new[]
        {
            @"(\b(SELECT|INSERT|UPDATE|DELETE|DROP|CREATE|ALTER|EXEC|EXECUTE)\b)",
            @"(\bOR\b\s+\d+\s*=\s*\d+)",
            @"(\bAND\b\s+\d+\s*=\s*\d+)",
            @"(--|/\*|\*/)",
            @"(\bUNION\b.*\bSELECT\b)",
            @"(\b(CHAR|ASCII|SUBSTRING|LEN|UPPER|LOWER)\s*\()",
            @"(;.*(\bDROP\b|\bDELETE\b))"
        };

        // Check query parameters
        foreach (var param in context.Request.Query)
        {
            var value = param.Value.ToString();
            foreach (var pattern in sqlInjectionPatterns)
            {
                if (Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase))
                {
                    result.AddViolation(OwaspCategory.A03_Injection, RiskLevel.Critical,
                        $"Potential SQL injection in parameter: {param.Key}", "SqlInjectionAttempt");
                    SecurityAuditLogger.LogSqlInjectionAttempt(
                        context.RequestServices.GetRequiredService<ILogger<SecurityComplianceMiddleware>>(),
                        value,
                        SecurityAuditLogger.GetClientIpAddress(context),
                        SecurityAuditLogger.GetUserId(context),
                        context.Request.Path);
                    break;
                }
            }
        }

        // XSS patterns
        var xssPatterns = new[]
        {
            @"<script[^>]*>.*?</script>",
            @"javascript:",
            @"on\w+\s*=",
            @"<iframe[^>]*>",
            @"<object[^>]*>",
            @"<embed[^>]*>",
            @"<link[^>]*rel\s*=\s*[""']?\s*stylesheet"
        };

        foreach (var param in context.Request.Query)
        {
            var value = param.Value.ToString();
            foreach (var pattern in xssPatterns)
            {
                if (Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase))
                {
                    result.AddViolation(OwaspCategory.A03_Injection, RiskLevel.High,
                        $"Potential XSS in parameter: {param.Key}", "XssAttempt");
                    break;
                }
            }
        }

        // Command injection patterns
        var commandInjectionPatterns = new[]
        {
            @"[;&|`$()]",
            @"\b(rm|del|format|shutdown|reboot|halt)\b",
            @"\b(cat|type|more|less)\b\s+[/\\]",
            @"\b(wget|curl|ping|nslookup|dig)\b"
        };

        foreach (var param in context.Request.Query)
        {
            var value = param.Value.ToString();
            foreach (var pattern in commandInjectionPatterns)
            {
                if (Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase))
                {
                    result.AddViolation(OwaspCategory.A03_Injection, RiskLevel.High,
                        $"Potential command injection in parameter: {param.Key}", "CommandInjectionAttempt");
                    break;
                }
            }
        }
    }

    private static void ValidateSecureDesign(HttpContext context, OwaspValidationResult result, OwaspValidationOptions options)
    {
        // Check for business logic validation
        var method = context.Request.Method;
        var path = context.Request.Path.Value?.ToLower() ?? "";

        // Administrative endpoints should require additional validation
        if (path.Contains("/admin") && !context.User.IsInRole("Administrator"))
        {
            result.AddViolation(OwaspCategory.A04_InsecureDesign, RiskLevel.High,
                "Administrative endpoint accessed without proper role", "AdminAccessControl");
        }

        // Bulk operations should have rate limiting
        if (method == "POST" && (path.Contains("/bulk") || path.Contains("/batch")))
        {
            var rateLimitMetadata = context.GetEndpoint()?.Metadata
                .GetMetadata<EnableRateLimitingAttribute>();
            if (rateLimitMetadata == null)
            {
                result.AddViolation(OwaspCategory.A04_InsecureDesign, RiskLevel.Medium,
                    "Bulk operation endpoint missing rate limiting", "BulkOperationRateLimit");
            }
        }

        // File upload endpoints should have additional security
        if (context.Request.HasFormContentType && context.Request.Form.Files.Any())
        {
            foreach (var file in context.Request.Form.Files)
            {
                if (file.Length > options.MaxFileUploadSize)
                {
                    result.AddViolation(OwaspCategory.A04_InsecureDesign, RiskLevel.High,
                        $"File upload exceeds maximum size: {file.FileName}", "FileUploadSizeLimit");
                }

                var extension = Path.GetExtension(file.FileName).ToLower();
                if (!options.AllowedFileExtensions.Contains(extension))
                {
                    result.AddViolation(OwaspCategory.A04_InsecureDesign, RiskLevel.High,
                        $"Disallowed file extension: {extension}", "FileUploadExtensionValidation");
                }
            }
        }
    }

    private static void ValidateSecurityConfiguration(HttpContext context, OwaspValidationResult result, OwaspValidationOptions options)
    {
        // Check for security headers
        var response = context.Response;
        var headers = response.Headers;

        if (options.RequireContentSecurityPolicy && !headers.ContainsKey("Content-Security-Policy"))
        {
            result.AddViolation(OwaspCategory.A05_SecurityMisconfiguration, RiskLevel.Medium,
                "Missing Content-Security-Policy header", "CspHeaderRequired");
        }

        if (!headers.ContainsKey("X-Frame-Options"))
        {
            result.AddViolation(OwaspCategory.A05_SecurityMisconfiguration, RiskLevel.Medium,
                "Missing X-Frame-Options header", "FrameOptionsRequired");
        }

        if (!headers.ContainsKey("Referrer-Policy"))
        {
            result.AddViolation(OwaspCategory.A05_SecurityMisconfiguration, RiskLevel.Low,
                "Missing Referrer-Policy header", "ReferrerPolicyRecommended");
        }

        // Check for information disclosure
        if (headers.ContainsKey("Server") || headers.ContainsKey("X-Powered-By"))
        {
            result.AddViolation(OwaspCategory.A05_SecurityMisconfiguration, RiskLevel.Low,
                "Information disclosure in response headers", "InformationDisclosure");
        }
    }

    private static void ValidateAuthentication(HttpContext context, OwaspValidationResult result, OwaspValidationOptions options)
    {
        var user = context.User;

        // Check for strong authentication
        if (user.Identity?.IsAuthenticated == true)
        {
            // Check token expiry
            var expClaim = user.FindFirst("exp");
            if (expClaim != null && long.TryParse(expClaim.Value, out var exp))
            {
                var expiry = DateTimeOffset.FromUnixTimeSeconds(exp);
                if (expiry < DateTimeOffset.UtcNow)
                {
                    result.AddViolation(OwaspCategory.A07_IdentificationAndAuthentication, RiskLevel.High,
                        "Expired authentication token", "TokenExpired");
                }
            }

            // Check for session security
            var sessionAge = context.Session.GetInt32("SessionAge");
            if (sessionAge.HasValue && sessionAge > options.MaxSessionAgeMinutes)
            {
                result.AddViolation(OwaspCategory.A07_IdentificationAndAuthentication, RiskLevel.Medium,
                    "Session age exceeds maximum allowed", "SessionAgeExceeded");
            }
        }

        // Check for brute force patterns
        var clientIp = SecurityAuditLogger.GetClientIpAddress(context);
        if (!string.IsNullOrEmpty(clientIp))
        {
            // This would need to integrate with actual rate limiting service
            // For now, just demonstrate the concept
            var failedAttempts = GetRecentFailedAttempts(clientIp);
            if (failedAttempts > options.MaxFailedAuthAttempts)
            {
                result.AddViolation(OwaspCategory.A07_IdentificationAndAuthentication, RiskLevel.High,
                    $"Excessive failed authentication attempts from IP: {clientIp}", "BruteForceDetection");
            }
        }
    }

    private static void ValidateDataIntegrity(HttpContext context, OwaspValidationResult result, OwaspValidationOptions options)
    {
        // Check for integrity violations
        var contentType = context.Request.ContentType;

        if (context.Request.Method == "POST" || context.Request.Method == "PUT")
        {
            // Validate content type
            if (string.IsNullOrEmpty(contentType))
            {
                result.AddViolation(OwaspCategory.A08_SoftwareAndDataIntegrity, RiskLevel.Medium,
                    "Missing Content-Type header for data modification", "ContentTypeRequired");
            }

            // Check for content integrity headers
            if (!context.Request.Headers.ContainsKey("Content-Length"))
            {
                result.AddViolation(OwaspCategory.A08_SoftwareAndDataIntegrity, RiskLevel.Low,
                    "Missing Content-Length header", "ContentLengthRecommended");
            }

            // Validate JSON structure for API endpoints
            if (contentType?.Contains("application/json") == true)
            {
                try
                {
                    var body = context.Request.Body;
                    // In real implementation, would validate JSON structure
                    // This is a placeholder for the validation logic
                }
                catch (JsonException)
                {
                    result.AddViolation(OwaspCategory.A08_SoftwareAndDataIntegrity, RiskLevel.High,
                        "Malformed JSON payload", "JsonStructureValidation");
                }
            }
        }

        // Check for data tampering indicators
        var userAgent = context.Request.Headers.UserAgent.ToString();
        if (options.DetectAutomatedClients && IsAutomatedClient(userAgent))
        {
            result.AddViolation(OwaspCategory.A08_SoftwareAndDataIntegrity, RiskLevel.Medium,
                "Request from automated client detected", "AutomatedClientDetection");
        }
    }

    private static void ValidateSsrfPrevention(HttpContext context, OwaspValidationResult result, OwaspValidationOptions options)
    {
        // Check for SSRF patterns in parameters
        foreach (var param in context.Request.Query)
        {
            var value = param.Value.ToString();

            // Check for internal/private network addresses
            if (IsInternalUrl(value))
            {
                result.AddViolation(OwaspCategory.A10_ServerSideRequestForgery, RiskLevel.High,
                    $"Internal network URL detected in parameter: {param.Key}", "SsrfInternalUrl");
            }

            // Check for URL patterns
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                // Validate allowed domains
                if (options.AllowedExternalDomains.Count > 0 &&
                    !options.AllowedExternalDomains.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
                {
                    result.AddViolation(OwaspCategory.A10_ServerSideRequestForgery, RiskLevel.Medium,
                        $"External URL to unauthorized domain: {uri.Host}", "SsrfUnauthorizedDomain");
                }

                // Check for localhost/internal references
                if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                    uri.Host.Equals("127.0.0.1") ||
                    uri.Host.StartsWith("192.168.") ||
                    uri.Host.StartsWith("10.") ||
                    uri.Host.StartsWith("172."))
                {
                    result.AddViolation(OwaspCategory.A10_ServerSideRequestForgery, RiskLevel.Critical,
                        $"SSRF attempt to internal address: {uri.Host}", "SsrfInternalAddress");
                }
            }
        }
    }

    private static bool IsInternalUrl(string url)
    {
        var internalPatterns = new[]
        {
            @"^https?://localhost",
            @"^https?://127\.0\.0\.1",
            @"^https?://192\.168\.",
            @"^https?://10\.",
            @"^https?://172\.((1[6-9])|(2[0-9])|(3[0-1]))\.",
            @"^file://",
            @"^ftp://.*local"
        };

        return internalPatterns.Any(pattern => Regex.IsMatch(url, pattern, RegexOptions.IgnoreCase));
    }

    private static bool IsAutomatedClient(string userAgent)
    {
        var automatedPatterns = new[]
        {
            @"bot",
            @"crawler",
            @"spider",
            @"scraper",
            @"curl",
            @"wget",
            @"python",
            @"java",
            @"^$" // Empty user agent
        };

        return automatedPatterns.Any(pattern =>
            Regex.IsMatch(userAgent, pattern, RegexOptions.IgnoreCase));
    }

    private static int GetRecentFailedAttempts(string clientIp)
    {
        // In real implementation, this would query a cache or database
        // For demonstration, return 0
        return 0;
    }
}

/// <summary>
/// Validation options for OWASP compliance checking.
/// </summary>
public class OwaspValidationOptions
{
    public bool RequireAuthentication { get; set; } = true;
    public bool RequireHttps { get; set; } = true;
    public bool RequireSecureHeaders { get; set; } = true;
    public bool RequireContentSecurityPolicy { get; set; } = true;
    public bool DetectAutomatedClients { get; set; } = true;

    public long MaxFileUploadSize { get; set; } = 10 * 1024 * 1024; // 10MB
    public int MaxSessionAgeMinutes { get; set; } = 480; // 8 hours
    public int MaxFailedAuthAttempts { get; set; } = 5;

    public HashSet<string> AllowedFileExtensions { get; set; } = new()
    {
        ".jpg", ".jpeg", ".png", ".gif", ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".zip"
    };

    public HashSet<string> SensitiveParameterNames { get; set; } = new()
    {
        "password", "token", "key", "secret", "auth", "session", "cookie"
    };

    public HashSet<string> AllowedExternalDomains { get; set; } = new();
}

/// <summary>
/// Result of OWASP compliance validation.
/// </summary>
public class OwaspValidationResult
{
    public List<OwaspViolation> Violations { get; } = new();
    public bool HasCriticalViolations => Violations.Any(v => v.RiskLevel == RiskLevel.Critical);
    public bool HasHighRiskViolations => Violations.Any(v => v.RiskLevel == RiskLevel.High);
    public bool IsCompliant => !HasCriticalViolations && !HasHighRiskViolations;
    public DateTime ValidationTimestamp { get; } = DateTime.UtcNow;

    public void AddViolation(OwaspCategory category, RiskLevel riskLevel, string message, string ruleId)
    {
        Violations.Add(new OwaspViolation
        {
            Category = category,
            RiskLevel = riskLevel,
            Message = message,
            RuleId = ruleId,
            Timestamp = DateTime.UtcNow
        });
    }

    public Dictionary<OwaspCategory, int> GetViolationsByCategory()
    {
        return Violations
            .GroupBy(v => v.Category)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    public Dictionary<RiskLevel, int> GetViolationsByRiskLevel()
    {
        return Violations
            .GroupBy(v => v.RiskLevel)
            .ToDictionary(g => g.Key, g => g.Count());
    }
}

/// <summary>
/// Individual OWASP compliance violation.
/// </summary>
public class OwaspViolation
{
    public OwaspCategory Category { get; set; }
    public RiskLevel RiskLevel { get; set; }
    public string Message { get; set; } = string.Empty;
    public string RuleId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public Dictionary<string, object> AdditionalData { get; set; } = new();
}
