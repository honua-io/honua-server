// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Infrastructure.Monitoring;

public interface IEnhancedExceptionTelemetry
{
    void RecordException(Exception exception, ExceptionContext? context = null);

    void RecordException(Exception exception, string correlationId, string operationType, IDictionary<string, object>? additionalProperties = null);

    ExceptionStatistics GetStatistics();

    IReadOnlyList<ExceptionRecord> GetRecentExceptions(int maxCount = 100, ExceptionSeverity? minSeverity = null);
}

public sealed class ExceptionContext
{
    public string? CorrelationId { get; set; }

    public string? OperationType { get; set; }

    public string? UserId { get; set; }

    public string? RequestPath { get; set; }

    public string? HttpMethod { get; set; }

    public Dictionary<string, object>? AdditionalProperties { get; set; }

    public bool IsCritical { get; set; }

    public TimeSpan? ExpectedRecoveryTime { get; set; }
}

public enum ExceptionSeverity
{
    Low = 1,

    Medium = 2,

    High = 3,

    Critical = 4
}

public sealed record ExceptionClassification
{
    public ExceptionSeverity Severity { get; init; }

    public string Category { get; init; } = string.Empty;

    public string? Subcategory { get; init; }

    public bool IsRecoverable { get; init; }

    public bool ShouldAlert { get; init; }

    public string? RateLimitKey { get; init; }
}

public sealed record ExceptionStatistics
{
    public long TotalExceptions { get; init; }

    public long CriticalExceptions { get; init; }

    public IReadOnlyDictionary<string, long> ByCategory { get; init; } =
        new Dictionary<string, long>();

    public IReadOnlyDictionary<ExceptionSeverity, long> BySeverity { get; init; } =
        new Dictionary<ExceptionSeverity, long>();

    public IReadOnlyList<ExceptionTypeStatistic> MostCommonTypes { get; init; } =
        Array.Empty<ExceptionTypeStatistic>();

    public double ExceptionsPerMinute { get; init; }

    public DateTime CollectedAt { get; init; } = DateTime.UtcNow;
}

public sealed record ExceptionTypeStatistic
{
    public string ExceptionType { get; init; } = string.Empty;

    public long Count { get; init; }

    public DateTime LastOccurred { get; init; }
}

public sealed record ExceptionRecord
{
    public string Id { get; init; } = string.Empty;

    public string? CorrelationId { get; init; }

    public string ExceptionType { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public ExceptionClassification Classification { get; init; } = new();

    public string? OperationType { get; init; }

    public string? RequestPath { get; init; }

    public DateTime OccurredAt { get; init; }

    public IReadOnlyDictionary<string, object>? Properties { get; init; }

    public string? StackTrace { get; init; }
}

public sealed class ExceptionTelemetryOptions
{
    public int MaxRecentExceptions { get; set; } = 1000;

    public bool CaptureStackTraces { get; set; } = true;

    public int MaxStackTraceLength { get; set; } = 4096;

    public bool SanitizeSensitiveData { get; set; } = true;

    public TimeSpan RateLimitWindow { get; set; } = TimeSpan.FromMinutes(1);

    public int MaxOccurrencesPerWindow { get; set; } = 10;
}

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
        // Intentional broad catch: exception telemetry is itself a side channel invoked while
        // handling another exception. A failure here must never mask or replace the original
        // exception flow, so it is logged and swallowed rather than propagated.
        catch (Exception ex) when (ex is not OutOfMemoryException)
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

        if (minSeverity is { } minSeverityValue)
        {
            exceptions = exceptions.Where(e => e.Classification.Severity >= minSeverityValue).ToArray();
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

        var cutoff = DateTime.UtcNow.Subtract(_options.RateLimitWindow.Add(TimeSpan.FromMinutes(5)));
        var expiredKeys = _rateLimiters
            .Where(kvp => kvp.Value.WindowStart < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

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
