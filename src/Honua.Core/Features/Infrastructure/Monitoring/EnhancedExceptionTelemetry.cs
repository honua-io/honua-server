// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Infrastructure.Monitoring;

/// <summary>
/// Enhanced exception telemetry system with standardized correlation ID propagation
/// and exception classification for improved observability.
/// </summary>
public interface IEnhancedExceptionTelemetry
{
    /// <summary>
    /// Records an exception with enhanced telemetry data.
    /// </summary>
    /// <param name="exception">The exception to record</param>
    /// <param name="context">Additional context about the exception</param>
    void RecordException(Exception exception, ExceptionContext? context = null);

    /// <summary>
    /// Records an exception with correlation ID and operation context.
    /// </summary>
    /// <param name="exception">The exception to record</param>
    /// <param name="correlationId">Correlation ID for distributed tracing</param>
    /// <param name="operationType">Type of operation that failed</param>
    /// <param name="additionalProperties">Additional properties for telemetry</param>
    void RecordException(Exception exception, string correlationId, string operationType, IDictionary<string, object>? additionalProperties = null);

    /// <summary>
    /// Gets exception statistics for monitoring and alerting.
    /// </summary>
    ExceptionStatistics GetStatistics();

    /// <summary>
    /// Gets recent exceptions for analysis.
    /// </summary>
    IReadOnlyList<ExceptionRecord> GetRecentExceptions(int maxCount = 100, ExceptionSeverity? minSeverity = null);
}

/// <summary>
/// Context information for exception recording.
/// </summary>
public sealed class ExceptionContext
{
    /// <summary>
    /// Correlation ID for distributed tracing.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Type of operation that failed.
    /// </summary>
    public string? OperationType { get; set; }

    /// <summary>
    /// User ID if available.
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>
    /// Request path or endpoint.
    /// </summary>
    public string? RequestPath { get; set; }

    /// <summary>
    /// HTTP method if applicable.
    /// </summary>
    public string? HttpMethod { get; set; }

    /// <summary>
    /// Additional properties for telemetry.
    /// </summary>
    public Dictionary<string, object>? AdditionalProperties { get; set; }

    /// <summary>
    /// Whether this exception represents a critical failure.
    /// </summary>
    public bool IsCritical { get; set; }

    /// <summary>
    /// Expected recovery time if known.
    /// </summary>
    public TimeSpan? ExpectedRecoveryTime { get; set; }
}

/// <summary>
/// Severity classification for exceptions.
/// </summary>
public enum ExceptionSeverity
{
    /// <summary>
    /// Low severity - information or expected errors.
    /// </summary>
    Low = 1,

    /// <summary>
    /// Medium severity - recoverable errors.
    /// </summary>
    Medium = 2,

    /// <summary>
    /// High severity - significant failures.
    /// </summary>
    High = 3,

    /// <summary>
    /// Critical severity - system-threatening failures.
    /// </summary>
    Critical = 4
}

/// <summary>
/// Classification of an exception for telemetry purposes.
/// </summary>
public sealed record ExceptionClassification
{
    /// <summary>
    /// Severity level of the exception.
    /// </summary>
    public ExceptionSeverity Severity { get; init; }

    /// <summary>
    /// Category of the exception (e.g., Database, Network, Authentication).
    /// </summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>
    /// Subcategory for more specific classification.
    /// </summary>
    public string? Subcategory { get; init; }

    /// <summary>
    /// Whether this exception type is expected/recoverable.
    /// </summary>
    public bool IsRecoverable { get; init; }

    /// <summary>
    /// Whether this exception should trigger alerts.
    /// </summary>
    public bool ShouldAlert { get; init; }

    /// <summary>
    /// Rate limiting key for similar exceptions.
    /// </summary>
    public string? RateLimitKey { get; init; }
}

/// <summary>
/// Statistics about recorded exceptions.
/// </summary>
public sealed record ExceptionStatistics
{
    /// <summary>
    /// Total number of exceptions recorded.
    /// </summary>
    public long TotalExceptions { get; init; }

    /// <summary>
    /// Number of critical exceptions.
    /// </summary>
    public long CriticalExceptions { get; init; }

    /// <summary>
    /// Exceptions by category.
    /// </summary>
    public IReadOnlyDictionary<string, long> ByCategory { get; init; } =
        new Dictionary<string, long>();

    /// <summary>
    /// Exceptions by severity.
    /// </summary>
    public IReadOnlyDictionary<ExceptionSeverity, long> BySeverity { get; init; } =
        new Dictionary<ExceptionSeverity, long>();

