// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;

namespace Honua.Server.Features.Infrastructure.Security;

/// <summary>
/// High-performance security audit logging for authentication and authorization events.
/// Provides structured logging for security monitoring and compliance.
/// </summary>
public static class SecurityAuditLogger
{
    /// <summary>
    /// Logs successful authentication events.
    /// </summary>
    public static void LogAuthenticationSuccess(ILogger logger, string? userId, string? clientIp, string userAgent)
    {
        Log.AuthenticationSuccess(logger, userId ?? "unknown", clientIp ?? "unknown", userAgent);
    }

    /// <summary>
    /// Logs failed authentication attempts with relevant details.
    /// </summary>
    public static void LogAuthenticationFailure(ILogger logger, string reason, string? clientIp, string userAgent)
    {
        Log.AuthenticationFailure(logger, reason, clientIp ?? "unknown", userAgent);
    }

    /// <summary>
    /// Logs authorization failures when authenticated users try to access forbidden resources.
    /// </summary>
    public static void LogAuthorizationFailure(ILogger logger, string? userId, string resource, string action, string? clientIp)
    {
        Log.AuthorizationFailure(logger, userId ?? "unknown", resource, action, clientIp ?? "unknown");
    }

    /// <summary>
    /// Logs suspicious activity patterns that might indicate attacks.
    /// </summary>
    public static void LogSuspiciousActivity(ILogger logger, string activityType, string details, string? clientIp, string? userAgent)
    {
        Log.SuspiciousActivity(logger, activityType, details, clientIp ?? "unknown", userAgent ?? "unknown");
    }

    /// <summary>
    /// Logs file upload security violations.
    /// </summary>
    public static void LogFileUploadSecurityViolation(ILogger logger, string fileName, string violationType, string? clientIp, string? userId)
    {
        Log.FileUploadSecurityViolation(logger, fileName, violationType, clientIp ?? "unknown", userId ?? "unknown");
    }

    /// <summary>
    /// Logs SQL injection attempt detection.
    /// </summary>
    public static void LogSqlInjectionAttempt(ILogger logger, string attemptedQuery, string? clientIp, string? userId, string endpoint)
    {
        Log.SqlInjectionAttempt(logger, attemptedQuery, clientIp ?? "unknown", userId ?? "unknown", endpoint);
    }

    /// <summary>
    /// Logs rate limiting violations.
    /// </summary>
    public static void LogRateLimitViolation(ILogger logger, string? clientIp, int requestCount, int windowMinutes, string endpoint)
    {
        Log.RateLimitViolation(logger, clientIp ?? "unknown", requestCount, windowMinutes, endpoint);
    }

    /// <summary>
    /// Logs admin operations for audit trail.
    /// </summary>
    public static void LogAdminOperation(ILogger logger, string operation, string? userId, string? clientIp, Dictionary<string, object>? parameters = null)
    {
        var parameterString = parameters != null ?
            string.Join(", ", parameters.Select(kvp => $"{kvp.Key}={kvp.Value}")) :
            "none";

        Log.AdminOperation(logger, operation, userId ?? "unknown", clientIp ?? "unknown", parameterString);
    }

    /// <summary>
    /// Logs data access events for compliance and auditing.
    /// </summary>
    public static void LogDataAccess(ILogger logger, string dataType, string operation, int recordCount, string? userId, string? clientIp)
    {
        Log.DataAccess(logger, dataType, operation, recordCount, userId ?? "unknown", clientIp ?? "unknown");
    }

    /// <summary>
    /// Logs configuration changes that might affect security.
    /// </summary>
    public static void LogSecurityConfigurationChange(ILogger logger, string setting, string oldValue, string newValue, string? userId, string? clientIp)
    {
        Log.SecurityConfigurationChange(logger, setting, oldValue, newValue, userId ?? "unknown", clientIp ?? "unknown");
    }

    /// <summary>
    /// Gets the client IP address from HTTP context, handling proxies.
    /// </summary>
    public static string? GetClientIpAddress(HttpContext context)
    {
        // Check X-Forwarded-For header (when behind proxy)
        if (context.Request.Headers.TryGetValue("X-Forwarded-For", out var forwardedFor))
        {
            var firstIp = forwardedFor.ToString().Split(',')[0].Trim();
            if (IPAddress.TryParse(firstIp, out var ip))
                return ip.ToString();
        }

        // Check X-Real-IP header
        if (context.Request.Headers.TryGetValue("X-Real-IP", out var realIp))
        {
            if (IPAddress.TryParse(realIp.ToString(), out var ip))
                return ip.ToString();
        }

        // Fall back to connection remote IP
        return context.Connection.RemoteIpAddress?.ToString();
    }

