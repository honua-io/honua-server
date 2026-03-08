// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.Infrastructure.Monitoring;

/// <summary>
/// Smart sampling rules that implement business logic for intelligent trace sampling.
/// Considers critical operations, error conditions, performance characteristics,
/// client priority, and geospatial complexity to make sampling decisions.
/// </summary>
public sealed partial class SmartSamplingRules : ISmartSamplingRules
{
    private readonly ILogger<SmartSamplingRules> _logger;
    private readonly HashSet<string> _vipClients;
    private readonly Dictionary<string, double> _operationBaselines;

    /// <summary>
    /// Initializes smart sampling rules with VIP client configuration.
    /// </summary>
    public SmartSamplingRules(
        ILogger<SmartSamplingRules> logger,
        IEnumerable<string>? vipClients = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _vipClients = new HashSet<string>(vipClients ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        _operationBaselines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Evaluates if an operation should be sampled based on smart sampling rules.
    /// </summary>
    /// <param name="context">The sampling context with operation details.</param>
    /// <returns>Sampling decision with reason and confidence score.</returns>
    public SmartSamplingDecision EvaluateSampling(SamplingContext context)
    {
        if (context == null)
        {
            return new SmartSamplingDecision(false, "No context provided", 0.0);
        }

        // Rule 1: Always sample error conditions
        if (HasErrorCondition(context))
        {
            return new SmartSamplingDecision(true, "Error condition detected", 1.0);
        }

        // Rule 2: Sample security-sensitive operations
        if (IsSecuritySensitive(context))
        {
            return new SmartSamplingDecision(true, "Security-sensitive operation", 0.8);
        }

        // Rule 3: Always sample large operations
        if (IsLargeOperation(context))
        {
            return new SmartSamplingDecision(true, "Large operation (high feature count)", 0.9);
        }

        // Rule 4: Always sample critical operations
        if (IsCriticalOperation(context))
        {
            return new SmartSamplingDecision(true, "Critical operation", 1.0);
        }

        // Rule 5: Always sample slow operations
        if (IsSlowOperation(context))
        {
            return new SmartSamplingDecision(true, "Slow operation detected", 0.9);
        }

        // Rule 6: Always sample VIP client operations
        if (IsVipClientOperation(context))
        {
            return new SmartSamplingDecision(true, "VIP client operation", 0.95);
        }

        // Rule 7: Sample geospatially complex operations
        if (IsGeospatiallyComplex(context))
        {
            return new SmartSamplingDecision(true, "Geospatially complex operation", 0.8);
        }

        // Rule 8: Sample operations exceeding performance baselines
        if (ExceedsPerformanceBaseline(context))
        {
            return new SmartSamplingDecision(true, "Performance baseline exceeded", 0.7);
        }

        // Rule 9: Sample new or untested code paths
        if (IsNewCodePath(context))
        {
            return new SmartSamplingDecision(true, "New or untested code path", 0.6);
        }

        // Rule 10: Sample operations during system stress
        if (IsSystemUnderStress(context))
        {
            // Paradoxically, we might want to reduce sampling under stress
            // But keep high-value operations sampled
            if (context.BusinessValue > 70)
            {
                return new SmartSamplingDecision(true, "High business value under system stress", 0.5);
            }
            return new SmartSamplingDecision(false, "System under stress, low business value", 0.3);
        }

        // No specific rules matched - use adaptive sampling
        return new SmartSamplingDecision(null, "No smart rules matched, defer to adaptive sampling", 0.0);
    }

    /// <summary>
    /// Updates performance baselines for operations based on historical data.
    /// </summary>
    /// <param name="operationName">The name of the operation.</param>
    /// <param name="baselineDurationMs">The baseline duration in milliseconds.</param>
    public void UpdatePerformanceBaseline(string operationName, double baselineDurationMs)
    {
        if (string.IsNullOrEmpty(operationName) || baselineDurationMs <= 0)
        {
            return;
        }

        _operationBaselines[operationName] = baselineDurationMs;

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            Log.PerformanceBaselineUpdated(_logger, operationName, baselineDurationMs);
        }
    }

    /// <summary>
    /// Adds a VIP client to the priority sampling list.
    /// </summary>
    /// <param name="clientId">The client identifier.</param>
    public void AddVipClient(string clientId)
    {
        if (string.IsNullOrEmpty(clientId))
        {
            return;
        }

        _vipClients.Add(clientId);
        Log.VipClientAdded(_logger, clientId);
    }

    /// <summary>
    /// Removes a VIP client from the priority sampling list.
    /// </summary>
    /// <param name="clientId">The client identifier.</param>
    public void RemoveVipClient(string clientId)
    {
        if (string.IsNullOrEmpty(clientId))
        {
            return;
        }

        if (_vipClients.Remove(clientId))
        {
            Log.VipClientRemoved(_logger, clientId);
        }
    }

    private static bool IsCriticalOperation(SamplingContext context)
    {
        var operationName = context.OperationName?.ToLowerInvariant() ?? string.Empty;

        // Authentication and authorization operations
        if (operationName.Contains("auth") || operationName.Contains("login") ||
            operationName.Contains("token") || operationName.Contains("permission"))
        {
            return true;
        }

        // Data modification operations
        if (operationName.Contains("create") || operationName.Contains("update") ||
            operationName.Contains("delete") || operationName.Contains("edit") ||
            operationName.Contains("insert") || operationName.Contains("modify"))
        {
            return true;
        }

        // Security and compliance operations
        if (operationName.Contains("security") || operationName.Contains("audit") ||
            operationName.Contains("compliance") || operationName.Contains("encrypt"))
        {
            return true;
        }

        // Transaction and payment operations
        if (operationName.Contains("transaction") || operationName.Contains("payment") ||
            operationName.Contains("billing") || operationName.Contains("invoice"))
        {
            return true;
        }

        return false;
    }

    private static bool HasErrorCondition(SamplingContext context)
    {
        // Check if there's an active exception
        if (context.Exception != null)
        {
            return true;
        }

        // Check if error tags are present
        if (context.ActivityTags?.ContainsKey("error") == true ||
            context.ActivityTags?.ContainsKey("exception.type") == true)
        {
            return true;
        }

        // Check for HTTP error status codes
        if (context.HttpStatusCode is >= 400 and < 600)
        {
            return true;
        }

        // Check for database error conditions
        if (context.DatabaseErrorCode.HasValue && context.DatabaseErrorCode.Value != 0)
        {
            return true;
        }

        return false;
    }

    private static bool IsLargeOperation(SamplingContext context)
    {
        // Large number of features being processed
        if (context.FeatureCount > 10000)
        {
            return true;
        }

        // Large data transfer
        if (context.DataSizeBytes > 10 * 1024 * 1024) // 10MB
        {
            return true;
        }

        // Bulk operations
        var operationName = context.OperationName?.ToLowerInvariant() ?? string.Empty;
        if (operationName.Contains("bulk") || operationName.Contains("batch") ||
            operationName.Contains("mass") || operationName.Contains("import"))
        {
            return true;
        }

        return false;
    }

    private bool IsSlowOperation(SamplingContext context)
    {
        // Operations taking longer than 5 seconds
        if (context.DurationMs > 5000)
        {
            return true;
        }

        // Operations exceeding their known baseline by 200%
        if (context.OperationName != null &&
            _operationBaselines.TryGetValue(context.OperationName, out var baseline))
        {
            return context.DurationMs > baseline * 2.0;
        }

        return false;
    }

    private bool IsVipClientOperation(SamplingContext context)
    {
        if (string.IsNullOrEmpty(context.ClientId))
        {
            return false;
        }

        return _vipClients.Contains(context.ClientId);
    }

    private static bool IsGeospatiallyComplex(SamplingContext context)
    {
        // Complex spatial operations
        var operationName = context.OperationName?.ToLowerInvariant() ?? string.Empty;
        if (operationName.Contains("spatial") || operationName.Contains("geospatial") ||
            operationName.Contains("geographic") || operationName.Contains("gis"))
        {
            return true;
        }

        // High coordinate complexity
        if (context.GeospatialComplexity > 1000)
        {
            return true;
        }

        // 3D or 4D operations
        if (context.SpatialDimensions > 2)
        {
            return true;
        }

        // High-precision calculations
        if (context.RequiresHighPrecision)
        {
            return true;
        }

        return false;
    }

    private bool ExceedsPerformanceBaseline(SamplingContext context)
    {
        if (context.OperationName == null || context.DurationMs <= 0)
        {
            return false;
        }

        if (!_operationBaselines.TryGetValue(context.OperationName, out var baseline))
        {
            return false;
        }

        // Exceeds baseline by 50% or more
        return context.DurationMs > baseline * 1.5;
    }

    private static bool IsSecuritySensitive(SamplingContext context)
    {
        // PII or sensitive data operations
        var operationName = context.OperationName?.ToLowerInvariant() ?? string.Empty;
        if (operationName.Contains("personal") || operationName.Contains("private") ||
            operationName.Contains("sensitive") || operationName.Contains("confidential"))
        {
            return true;
        }

        // Operations involving user data
        if (operationName.Contains("user") && (operationName.Contains("data") ||
            operationName.Contains("profile") || operationName.Contains("information")))
        {
            return true;
        }

        // High security risk context
        if (context.SecurityRiskLevel?.ToLowerInvariant() is "high" or "critical")
        {
            return true;
        }

        return false;
    }

    private static bool IsNewCodePath(SamplingContext context)
    {
        // Operations marked as experimental or new
        var operationName = context.OperationName?.ToLowerInvariant() ?? string.Empty;
        if (operationName.Contains("experimental") || operationName.Contains("beta") ||
            operationName.Contains("preview") || operationName.Contains("test"))
        {
            return true;
        }

        // New API versions
        if (context.ApiVersion?.StartsWith("v1.", StringComparison.OrdinalIgnoreCase) == false &&
            context.ApiVersion?.StartsWith("beta", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        return false;
    }

    private static bool IsSystemUnderStress(SamplingContext context)
    {
        // High CPU usage
        if (context.SystemCpuUsage > 80)
        {
            return true;
        }

        // High memory usage
        if (context.SystemMemoryUsage > 85)
        {
            return true;
        }

        // High active request count
        if (context.ActiveRequests > 1000)
        {
            return true;
        }

        // Database connection pool exhaustion
        if (context.DatabaseConnectionPoolUsage > 90)
        {
            return true;
        }

        return false;
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 1,
            Level = LogLevel.Debug,
            Message = "Updated performance baseline for {OperationName}: {BaselineDurationMs}ms")]
        public static partial void PerformanceBaselineUpdated(
            ILogger logger,
            string operationName,
            double baselineDurationMs);

        [LoggerMessage(
            EventId = 2,
            Level = LogLevel.Information,
            Message = "Added VIP client: {ClientId}")]
        public static partial void VipClientAdded(ILogger logger, string clientId);

        [LoggerMessage(
            EventId = 3,
            Level = LogLevel.Information,
            Message = "Removed VIP client: {ClientId}")]
        public static partial void VipClientRemoved(ILogger logger, string clientId);
    }
}

/// <summary>
/// Interface for smart sampling rule evaluation.
/// </summary>
public interface ISmartSamplingRules
{
    /// <summary>
    /// Evaluates if an operation should be sampled based on smart rules.
    /// </summary>
    SmartSamplingDecision EvaluateSampling(SamplingContext context);