    /// <summary>
    /// Most common exception types.
    /// </summary>
    public IReadOnlyList<ExceptionTypeStatistic> MostCommonTypes { get; init; } =
        Array.Empty<ExceptionTypeStatistic>();

    /// <summary>
    /// Exception rate per minute.
    /// </summary>
    public double ExceptionsPerMinute { get; init; }

    /// <summary>
    /// Timestamp when statistics were collected.
    /// </summary>
    public DateTime CollectedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Statistics for a specific exception type.
/// </summary>
public sealed record ExceptionTypeStatistic
{
    /// <summary>
    /// Exception type name.
    /// </summary>
    public string ExceptionType { get; init; } = string.Empty;

    /// <summary>
    /// Number of occurrences.
    /// </summary>
    public long Count { get; init; }

    /// <summary>
    /// Most recent occurrence.
    /// </summary>
    public DateTime LastOccurred { get; init; }
}

/// <summary>
/// Record of a specific exception occurrence.
/// </summary>
public sealed record ExceptionRecord
{
    /// <summary>
    /// Unique identifier for this exception record.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Correlation ID for distributed tracing.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Exception type name.
    /// </summary>
    public string ExceptionType { get; init; } = string.Empty;

    /// <summary>
    /// Exception message.
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Classification of the exception.
    /// </summary>
    public ExceptionClassification Classification { get; init; } = new();

    /// <summary>
    /// Operation type that failed.
    /// </summary>
    public string? OperationType { get; init; }

    /// <summary>
    /// Request path or endpoint.
    /// </summary>
    public string? RequestPath { get; init; }

    /// <summary>
    /// When the exception occurred.
    /// </summary>
    public DateTime OccurredAt { get; init; }

    /// <summary>
    /// Additional telemetry properties.
    /// </summary>
    public IReadOnlyDictionary<string, object>? Properties { get; init; }

    /// <summary>
    /// Stack trace (sanitized).
    /// </summary>
    public string? StackTrace { get; init; }
}

/// <summary>
/// Configuration options for enhanced exception telemetry.
/// </summary>
public sealed class ExceptionTelemetryOptions
{
    /// <summary>
    /// Maximum number of recent exceptions to keep in memory.
    /// </summary>
    public int MaxRecentExceptions { get; set; } = 1000;

    /// <summary>
    /// Whether to capture stack traces for exceptions.
    /// </summary>
    public bool CaptureStackTraces { get; set; } = true;

    /// <summary>
    /// Maximum length of captured stack traces.
    /// </summary>
    public int MaxStackTraceLength { get; set; } = 4096;

    /// <summary>
    /// Whether to sanitize sensitive data from exception details.
    /// </summary>
    public bool SanitizeSensitiveData { get; set; } = true;

    /// <summary>
    /// Rate limiting window for similar exceptions.
    /// </summary>
    public TimeSpan RateLimitWindow { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Maximum occurrences per rate limit window.
    /// </summary>
    public int MaxOccurrencesPerWindow { get; set; } = 10;
}

/// <summary>
/// Enhanced exception telemetry implementation.
/// </summary>
internal sealed partial class EnhancedExceptionTelemetry : IEnhancedExceptionTelemetry, IDisposable
{
    private readonly ILogger<EnhancedExceptionTelemetry> _logger;
    private readonly ExceptionTelemetryOptions _options;

    private readonly ConcurrentQueue<ExceptionRecord> _recentExceptions = new();
    private readonly ConcurrentDictionary<string, ExceptionTypeCounter> _exceptionTypeCounts = new();
    private readonly ConcurrentDictionary<ExceptionSeverity, long> _severityCounts = new();
    private readonly ConcurrentDictionary<string, long> _categoryCounts = new();
    private readonly ConcurrentDictionary<string, RateLimitInfo> _rateLimiters = new();

    private long _totalExceptions;
    private long _criticalExceptions;
    private readonly Timer _cleanupTimer;
    private volatile bool _disposed;