    /// <summary>
    /// Gets the user agent string from HTTP context.
    /// </summary>
    public static string GetUserAgent(HttpContext context)
    {
        return context.Request.Headers.UserAgent.ToString() ?? "Unknown";
    }

    /// <summary>
    /// Gets the current authenticated user ID from HTTP context.
    /// </summary>
    public static string? GetUserId(HttpContext context)
    {
        return context.User.Identity?.IsAuthenticated == true
            ? context.User.FindFirst("sub")?.Value ?? context.User.Identity.Name
            : null;
    }
}

/// <summary>
/// High-performance logging definitions for security events using source generators.
/// </summary>
internal static partial class Log
{
    /// <summary>
    /// Logs successful user authentication events for security audit purposes.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="userId">The identifier of the user who successfully authenticated.</param>
    /// <param name="clientIp">The IP address of the client.</param>
    /// <param name="userAgent">The user agent string from the client request.</param>
    [LoggerMessage(
        EventId = 6001,
        Level = LogLevel.Information,
        Message = "Authentication successful for user {UserId} from {ClientIp} using {UserAgent}")]
    public static partial void AuthenticationSuccess(ILogger logger, string userId, string clientIp, string userAgent);

    /// <summary>
    /// Logs failed authentication attempts for security audit purposes.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="reason">The reason why authentication failed.</param>
    /// <param name="clientIp">The IP address of the client.</param>
    /// <param name="userAgent">The user agent string from the client request.</param>
    [LoggerMessage(
        EventId = 6002,
        Level = LogLevel.Warning,
        Message = "Authentication failed: {Reason} from {ClientIp} using {UserAgent}")]
    public static partial void AuthenticationFailure(ILogger logger, string reason, string clientIp, string userAgent);

    /// <summary>
    /// Logs authorization failures when authenticated users attempt unauthorized actions.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="userId">The identifier of the user who failed authorization.</param>
    /// <param name="resource">The resource the user attempted to access.</param>
    /// <param name="action">The action the user attempted to perform.</param>
    /// <param name="clientIp">The IP address of the client.</param>
    [LoggerMessage(
        EventId = 6003,
        Level = LogLevel.Warning,
        Message = "Authorization failed for user {UserId} attempting {Action} on {Resource} from {ClientIp}")]
    public static partial void AuthorizationFailure(ILogger logger, string userId, string resource, string action, string clientIp);

    /// <summary>
    /// Logs suspicious activity that may indicate security threats or attacks.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="activityType">The type of suspicious activity detected.</param>
    /// <param name="details">Additional details about the suspicious activity.</param>
    /// <param name="clientIp">The IP address of the client.</param>
    /// <param name="userAgent">The user agent string from the client request.</param>
    [LoggerMessage(
        EventId = 6004,
        Level = LogLevel.Warning,
        Message = "Suspicious activity detected: {ActivityType} - {Details} from {ClientIp} using {UserAgent}")]
    public static partial void SuspiciousActivity(ILogger logger, string activityType, string details, string clientIp, string userAgent);

    /// <summary>
    /// Logs file upload security violations such as malicious files or policy violations.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="fileName">The name of the file that violated security policies.</param>
    /// <param name="violationType">The type of security violation detected.</param>
    /// <param name="clientIp">The IP address of the client.</param>
    /// <param name="userId">The identifier of the user who uploaded the file.</param>
    [LoggerMessage(
        EventId = 6005,
        Level = LogLevel.Warning,
        Message = "File upload security violation: {FileName} - {ViolationType} from {ClientIp} by user {UserId}")]
    public static partial void FileUploadSecurityViolation(ILogger logger, string fileName, string violationType, string clientIp, string userId);

    /// <summary>
    /// Logs potential SQL injection attempts for immediate security response.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="attemptedQuery">The suspicious query that was attempted.</param>
    /// <param name="clientIp">The IP address of the client.</param>
    /// <param name="userId">The identifier of the user who made the attempt.</param>
    /// <param name="endpoint">The API endpoint where the attempt was made.</param>
    [LoggerMessage(
        EventId = 6006,
        Level = LogLevel.Error,
        Message = "SQL injection attempt detected: {AttemptedQuery} from {ClientIp} by user {UserId} on endpoint {Endpoint}")]
    public static partial void SqlInjectionAttempt(ILogger logger, string attemptedQuery, string clientIp, string userId, string endpoint);