    /// <summary>
    /// Updates performance baselines for operations.
    /// </summary>
    void UpdatePerformanceBaseline(string operationName, double baselineDurationMs);

    /// <summary>
    /// Adds a VIP client to priority sampling.
    /// </summary>
    void AddVipClient(string clientId);

    /// <summary>
    /// Removes a VIP client from priority sampling.
    /// </summary>
    void RemoveVipClient(string clientId);
}

/// <summary>
/// Context information for smart sampling decisions.
/// </summary>
public sealed record SamplingContext
{
    /// <summary>Name of the operation being sampled.</summary>
    public string? OperationName { get; init; }

    /// <summary>Duration of the operation in milliseconds.</summary>
    public double DurationMs { get; init; }

    /// <summary>Number of features being processed.</summary>
    public int FeatureCount { get; init; }

    /// <summary>Size of data being transferred in bytes.</summary>
    public long DataSizeBytes { get; init; }

    /// <summary>Client identifier for VIP detection.</summary>
    public string? ClientId { get; init; }

    /// <summary>User identifier for personalization.</summary>
    public string? UserId { get; init; }

    /// <summary>Exception if operation failed.</summary>
    public Exception? Exception { get; init; }

    /// <summary>HTTP status code if applicable.</summary>
    public int? HttpStatusCode { get; init; }