    public EnhancedExceptionTelemetry(
        ILogger<EnhancedExceptionTelemetry> logger,
        IOptions<ExceptionTelemetryOptions> options)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        // Clean up old rate limiters periodically
        _cleanupTimer = new Timer(CleanupRateLimiters, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public void RecordException(Exception exception, ExceptionContext? context = null)
    {
        if (_disposed || exception == null) return;

        var correlationId = context?.CorrelationId ?? Activity.Current?.Id ?? GetCorrelationIdFromContext();
        var operationType = context?.OperationType ?? "Unknown";
        var additionalProperties = context?.AdditionalProperties ?? new Dictionary<string, object>();

        RecordException(exception, correlationId, operationType, additionalProperties);
    }

    public void RecordException(Exception exception, string correlationId, string operationType, IDictionary<string, object>? additionalProperties = null)
    {
        if (_disposed || exception == null) return;

        try
        {
            var classification = ClassifyException(exception);

            // Apply rate limiting for non-critical exceptions
            if (classification.Severity != ExceptionSeverity.Critical && ShouldRateLimit(classification, exception))
            {
                return;
            }

            var recordId = Guid.NewGuid().ToString("N");
            var properties = new Dictionary<string, object>();

            // Add correlation ID to Activity tags
            if (!string.IsNullOrEmpty(correlationId) && Activity.Current != null)
            {
                Activity.Current.SetTag("error.correlation_id", correlationId);
                Activity.Current.SetTag("error.type", exception.GetType().Name);
                Activity.Current.SetTag("error.category", classification.Category);
                Activity.Current.SetTag("error.severity", classification.Severity.ToString());
            }

            // Copy additional properties
            if (additionalProperties != null)
            {
                foreach (var kvp in additionalProperties)
                {
                    properties[kvp.Key] = kvp.Value;
                }
            }

            // Add standard properties
            properties["ExceptionId"] = recordId;
            properties["CorrelationId"] = correlationId;
            properties["OperationType"] = operationType;
            properties["Classification"] = FormatClassification(classification);

            // Create exception record
            var record = new ExceptionRecord
            {
                Id = recordId,
                CorrelationId = correlationId,
                ExceptionType = exception.GetType().Name,
                Message = SanitizeMessage(exception.Message),
                Classification = classification,
                OperationType = operationType,
                RequestPath = GetValueFromProperties(additionalProperties, "RequestPath"),
                OccurredAt = DateTime.UtcNow,
                Properties = properties,
                StackTrace = _options.CaptureStackTraces ? SanitizeStackTrace(exception.StackTrace) : null
            };

            // Update statistics
            Interlocked.Increment(ref _totalExceptions);

            if (classification.Severity == ExceptionSeverity.Critical)
            {
                Interlocked.Increment(ref _criticalExceptions);
            }

            _severityCounts.AddOrUpdate(classification.Severity, 1, (_, count) => count + 1);
            _categoryCounts.AddOrUpdate(classification.Category, 1, (_, count) => count + 1);

            var exceptionType = exception.GetType().Name;
            _exceptionTypeCounts.AddOrUpdate(
                exceptionType,
                new ExceptionTypeCounter { Count = 1, LastOccurred = DateTime.UtcNow },
                (_, existing) => new ExceptionTypeCounter
                {
                    Count = existing.Count + 1,
                    LastOccurred = DateTime.UtcNow
                });

            // Add to recent exceptions queue
            _recentExceptions.Enqueue(record);

            // Maintain queue size
            while (_recentExceptions.Count > _options.MaxRecentExceptions)
            {
                _recentExceptions.TryDequeue(out _);
            }

            // Log exception with enhanced context
            LogException(exception, record, classification);
        }
        catch (Exception ex)
        {
            // Don't let telemetry recording break the application
            TelemetryLog.ExceptionRecordingFailed(_logger, ex);
        }
    }

    public ExceptionStatistics GetStatistics()
    {
        if (_disposed) return new ExceptionStatistics();

        var now = DateTime.UtcNow;
        var oneMinuteAgo = now.Subtract(TimeSpan.FromMinutes(1));

        // Calculate exceptions per minute from recent exceptions
        var recentExceptionsCount = _recentExceptions.Count(e => e.OccurredAt >= oneMinuteAgo);
        var exceptionsPerMinute = (double)recentExceptionsCount;

        var mostCommonTypes = _exceptionTypeCounts
            .OrderByDescending(kvp => kvp.Value.Count)
            .Take(10)
            .Select(kvp => new ExceptionTypeStatistic
            {
                ExceptionType = kvp.Key,
                Count = kvp.Value.Count,
                LastOccurred = kvp.Value.LastOccurred
            })
            .ToList();

        return new ExceptionStatistics
        {
            TotalExceptions = Interlocked.Read(ref _totalExceptions),
            CriticalExceptions = Interlocked.Read(ref _criticalExceptions),
            ByCategory = new Dictionary<string, long>(_categoryCounts),
            BySeverity = new Dictionary<ExceptionSeverity, long>(_severityCounts),
            MostCommonTypes = mostCommonTypes,
            ExceptionsPerMinute = exceptionsPerMinute
        };
    }

    public IReadOnlyList<ExceptionRecord> GetRecentExceptions(int maxCount = 100, ExceptionSeverity? minSeverity = null)
    {
        if (_disposed) return Array.Empty<ExceptionRecord>();

        var exceptions = _recentExceptions.ToArray();

        if (minSeverity.HasValue)
        {
            exceptions = exceptions.Where(e => e.Classification.Severity >= minSeverity.Value).ToArray();
        }

        return exceptions
            .OrderByDescending(e => e.OccurredAt)
            .Take(maxCount)
            .ToList();
    }

    private static ExceptionClassification ClassifyException(Exception exception)
    {
        var exceptionType = exception.GetType();

        // Critical system exceptions
        if (exceptionType == typeof(OutOfMemoryException) ||
            exceptionType == typeof(StackOverflowException) ||
            exceptionType.Name.Contains("Fatal"))
        {
            return new ExceptionClassification
            {
                Severity = ExceptionSeverity.Critical,
                Category = "System",
                Subcategory = "Fatal",
                IsRecoverable = false,
                ShouldAlert = true,
                RateLimitKey = $"Critical.{exceptionType.Name}"
            };
        }

        // Database-related exceptions
        if (IsDbException(exceptionType))
        {
            var severity = exception.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                ? ExceptionSeverity.High
                : ExceptionSeverity.Medium;

            return new ExceptionClassification
            {
                Severity = severity,
                Category = "Database",
                Subcategory = GetDbExceptionSubcategory(exception),
                IsRecoverable = true,
                ShouldAlert = severity == ExceptionSeverity.High,
                RateLimitKey = $"Database.{exceptionType.Name}"
            };
        }

        // Network-related exceptions
        if (IsNetworkException(exceptionType))
        {
            return new ExceptionClassification
            {
                Severity = ExceptionSeverity.Medium,
                Category = "Network",
                IsRecoverable = true,
                ShouldAlert = false,
                RateLimitKey = $"Network.{exceptionType.Name}"
            };
        }

        // Authentication/Authorization exceptions
        if (IsAuthException(exceptionType))
        {
            return new ExceptionClassification
            {
                Severity = ExceptionSeverity.Low,
                Category = "Authentication",
                IsRecoverable = false,
                ShouldAlert = false,
                RateLimitKey = $"Auth.{exceptionType.Name}"
            };
        }

        // Default classification
        return new ExceptionClassification
        {
            Severity = ExceptionSeverity.Medium,
            Category = "Application",
            IsRecoverable = true,
            ShouldAlert = false,
            RateLimitKey = exceptionType.Name
        };
    }

    private static string FormatClassification(ExceptionClassification classification)
    {
        return string.Concat(
            "Severity=", classification.Severity,
            ";Category=", classification.Category,
            ";Subcategory=", classification.Subcategory ?? string.Empty,
            ";IsRecoverable=", classification.IsRecoverable,
            ";ShouldAlert=", classification.ShouldAlert,
            ";RateLimitKey=", classification.RateLimitKey ?? string.Empty);
    }

    private bool ShouldRateLimit(ExceptionClassification classification, Exception exception)
    {
        if (string.IsNullOrEmpty(classification.RateLimitKey)) return false;

        var now = DateTime.UtcNow;
        var rateLimitInfo = _rateLimiters.GetOrAdd(
            classification.RateLimitKey,
            _ => new RateLimitInfo { WindowStart = now, Count = 0 });

        lock (rateLimitInfo)
        {
            // Reset window if expired
            if (now - rateLimitInfo.WindowStart > _options.RateLimitWindow)
            {
                rateLimitInfo.WindowStart = now;
                rateLimitInfo.Count = 0;
            }

            rateLimitInfo.Count++;

            return rateLimitInfo.Count > _options.MaxOccurrencesPerWindow;
        }
    }

    private void LogException(Exception exception, ExceptionRecord record, ExceptionClassification classification)
    {
        var logLevel = classification.Severity switch
        {
            ExceptionSeverity.Critical => LogLevel.Critical,
            ExceptionSeverity.High => LogLevel.Error,
            ExceptionSeverity.Medium => LogLevel.Warning,
            _ => LogLevel.Information
        };

        TelemetryLog.ExceptionRecorded(_logger, logLevel, record.ExceptionType, record.Message,
            record.CorrelationId ?? "Unknown", classification.Category, exception);
    }

    private static bool IsDbException(Type exceptionType) =>
        exceptionType.Namespace?.Contains("Npgsql") == true ||
        exceptionType.Namespace?.Contains("Database") == true ||
        exceptionType.Name.Contains("Sql") ||
        exceptionType.Name.Contains("Database");

    private static bool IsNetworkException(Type exceptionType) =>
        exceptionType.Namespace?.StartsWith("System.Net", StringComparison.Ordinal) == true ||
        exceptionType.Name.Contains("Http") ||
        exceptionType.Name.Contains("Socket") ||
        exceptionType.Name.Contains("Network");

    private static bool IsAuthException(Type exceptionType) =>
        exceptionType.Name.Contains("Authentication") ||
        exceptionType.Name.Contains("Authorization") ||
        exceptionType.Name.Contains("Unauthorized") ||
        exceptionType.Name.Contains("Forbidden");

    private static string GetDbExceptionSubcategory(Exception exception)
    {
        if (exception.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return "Timeout";
        if (exception.Message.Contains("connection", StringComparison.OrdinalIgnoreCase))
            return "Connection";
        if (exception.Message.Contains("constraint", StringComparison.OrdinalIgnoreCase))
            return "Constraint";
        return "General";
    }

    private string SanitizeMessage(string? message)
    {
        if (string.IsNullOrEmpty(message) || !_options.SanitizeSensitiveData)
            return message ?? string.Empty;

        // Remove common sensitive patterns
        var sanitized = message;
        sanitized = System.Text.RegularExpressions.Regex.Replace(sanitized, @"\bpassword[=:]\s*\w+", "password=***", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        sanitized = System.Text.RegularExpressions.Regex.Replace(sanitized, @"\btoken[=:]\s*\w+", "token=***", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        sanitized = System.Text.RegularExpressions.Regex.Replace(sanitized, @"\bkey[=:]\s*\w+", "key=***", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return sanitized;
    }

    private string? SanitizeStackTrace(string? stackTrace)
    {
        if (string.IsNullOrEmpty(stackTrace)) return stackTrace;

        if (stackTrace.Length > _options.MaxStackTraceLength)
        {
            stackTrace = string.Concat(
                stackTrace.AsSpan(0, _options.MaxStackTraceLength),
                "... (truncated)");
        }

        return _options.SanitizeSensitiveData ? SanitizeMessage(stackTrace) : stackTrace;
    }

    private static string? GetValueFromProperties(IDictionary<string, object>? properties, string key)
    {
        if (properties?.TryGetValue(key, out var value) == true)
        {
            return value?.ToString();
        }
        return null;
    }

    private static string GetCorrelationIdFromContext()
    {
        // Try to get correlation ID from various sources
        return Activity.Current?.Id ??
               Guid.NewGuid().ToString("N")[..8]; // Fallback short ID
    }

    private void CleanupRateLimiters(object? state)
    {
        if (_disposed) return;

        var expiredKeys = new List<string>();
        var cutoff = DateTime.UtcNow.Subtract(_options.RateLimitWindow.Add(TimeSpan.FromMinutes(5)));

        foreach (var kvp in _rateLimiters)
        {
            if (kvp.Value.WindowStart < cutoff)
            {
                expiredKeys.Add(kvp.Key);
            }
        }

        foreach (var key in expiredKeys)
        {
            _rateLimiters.TryRemove(key, out _);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cleanupTimer?.Dispose();
        _rateLimiters.Clear();
        _exceptionTypeCounts.Clear();
        while (_recentExceptions.TryDequeue(out _)) { }
    }

    private sealed class ExceptionTypeCounter
    {
        public long Count { get; set; }
        public DateTime LastOccurred { get; set; }
    }

    private sealed class RateLimitInfo
    {
        public DateTime WindowStart { get; set; }
        public int Count { get; set; }
    }

    private static partial class TelemetryLog
    {
        [LoggerMessage(
            EventId = 9201,
            Message = "Exception recorded: {ExceptionType} - {Message} [CorrelationId={CorrelationId}, Category={Category}]")]
        public static partial void ExceptionRecorded(ILogger logger, LogLevel level, string exceptionType, string message, string correlationId, string category, Exception exception);

        [LoggerMessage(
            EventId = 9202,
            Level = LogLevel.Error,
            Message = "Failed to record exception telemetry")]
        public static partial void ExceptionRecordingFailed(ILogger logger, Exception exception);
    }
}