    /// <summary>
    /// Logs rate limit violations for security monitoring and abuse prevention.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="clientIp">The IP address of the client that violated the rate limit.</param>
    /// <param name="requestCount">The number of requests made within the time window.</param>
    /// <param name="windowMinutes">The time window in minutes for the rate limit.</param>
    /// <param name="endpoint">The API endpoint where the violation occurred.</param>
    [LoggerMessage(
        EventId = 6007,
        Level = LogLevel.Warning,
        Message = "Rate limit violation: {ClientIp} made {RequestCount} requests in {WindowMinutes} minutes on {Endpoint}")]
    public static partial void RateLimitViolation(ILogger logger, string clientIp, int requestCount, int windowMinutes, string endpoint);

    /// <summary>
    /// Logs administrative operations for compliance and audit trail purposes.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="operation">The administrative operation that was performed.</param>
    /// <param name="userId">The identifier of the administrator who performed the operation.</param>
    /// <param name="clientIp">The IP address of the client.</param>
    /// <param name="parameters">The parameters or details of the operation.</param>
    [LoggerMessage(
        EventId = 6008,
        Level = LogLevel.Information,
        Message = "Admin operation: {Operation} performed by user {UserId} from {ClientIp} with parameters: {Parameters}")]
    public static partial void AdminOperation(ILogger logger, string operation, string userId, string clientIp, string parameters);

    /// <summary>
    /// Logs data access operations for data governance and compliance monitoring.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="dataType">The type of data that was accessed.</param>
    /// <param name="operation">The operation that was performed (read, write, delete, etc.).</param>
    /// <param name="recordCount">The number of records accessed during the operation.</param>
    /// <param name="userId">The identifier of the user who accessed the data.</param>
    /// <param name="clientIp">The IP address of the client.</param>
    [LoggerMessage(
        EventId = 6009,
        Level = LogLevel.Information,
        Message = "Data access: {DataType} {Operation} operation accessing {RecordCount} records by user {UserId} from {ClientIp}")]
    public static partial void DataAccess(ILogger logger, string dataType, string operation, int recordCount, string userId, string clientIp);

    /// <summary>
    /// Logs changes to security configuration settings for audit and compliance purposes.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="setting">The security setting that was changed.</param>
    /// <param name="oldValue">The previous value of the setting.</param>
    /// <param name="newValue">The new value of the setting.</param>
    /// <param name="userId">The identifier of the user who made the change.</param>
    /// <param name="clientIp">The IP address of the client.</param>
    [LoggerMessage(
        EventId = 6010,
        Level = LogLevel.Warning,
        Message = "Security configuration change: {Setting} changed from '{OldValue}' to '{NewValue}' by user {UserId} from {ClientIp}")]
    public static partial void SecurityConfigurationChange(ILogger logger, string setting, string oldValue, string newValue, string userId, string clientIp);
}

/// <summary>
/// Extension methods for adding security audit logging to HTTP context.
/// </summary>
public static class HttpContextSecurityExtensions
{
    /// <summary>
    /// Adds security audit logging extensions to HTTP context for easy access.
    /// </summary>
    public static void LogSecurityEvent(this HttpContext context, ILogger logger, string eventType, string details)
    {
        var clientIp = SecurityAuditLogger.GetClientIpAddress(context);
        var userAgent = SecurityAuditLogger.GetUserAgent(context);
        var userId = SecurityAuditLogger.GetUserId(context);

        SecurityAuditLogger.LogSuspiciousActivity(logger, eventType, details, clientIp, userAgent);
    }

    /// <summary>
    /// Logs authentication events from HTTP context.
    /// </summary>
    public static void LogAuthenticationEvent(this HttpContext context, ILogger logger, bool success, string? reason = null)
    {
        var clientIp = SecurityAuditLogger.GetClientIpAddress(context);
        var userAgent = SecurityAuditLogger.GetUserAgent(context);
        var userId = SecurityAuditLogger.GetUserId(context);

        if (success)
        {
            SecurityAuditLogger.LogAuthenticationSuccess(logger, userId, clientIp, userAgent);
        }
        else
        {
            SecurityAuditLogger.LogAuthenticationFailure(logger, reason ?? "Unknown", clientIp, userAgent);
        }
    }

    /// <summary>
    /// Logs data access events from HTTP context.
    /// </summary>
    public static void LogDataAccessEvent(this HttpContext context, ILogger logger, string dataType, string operation, int recordCount)
    {
        var clientIp = SecurityAuditLogger.GetClientIpAddress(context);
        var userId = SecurityAuditLogger.GetUserId(context);

        SecurityAuditLogger.LogDataAccess(logger, dataType, operation, recordCount, userId, clientIp);
    }
}