    /// <summary>Database error code if applicable.</summary>
    public int? DatabaseErrorCode { get; init; }

    /// <summary>Geospatial complexity score.</summary>
    public int GeospatialComplexity { get; init; }

    /// <summary>Number of spatial dimensions (2D, 3D, 4D).</summary>
    public int SpatialDimensions { get; init; }

    /// <summary>Whether high-precision calculations are required.</summary>
    public bool RequiresHighPrecision { get; init; }

    /// <summary>Business value score (0-100).</summary>
    public int BusinessValue { get; init; }

    /// <summary>Security risk level.</summary>
    public string? SecurityRiskLevel { get; init; }

    /// <summary>API version being used.</summary>
    public string? ApiVersion { get; init; }

    /// <summary>Current system CPU usage percentage.</summary>
    public double SystemCpuUsage { get; init; }

    /// <summary>Current system memory usage percentage.</summary>
    public double SystemMemoryUsage { get; init; }

    /// <summary>Number of active requests.</summary>
    public int ActiveRequests { get; init; }

    /// <summary>Database connection pool usage percentage.</summary>
    public double DatabaseConnectionPoolUsage { get; init; }

    /// <summary>Activity tags for additional context.</summary>
    public IReadOnlyDictionary<string, object?>? ActivityTags { get; init; }
}

/// <summary>
/// Result of smart sampling rule evaluation.
/// </summary>
/// <param name="ShouldSample">Whether the operation should be sampled. Null means defer to adaptive sampling.</param>
/// <param name="Reason">Reason for the sampling decision.</param>
/// <param name="ConfidenceScore">Confidence score of the decision (0.0-1.0).</param>
public readonly record struct SmartSamplingDecision(bool? ShouldSample, string Reason, double ConfidenceScore);
